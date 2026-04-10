namespace NKS.WebDevConsole.Daemon.Config;

/// <summary>
/// Writes config files atomically (write-to-temp then rename) with automatic history rotation.
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
}
