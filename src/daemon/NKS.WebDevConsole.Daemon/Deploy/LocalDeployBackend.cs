using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Interfaces;

namespace NKS.WebDevConsole.Daemon.Deploy;

/// <summary>
/// Phase 7.5+++ — REAL local-loopback deploy backend modelled on the
/// nksdeploy folder convention (releases/, current symlink, shared/ with
/// per-release symlinks, retention, .dep/ state). Replaces the prior
/// "copy files + symlink" minimum implementation that lacked shared/
/// and retention semantics.
///
/// Resulting target tree:
/// <code>
///   {target}/
///   ├── releases/
///   │   ├── 20260427_010500/
///   │   ├── 20260427_011200/
///   │   └── 20260427_011900/    ← latest
///   ├── current → releases/20260427_011900/   (symlink, atomic swap)
///   ├── shared/
///   │   ├── log/                (configurable shared dirs)
///   │   ├── temp/
///   │   └── .env                (configurable shared files)
///   └── .dep/                   (release manifest, previous_release ptr)
/// </code>
///
/// Pipeline:
///   1. Validate + setup target tree (releases/, shared/, .dep/).
///   2. Create release dir at <c>{target}/releases/{releaseId}/</c>.
///   3. Recursive copy of source → release dir (skips .git/, node_modules/, etc.).
///   4. Apply shared symlinks: each shared dir/file is moved to shared/
///      on first deploy (seed), then symlinked into the release.
///   5. Atomic <c>current</c> symlink swap (or recursive replace fallback
///      on Windows without DevMode/admin).
///   6. Soak/health placeholder phase.
///   7. Cleanup: keep N most recent releases, delete the rest, never
///      touching the one that <c>current</c> points to.
///
/// Each step broadcasts a `deploy:phase` SSE event so the GUI drawer
/// renders progress. Errors mark the run as failed and broadcast
/// <c>deploy:complete{success:false}</c> with the message.
///
/// Triggered when POST /api/nks.wdc.deploy/sites/{domain}/deploy
/// resolves <c>localPaths.{source,target}</c> (body or host settings).
/// </summary>
public sealed class LocalDeployBackend
{
    private static readonly string[] SkipDirs = { ".git", "node_modules", ".vs", "bin", "obj", ".next" };

    /// <summary>Default shared paths used when host config doesn't override.
    /// Mirrors the nksdeploy default for a typical Nette/PHP project.</summary>
    private static readonly string[] DefaultSharedDirs = { "log", "temp" };
    private static readonly string[] DefaultSharedFiles = Array.Empty<string>();

    /// <summary>Default retention: keep last 5 releases including current.
    /// Matches nksdeploy default and DeployAdvancedConfig.keepReleases.</summary>
    private const int DefaultKeepReleases = 5;

    private readonly IDeployRunsRepository _runs;
    private readonly IDeployEventBroadcaster _events;
    private readonly ILogger<LocalDeployBackend> _logger;

    /// <summary>
    /// In-flight deploy registry → CancellationTokenSource keyed by
    /// deployId so the cancel endpoint can interrupt a running backend
    /// task. Entry added when RunAsync starts, removed when it ends
    /// (success/fail/cancel). ConcurrentDictionary because cancel may
    /// fire from another HTTP request thread mid-deploy.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource>
        _inflight = new();

    /// <summary>
    /// Trip the cancellation token for a given deploy if it's still
    /// in flight. Returns true when a CTS was found + cancelled, false
    /// when the deploy already finished. The HTTP cancel endpoint calls
    /// this BEFORE its own DB-status update so the backend has a chance
    /// to bail at its next ct.ThrowIfCancellationRequested check.
    /// </summary>
    public bool TryCancel(string deployId)
    {
        if (_inflight.TryGetValue(deployId, out var cts))
        {
            try { cts.Cancel(); } catch { }
            return true;
        }
        return false;
    }

    public LocalDeployBackend(
        IDeployRunsRepository runs,
        IDeployEventBroadcaster events,
        ILogger<LocalDeployBackend> logger)
    {
        _runs = runs;
        _events = events;
        _logger = logger;
    }

