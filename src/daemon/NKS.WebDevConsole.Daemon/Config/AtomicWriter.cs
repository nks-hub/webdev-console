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
    // Serialises writers per target path. Two concurrent saves of the same
    // file used to share one fixed "<target>.tmp" name, so one writer could
    // rename the other's half-written temp file into place (or fail with
    // "file not found" when its own temp had already been moved).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> PathLocks =
        new(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private static SemaphoreSlim LockFor(string path) =>
        PathLocks.GetOrAdd(Path.GetFullPath(path), _ => new SemaphoreSlim(1, 1));

    /// <summary>Unique temp name next to <paramref name="targetPath"/> (same volume, so the rename is atomic).</summary>
    public static string TempPathFor(string targetPath) =>
        $"{targetPath}.{Guid.NewGuid():N}.tmp";

    /// <summary>
    /// Synchronous temp-file + rename write without history, for small state
    /// files (plugin state, consent, per-site php.ini). Flushes to disk before
    /// the rename so a power cut cannot leave a zero-length file in place.
    /// </summary>
    public static void WriteAllTextAtomic(string targetPath, string content)
    {
        var dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var gate = LockFor(targetPath);
        gate.Wait();
        var tmpPath = TempPathFor(targetPath);
        try
        {
            using (var fs = new FileStream(tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var sw = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
            {
                sw.Write(content);
                sw.Flush();
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmpPath, targetPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { /* ignore */ }
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="targetPath"/> atomically.
    /// Archives the previous version in a <c>history/</c> subdirectory, keeping at most
    /// <paramref name="maxHistory"/> backups.
    /// </summary>
    public async Task WriteAsync(string targetPath, string content, int maxHistory = 5)
    {
        var dir = Path.GetDirectoryName(targetPath) ?? ".";
        var tmpPath = TempPathFor(targetPath);

        // Ensure parent directory exists before writing the temp file.
        // Without this, callers had to pre-create the directory or eat a
        // DirectoryNotFoundException; with it, AtomicWriter is safe to call
        // anywhere (per-site config trees, plugin-owned subdirectories, etc).
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        var gate = LockFor(targetPath);
        await gate.WaitAsync();
        try
        {
            // Write to temp file first, flushed to disk before the rename.
            await using (var fs = new FileStream(tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            await using (var sw = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
            {
                await sw.WriteAsync(content);
                await sw.FlushAsync();
                fs.Flush(flushToDisk: true);
            }

            // Archive current version if it exists
            if (File.Exists(targetPath))
            {
                var historyDir = Path.Combine(dir, "history");
                Directory.CreateDirectory(historyDir);
                // Millisecond resolution: two saves within the same second
                // used to overwrite each other's history entry.
                var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
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
        finally
        {
            gate.Release();
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
