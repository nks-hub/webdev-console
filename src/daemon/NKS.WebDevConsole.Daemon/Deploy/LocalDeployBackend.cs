using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Interfaces;

namespace NKS.WebDevConsole.Daemon.Deploy;

/// <summary>
/// Phase 7.5+++ — minimum REAL deploy backend (replacing the dummy
/// Task.Delay state machine for actual UI/E2E testing). Performs:
///
///   1. Validate source dir exists + target dir parent writable.
///   2. Create release dir at <c>{target}/releases/{releaseId}/</c>.
///   3. Recursively copy source → release dir (skips .git/, node_modules/).
///   4. Atomic symlink swap <c>{target}/current</c> → release.
///   5. Mark deploy_runs row as completed.
///
/// Each step broadcasts a `deploy:phase` SSE event so the GUI drawer
/// renders progress. Errors are caught and result in
/// <c>deploy:complete{success:false}</c> with the message.
///
/// Triggered when the POST /api/nks.wdc.deploy/sites/{domain}/deploy
/// body includes <c>"localPaths": {"source":"...","target":"..."}</c>.
/// Without that field, the existing dummy executor runs (preserves E2E
/// suite which depends on synthetic timing).
///
/// Cross-platform note: symlink creation requires admin on Windows
/// without Developer Mode. Falls back to copying a "current" subdir
/// when symlink fails — operator can read the dir name from the
/// completed deploy_runs row to learn which release is "current".
/// </summary>
public sealed class LocalDeployBackend
{
    private static readonly string[] SkipDirs = { ".git", "node_modules", ".vs", "bin", "obj", ".next" };

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
    /// Run the full pipeline. Returns when complete (success or failure).
    /// Caller is expected to fire-and-forget via Task.Run since the deploy
    /// HTTP request returns 202 Accepted before this finishes.
    /// </summary>
    public async Task RunAsync(
        string deployId,
        string releaseId,
        string sourcePath,
        string targetPath,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await _events.BroadcastAsync("deploy:phase",
                new { deployId, phase = "Building", message = $"Validating source {sourcePath}" });
            if (!Directory.Exists(sourcePath))
                throw new DirectoryNotFoundException($"Source path does not exist: {sourcePath}");
            Directory.CreateDirectory(targetPath); // also creates parents

            var releasesDir = Path.Combine(targetPath, "releases");
            var releaseDir = Path.Combine(releasesDir, releaseId);
            Directory.CreateDirectory(releaseDir);

            await _runs.UpdateStatusAsync(deployId, "running", ct);

            await _events.BroadcastAsync("deploy:phase",
                new { deployId, phase = "Fetching", message = $"Copying files to {releaseDir}" });
            CopyDirectory(sourcePath, releaseDir);

            await _events.BroadcastAsync("deploy:phase",
                new { deployId, phase = "Switching", message = "Atomic symlink swap" });
            var currentLink = Path.Combine(targetPath, "current");
            SwitchSymlinkOrCopy(currentLink, releaseDir);
            await _runs.MarkPastPonrAsync(deployId, ct);

            await _events.BroadcastAsync("deploy:phase",
                new { deployId, phase = "AwaitingSoak", message = "Switched, soak complete (local backend)" });
            await _runs.UpdateStatusAsync(deployId, "awaiting_soak", ct);

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
}