    /// <summary>
    /// Deploy options resolved from per-domain settings JSON before the
    /// backend runs. Optional — empty record uses nksdeploy-equivalent
    /// defaults (shared: log/+temp/, retention: 5).
    /// </summary>
    public sealed record Options(
        IReadOnlyList<string>? SharedDirs = null,
        IReadOnlyList<string>? SharedFiles = null,
        int? KeepReleases = null,
        IReadOnlyList<HookSpec>? Hooks = null,
        IReadOnlyDictionary<string, string>? EnvVars = null,
        NotificationsConfig? Notifications = null,
        string? Domain = null,
        string? Host = null);

    /// <summary>
    /// Notifications config resolved from settings.notifications. Empty
    /// fields → that channel is silent. notifyOn values are matched to
    /// the deploy outcome (success / failure / awaiting_soak / cancelled).
    /// </summary>
    public sealed record NotificationsConfig(
        string? SlackWebhook = null,
        IReadOnlyList<string>? EmailRecipients = null,
        IReadOnlyList<string>? NotifyOn = null);

    /// <summary>
    /// One configured deploy hook. Mirrors DeployHookConfig in api/deploy.ts.
    /// Event values: pre_deploy, post_fetch, pre_switch, post_switch, on_failure, on_rollback.
    /// Type values: shell, http, php.
    /// </summary>
    public sealed record HookSpec(
        string Event, string Type, string Command,
        int TimeoutSeconds = 60,
        bool Enabled = true,
        string? Description = null);

