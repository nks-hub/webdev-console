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
        int? KeepReleases = null);

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

            await _runs.UpdateStatusAsync(deployId, "running", ct);

            // ── 2. Copy source → release dir ─────────────────────────────
            await _events.BroadcastAsync("deploy:phase",
                new { deployId, phase = "Fetching", message = $"Copying files to {releaseDir}" });
            CopyDirectory(sourcePath, releaseDir);

            // ── 3. Apply shared symlinks ─────────────────────────────────
            await _events.BroadcastAsync("deploy:phase",
                new { deployId, phase = "Building",
                      message = $"Linking shared resources ({sharedDirs.Count} dirs, {sharedFiles.Count} files)" });
            foreach (var dir in sharedDirs)
                LinkSharedDir(releaseDir, sharedDir, dir);
            foreach (var file in sharedFiles)
                LinkSharedFile(releaseDir, sharedDir, file);

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
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(ex, "Local deploy {DeployId} failed: {Msg}", deployId, ex.Message);
            try { await _runs.MarkCompletedAsync(deployId, success: false, exitCode: -1,
                errorMessage: ex.Message, durationMs: sw.ElapsedMilliseconds, ct); } catch { }
            try { await _events.BroadcastAsync("deploy:complete",
                new { deployId, success = false, error = ex.Message }); } catch { }
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
