namespace NKS.WebDevConsole.Daemon.Config;

/// <summary>
/// Writes config files atomically (write-to-temp then rename) with automatic history rotation.
///
/// Failure modes covered:
///   - Daemon killed between tmp-write and rename → orphan <c>*.tmp</c> files accumulate
///     in the target directory. <see cref="CleanupOrphanTempFiles"/> is called on daemon
///     startup to reap any &gt; 1 hour old. The 1-hour window is short enough to free
///     space quickly but long enough that a tmp file being written right now (say, by a
///     concurrent process) is not clobbered.
///   - History pruning leaves exactly <paramref name="maxHistory"/> entries; verified by
///     xUnit test in BackupAndCrashRecoveryTests.
/// </summary>
public sealed class AtomicWriter
{
    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="targetPath"/> atomically.
    /// Archives the previous version in a <c>history/</c> subdirectory, keeping at most
    /// <paramref name="maxHistory"/> backups.
    /// </summary>
    public async Task WriteAsync(string targetPath, string content, int maxHistory = 5)
    {
        var dir = Path.GetDirectoryName(targetPath) ?? ".";
        var tmpPath = targetPath + ".tmp";

        // Ensure parent directory exists before writing the temp file.
        // Without this, callers had to pre-create the directory or eat a
        // DirectoryNotFoundException; with it, AtomicWriter is safe to call
        // anywhere (per-site config trees, plugin-owned subdirectories, etc).
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        try
        {
            // Write to temp file first
            await File.WriteAllTextAsync(tmpPath, content);

            // Archive current version if it exists
            if (File.Exists(targetPath))
            {
                var historyDir = Path.Combine(dir, "history");
                Directory.CreateDirectory(historyDir);
                var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                var historyPath = Path.Combine(historyDir,
                    $"{Path.GetFileName(targetPath)}.{timestamp}");
                File.Copy(targetPath, historyPath, overwrite: true);

                // Prune old history beyond maxHistory
                var files = Directory.GetFiles(historyDir, $"{Path.GetFileName(targetPath)}.*")
                    .OrderByDescending(f => f)
                    .Skip(maxHistory);
                foreach (var old in files)
                    File.Delete(old);
            }

            // Atomic rename (overwrite)
            File.Move(tmpPath, targetPath, overwrite: true);
        }
        catch
        {
            // On failure, make sure we don't leave an orphan tmp. A subsequent retry
            // will recreate it cleanly. If the delete itself fails (still locked),
            // CleanupOrphanTempFiles on next daemon start will catch it.
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { /* ignore */ }
            throw;
        }
    }

    /// <summary>
    /// Sweeps <paramref name="rootDir"/> for leftover <c>*.tmp</c> files older than
    /// <paramref name="staleAfter"/> and deletes them. Called from Program.cs on daemon
    /// startup to reap orphans left behind when the daemon was killed mid-write (e.g.
    /// power cut, taskkill /F). Never throws — failures are logged by the caller.
    /// </summary>
    public static int CleanupOrphanTempFiles(string rootDir, TimeSpan? staleAfter = null)
    {
        if (!Directory.Exists(rootDir)) return 0;
        var cutoff = DateTime.UtcNow - (staleAfter ?? TimeSpan.FromHours(1));
        int removed = 0;
        try
        {
            foreach (var tmp in Directory.EnumerateFiles(rootDir, "*.tmp", SearchOption.AllDirectories))
            {
                try
                {
                    var info = new FileInfo(tmp);
                    if (info.LastWriteTimeUtc < cutoff)
                    {
                        info.Delete();
                        removed++;
                    }
                }
                catch { /* locked or vanished — skip */ }
            }
        }
        catch { /* directory enumeration may race with daemon plugins creating files */ }
        return removed;
    }
}