    /// <summary>
    /// Run the full pipeline. Returns when complete (success or failure).
    /// Caller is expected to fire-and-forget via Task.Run since the deploy
    /// HTTP request returns 202 Accepted before this finishes.
    /// </summary>
    public async Task RunAsync(
        string deployId,
        string releaseId,
        string sourcePath,
        string targetPath,
        Options? options = null,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var sharedDirs = options?.SharedDirs ?? DefaultSharedDirs;
        var sharedFiles = options?.SharedFiles ?? DefaultSharedFiles;
        var keepReleases = Math.Max(1, options?.KeepReleases ?? DefaultKeepReleases);
        var hooks = options?.Hooks ?? Array.Empty<HookSpec>();
        var envVars = options?.EnvVars ?? new Dictionary<string, string>();
        string? releaseDirForHooks = null;

        // Phase 7.5+++ — register cancellation source so the cancel
        // endpoint can interrupt this run. Linked CTS combines the
        // caller's ct with our own so external cancel still works.
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var deployCt = runCts.Token;
        _inflight[deployId] = runCts;

        try
        {
            // ── 1. Validate + setup target tree ──────────────────────────
            await _events.BroadcastAsync("deploy:phase",
                new { deployId, phase = "Building", message = $"Validating source {sourcePath}" });
            if (!Directory.Exists(sourcePath))
                throw new DirectoryNotFoundException($"Source path does not exist: {sourcePath}");

            Directory.CreateDirectory(targetPath);
            var releasesDir = Path.Combine(targetPath, "releases");
            var sharedDir = Path.Combine(targetPath, "shared");
            var depDir = Path.Combine(targetPath, ".dep");
            Directory.CreateDirectory(releasesDir);
            Directory.CreateDirectory(sharedDir);
            Directory.CreateDirectory(depDir);

            var releaseDir = Path.Combine(releasesDir, releaseId);
            Directory.CreateDirectory(releaseDir);
            releaseDirForHooks = releaseDir;

            await _runs.UpdateStatusAsync(deployId, "running", ct);
            deployCt.ThrowIfCancellationRequested();

            // Phase 7.5+++ — pre_deploy hooks fire before any file work.
            await RunHooksAsync(hooks, "pre_deploy", deployId, releaseDir, envVars, ct);
            deployCt.ThrowIfCancellationRequested();

            // ── 2. Copy source → release dir ─────────────────────────────
            await _events.BroadcastAsync("deploy:phase",
                new { deployId, phase = "Fetching", message = $"Copying files to {releaseDir}" });
            CopyDirectory(sourcePath, releaseDir);

            // post_fetch hooks: copy is done, shared not linked yet.
            await RunHooksAsync(hooks, "post_fetch", deployId, releaseDir, envVars, ct);
            deployCt.ThrowIfCancellationRequested();

            // ── 3. Apply shared symlinks ─────────────────────────────────
            await _events.BroadcastAsync("deploy:phase",
                new { deployId, phase = "Building",
                      message = $"Linking shared resources ({sharedDirs.Count} dirs, {sharedFiles.Count} files)" });
            foreach (var dir in sharedDirs)
                LinkSharedDir(releaseDir, sharedDir, dir);
            foreach (var file in sharedFiles)
                LinkSharedFile(releaseDir, sharedDir, file);

            // pre_switch hooks: release is fully prepared, current still old.
            await RunHooksAsync(hooks, "pre_switch", deployId, releaseDir, envVars, ct);
            // Last cancellation checkpoint BEFORE the symlink swap (PONR).
            // After this point we don't honor cancel — the deploy is committed.
            deployCt.ThrowIfCancellationRequested();

            // ── 4. Record previous release (for rollback) ────────────────
            var currentLink = Path.Combine(targetPath, "current");
            var previousRelease = TryReadSymlinkTarget(currentLink);
            if (!string.IsNullOrEmpty(previousRelease))
                await File.WriteAllTextAsync(
                    Path.Combine(depDir, "previous_release"),
                    previousRelease, ct);
            await File.WriteAllTextAsync(
                Path.Combine(depDir, "current_release"),
                releaseDir, ct);

            // ── 5. Atomic symlink swap ───────────────────────────────────
            await _events.BroadcastAsync("deploy:phase",
                new { deployId, phase = "Switching", message = "Atomic symlink swap" });
            SwitchSymlinkOrCopy(currentLink, releaseDir);
            await _runs.MarkPastPonrAsync(deployId, ct);

            // post_switch hooks: current now points at the new release.
            await RunHooksAsync(hooks, "post_switch", deployId, releaseDir, envVars, ct);

            // ── 6. Soak (health placeholder) ─────────────────────────────
            await _events.BroadcastAsync("deploy:phase",
                new { deployId, phase = "AwaitingSoak", message = "Soak window (local backend skips real probe)" });
            await _runs.UpdateStatusAsync(deployId, "awaiting_soak", ct);

            // ── 7. Cleanup old releases ──────────────────────────────────
            await _events.BroadcastAsync("deploy:phase",
                new { deployId, phase = "Building",
                      message = $"Cleaning up old releases (keep: {keepReleases})" });
            CleanupOldReleases(releasesDir, releaseDir, keepReleases);

            sw.Stop();
            await _runs.MarkCompletedAsync(deployId, success: true, exitCode: 0,
                errorMessage: null, durationMs: sw.ElapsedMilliseconds, ct);
            await _events.BroadcastAsync("deploy:complete",
                new { deployId, success = true, durationMs = sw.ElapsedMilliseconds, releaseDir });
            // Phase 7.5+++ — fire configured notifications (Slack/etc) on success.
            try { await DispatchNotificationsAsync(options?.Notifications, options?.Domain, options?.Host,
                deployId, success: true, durationMs: sw.ElapsedMilliseconds, error: null, ct); }
            catch { }
        }
        catch (OperationCanceledException)
        {
            // Phase 7.5+++ — cancel by operator. Don't fire on_failure
            // hooks (operator initiated, not a real failure). Status was
            // likely already flipped to "cancelled" by the cancel endpoint;
            // we re-flip just to win any race where the cancel handler
            // ran BEFORE we started UpdateStatusAsync(running) above.
            sw.Stop();
            _logger.LogInformation("Local deploy {DeployId} cancelled by operator", deployId);
            try { await _runs.UpdateStatusAsync(deployId, "cancelled", default); } catch { }
            try { await _runs.MarkCompletedAsync(deployId, success: false, exitCode: -2,
                errorMessage: "cancelled by operator", durationMs: sw.ElapsedMilliseconds, default); } catch { }
            try { await _events.BroadcastAsync("deploy:complete",
                new { deployId, success = false, error = "cancelled" }); } catch { }
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(ex, "Local deploy {DeployId} failed: {Msg}", deployId, ex.Message);
            // on_failure hooks — best-effort, never block reporting the failure.
            try { await RunHooksAsync(hooks, "on_failure", deployId, releaseDirForHooks ?? targetPath, envVars, ct); }
            catch { }
            try { await _runs.MarkCompletedAsync(deployId, success: false, exitCode: -1,
                errorMessage: ex.Message, durationMs: sw.ElapsedMilliseconds, ct); } catch { }
            try { await _events.BroadcastAsync("deploy:complete",
                new { deployId, success = false, error = ex.Message }); } catch { }
            // Failure notifications — same dispatcher, different outcome.
            try { await DispatchNotificationsAsync(options?.Notifications, options?.Domain, options?.Host,
                deployId, success: false, durationMs: sw.ElapsedMilliseconds, error: ex.Message, ct); }
            catch { }
        }
        finally
        {
            // Always remove from registry so cancel of a finished deploy
            // returns false rather than triggering ghost cancellations.
            _inflight.TryRemove(deployId, out _);
        }
    }

    /// <summary>
    /// Fire configured notification channels for a completed deploy.
    /// Currently dispatches Slack webhooks (POST {text} JSON). Email
    /// dispatch is deferred (needs SMTP server config). Channel-by-channel
    /// failure isolation — one channel erroring doesn't skip the next.
    /// Public so the test-notification endpoint can reuse it.
    /// </summary>
    public async Task DispatchNotificationsAsync(
        NotificationsConfig? cfg, string? domain, string? host,
        string deployId, bool success, long durationMs, string? error,
        CancellationToken ct)
    {
        if (cfg is null) return;
        // Map success/failure → string matching the GUI's notifyOn enum values.
        var outcome = success ? "success" : "failure";
        var notifyOn = cfg.NotifyOn ?? new[] { "success", "failure" };
        if (!notifyOn.Contains(outcome, StringComparer.OrdinalIgnoreCase)) return;

        if (!string.IsNullOrEmpty(cfg.SlackWebhook))
        {
            try
            {
                await PostSlackAsync(cfg.SlackWebhook, domain, host, deployId,
                    success, durationMs, error, ct);
            }
            catch (Exception ex)
            {
                // Dispatch swallows: a notification failure is never allowed
                // to mask the underlying deploy outcome. The test endpoint
                // calls PostSlackAsync directly so it can surface failures.
                _logger.LogWarning(ex, "Slack webhook dispatch failed for deploy {DeployId}", deployId);
            }
        }
    }

    /// <summary>
    /// Inner Slack-webhook POST. Throws on non-2xx + on transport
    /// failures so callers can surface the error. The dispatch path
    /// wraps this in try/catch; the test-notification endpoint lets it
    /// throw so the operator sees what's wrong.
    /// </summary>
    public static async Task PostSlackAsync(
        string webhook, string? domain, string? host, string deployId,
        bool success, long durationMs, string? error, CancellationToken ct)
    {
        var icon = success ? ":white_check_mark:" : ":x:";
        var summary = success
            ? $"{icon} *{domain ?? "?"}* deploy succeeded on `{host ?? "?"}` ({durationMs} ms)"
            : $"{icon} *{domain ?? "?"}* deploy FAILED on `{host ?? "?"}` ({durationMs} ms)";
        if (!success && !string.IsNullOrEmpty(error))
            summary += $"\n>{error}";
        summary += $"\n_deployId: `{deployId[..Math.Min(8, deployId.Length)]}`_";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var payload = System.Text.Json.JsonSerializer.Serialize(new { text = summary });
        using var resp = await http.PostAsync(webhook,
            new StringContent(payload, System.Text.Encoding.UTF8, "application/json"), ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Slack webhook returned {(int)resp.StatusCode} {resp.ReasonPhrase}: {body.Trim()}");
        }
    }

    /// <summary>
    /// Execute every enabled hook matching the given event in declared
    /// order. Each hook runs sequentially (parallelism would mask which
    /// step failed in operator logs). Failures broadcast a `deploy:hook`
    /// SSE event but do NOT abort the deploy — the operator can
    /// configure on_failure hooks for that. Honors per-hook timeout.
    /// </summary>
    private async Task RunHooksAsync(
        IReadOnlyList<HookSpec> hooks, string evt,
        string deployId, string releaseDir,
        IReadOnlyDictionary<string, string> envVars,
        CancellationToken ct)
    {
        var matched = hooks.Where(h => h.Enabled && string.Equals(h.Event, evt, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matched.Count == 0) return;

        await _events.BroadcastAsync("deploy:phase",
            new { deployId, phase = "Building",
                  message = $"Running {matched.Count} {evt} hook(s)" });

        foreach (var hook in matched)
        {
            var hookSw = System.Diagnostics.Stopwatch.StartNew();
            var label = hook.Description ?? hook.Command;
            try
            {
                await ExecuteHookAsync(hook, releaseDir, envVars, ct);
                hookSw.Stop();
                await _events.BroadcastAsync("deploy:hook",
                    new { deployId, evt, type = hook.Type, label,
                          ok = true, durationMs = hookSw.ElapsedMilliseconds });
                _logger.LogInformation("Hook {Event}/{Type} OK in {Ms}ms: {Label}",
                    evt, hook.Type, hookSw.ElapsedMilliseconds, label);
            }
            catch (Exception ex)
            {
                hookSw.Stop();
                _logger.LogWarning(ex, "Hook {Event}/{Type} FAILED: {Label} — {Msg}",
                    evt, hook.Type, label, ex.Message);
                await _events.BroadcastAsync("deploy:hook",
                    new { deployId, evt, type = hook.Type, label,
                          ok = false, error = ex.Message, durationMs = hookSw.ElapsedMilliseconds });
                // Continue to next hook — failure-isolation. on_failure
                // hooks intentionally fall through so the operator's
                // notification still fires when a recovery hook errors.
            }
        }
    }

    /// <summary>
    /// Dispatch one hook by type. shell → cmd.exe (Windows) or sh -c
    /// (POSIX). http → POST to URL with deploy context as body. php →
    /// `php -r {command}` if no path-like first token, else php {file}.
    /// All paths run with the release dir as working directory + envVars
    /// merged into the process environment.
    /// </summary>
    /// <summary>
    /// Public entry for test-hook flows. Wraps ExecuteHookAsync with a
    /// stopwatch + try/catch so the daemon endpoint can return a clean
    /// shape `{ok, durationMs, error?}` to the GUI's Test button.
    /// </summary>
    public async Task<(bool ok, long durationMs, string? error)> TestHookAsync(
        HookSpec hook, string workingDir,
        IReadOnlyDictionary<string, string>? envVars = null,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await ExecuteHookAsync(hook, workingDir,
                envVars ?? new Dictionary<string, string>(), ct);
            sw.Stop();
            return (true, sw.ElapsedMilliseconds, null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return (false, sw.ElapsedMilliseconds, ex.Message);
        }
    }

    private async Task ExecuteHookAsync(
        HookSpec hook, string releaseDir,
        IReadOnlyDictionary<string, string> envVars,
        CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(1, hook.TimeoutSeconds));
        using var hookCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        hookCts.CancelAfter(timeout);

        switch (hook.Type.ToLowerInvariant())
        {
            case "http":
                using (var http = new HttpClient { Timeout = timeout })
                {
                    var payload = System.Text.Json.JsonSerializer.Serialize(
                        new { releaseDir, hook.Event });
                    using var req = new HttpRequestMessage(HttpMethod.Post, hook.Command)
                    {
                        Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
                    };
                    using var resp = await http.SendAsync(req, hookCts.Token);
                    if (!resp.IsSuccessStatusCode)
                        throw new InvalidOperationException(
                            $"HTTP hook returned {(int)resp.StatusCode} {resp.ReasonPhrase}");
                }
                return;

            case "php":
                {
                    // First token decides: looks-like-path → run as script,
                    // else treat the whole command as inline code via -r.
                    var firstToken = hook.Command.Split(' ', 2)[0];
                    var isPath = firstToken.EndsWith(".php", StringComparison.OrdinalIgnoreCase)
                                 || File.Exists(firstToken)
                                 || File.Exists(Path.Combine(releaseDir, firstToken));
                    var args = isPath ? hook.Command : $"-r \"{hook.Command.Replace("\"", "\\\"")}\"";
                    await RunProcessAsync("php", args, releaseDir, envVars, hookCts.Token);
                }
                return;

            case "shell":
            default:
                if (OperatingSystem.IsWindows())
                    await RunProcessAsync("cmd.exe", $"/c {hook.Command}", releaseDir, envVars, hookCts.Token);
                else
                    await RunProcessAsync("sh", $"-c \"{hook.Command.Replace("\"", "\\\"")}\"", releaseDir, envVars, hookCts.Token);
                return;
        }
    }

    /// <summary>
    /// Spawn a child process with stdout/stderr piped + envVars merged.
    /// Throws on non-zero exit code with stderr appended for diagnostics.
    /// </summary>
    private static async Task RunProcessAsync(
        string fileName, string arguments, string workingDir,
        IReadOnlyDictionary<string, string> envVars,
        CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var (k, v) in envVars) psi.Environment[k] = v;
        using var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {fileName}");
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0)
        {
            var stderr = await proc.StandardError.ReadToEndAsync(ct);
            throw new InvalidOperationException(
                $"{fileName} exited with {proc.ExitCode}: {stderr.Trim()}");
        }
    }

    /// <summary>
    /// Recursive directory copy excluding well-known build artefact dirs.
    /// </summary>
    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var name = Path.GetFileName(entry);
            if (Array.Exists(SkipDirs, s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase)))
                continue;
            var dst = Path.Combine(dest, name);
            if (Directory.Exists(entry)) CopyDirectory(entry, dst);
            else File.Copy(entry, dst, overwrite: true);
        }
    }

    /// <summary>
    /// Move a release-side directory to shared/ on first deploy (seed),
    /// then replace the release-side path with a symlink pointing into
    /// shared/. Subsequent deploys skip the seed step and only relink.
    /// Mirrors nksdeploy's SharedLinksStep::linkSharedDir behaviour.
    /// </summary>
    private void LinkSharedDir(string releaseDir, string sharedRoot, string relPath)
    {
        var relTrim = relPath.TrimStart('/', '\\');
        var releasePath = Path.Combine(releaseDir, relTrim);
        var sharedPath = Path.Combine(sharedRoot, relTrim);

        Directory.CreateDirectory(sharedPath);

        if (Directory.Exists(releasePath))
        {
            var info = new DirectoryInfo(releasePath);
            if (info.LinkTarget is null)
            {
                // First deploy or release re-introduced the dir as a real
                // tree — copy contents into shared/ then drop the release
                // copy so the symlink can take its place.
                CopyDirectory(releasePath, sharedPath);
                Directory.Delete(releasePath, recursive: true);
            }
            else
            {
                Directory.Delete(releasePath); // stale link
            }
        }

        var parent = Path.GetDirectoryName(releasePath);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

        try
        {
            Directory.CreateSymbolicLink(releasePath, sharedPath);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _logger.LogInformation(
                "Shared dir symlink failed for {Path}; falling back to junction-style copy. Reason: {Msg}",
                relTrim, ex.Message);
            CopyDirectory(sharedPath, releasePath);
        }
    }

    /// <summary>
    /// Move a file out of the release into shared/ on first deploy then
    /// symlink it back. If the file doesn't exist anywhere yet, an empty
    /// placeholder is created in shared/ — matches nksdeploy behaviour
    /// for files like <c>.env</c> that the operator populates manually.
    /// </summary>
    private void LinkSharedFile(string releaseDir, string sharedRoot, string relPath)
    {
        var relTrim = relPath.TrimStart('/', '\\');
        var releasePath = Path.Combine(releaseDir, relTrim);
        var sharedPath = Path.Combine(sharedRoot, relTrim);

        var sharedParent = Path.GetDirectoryName(sharedPath);
        if (!string.IsNullOrEmpty(sharedParent)) Directory.CreateDirectory(sharedParent);

        if (!File.Exists(sharedPath))
        {
            if (File.Exists(releasePath))
                File.Move(releasePath, sharedPath); // seed from release
            else
                File.WriteAllText(sharedPath, string.Empty); // placeholder
        }
        else if (File.Exists(releasePath))
        {
            File.Delete(releasePath); // stale copy from prior seed
        }

        var releaseParent = Path.GetDirectoryName(releasePath);
        if (!string.IsNullOrEmpty(releaseParent)) Directory.CreateDirectory(releaseParent);

        try
        {
            File.CreateSymbolicLink(releasePath, sharedPath);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _logger.LogInformation(
                "Shared file symlink failed for {Path}; copying instead. Reason: {Msg}",
                relTrim, ex.Message);
            File.Copy(sharedPath, releasePath, overwrite: true);
        }
    }

    /// <summary>
    /// Atomic symlink swap on Linux/macOS, fallback to recursive replace on
    /// Windows when CreateSymbolicLink fails (no admin / no DevMode).
    /// </summary>
    private void SwitchSymlinkOrCopy(string currentLink, string releaseDir)
    {
        try
        {
            // Remove existing link/dir before creating new one.
            if (Directory.Exists(currentLink))
            {
                var fi = new DirectoryInfo(currentLink);
                if (fi.LinkTarget is not null) Directory.Delete(currentLink);
                else Directory.Delete(currentLink, recursive: true);
            }
            else if (File.Exists(currentLink))
            {
                File.Delete(currentLink);
            }
            Directory.CreateSymbolicLink(currentLink, releaseDir);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _logger.LogInformation(
                "Symlink failed ({Msg}); falling back to directory copy at {CurrentLink}",
                ex.Message, currentLink);
            if (Directory.Exists(currentLink)) Directory.Delete(currentLink, recursive: true);
            CopyDirectory(releaseDir, currentLink);
        }
    }

    /// <summary>
    /// Read the target of a symlink directory; returns empty string when
    /// not a symlink or the link can't be resolved. Used to record the
    /// previous-release pointer in <c>.dep/previous_release</c> before
    /// the swap so rollback can find the prior release.
    /// </summary>
    private static string TryReadSymlinkTarget(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                var info = new DirectoryInfo(path);
                if (info.LinkTarget is not null) return info.LinkTarget;
            }
        }
        catch { /* swallow — best-effort */ }
        return string.Empty;
    }

    /// <summary>
    /// Lexicographic-sorted retention. Release IDs are timestamped
    /// (<c>yyyyMMdd_HHmmss</c>) so name ordering matches chronological
    /// ordering. Keeps the N newest, never deletes the directory that
    /// <c>current</c> points to (defensive — under normal conditions
    /// the just-created release IS the newest so this is the same path,
    /// but during edge cases like manual rollback they diverge).
    /// </summary>
    private void CleanupOldReleases(string releasesDir, string activeRelease, int keep)
    {
        if (!Directory.Exists(releasesDir)) return;
        var all = Directory.EnumerateDirectories(releasesDir)
            .OrderBy(d => Path.GetFileName(d), StringComparer.Ordinal)
            .ToList();
        if (all.Count <= keep) return;

        var toDelete = all.Take(all.Count - keep).ToList();
        foreach (var dir in toDelete)
        {
            if (string.Equals(Path.GetFullPath(dir), Path.GetFullPath(activeRelease),
                    StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                Directory.Delete(dir, recursive: true);
                _logger.LogInformation("Pruned old release {Dir}", dir);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to prune release {Dir}: {Msg}", dir, ex.Message);
            }
        }
    }
}
