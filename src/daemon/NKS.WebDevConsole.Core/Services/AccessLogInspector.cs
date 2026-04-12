namespace NKS.WebDevConsole.Core.Services;

/// <summary>
/// Cheap filesystem-level inspector for Apache access logs. The first
/// foothold for the Phase 11 "Performance monitoring dashboard" item —
/// just metadata so the UI can show "1.2 MB · 15,234 requests · last hit
/// 3 min ago" per site without a full log parser or Apache module
/// plumbing.
///
/// Kept in <c>Core.Services</c> so both the daemon's Apache plugin
/// (when it wants to expose logs for its own services) and the daemon
/// itself (the <c>/api/sites/{domain}/metrics</c> endpoint) can reach
/// it without pulling a plugin reference.
///
/// This type is intentionally <b>not</b> a parser: it reports file size
/// and line count. Counting lines is O(n) in file size, so callers
/// should cache results or show them only on explicit user request
/// (e.g. SiteEdit page open, not the main sites list).
/// </summary>
public static class AccessLogInspector
{
    public record AccessLogStats(
        string Path,
        long SizeBytes,
        long LineCount,
        DateTime LastWrittenUtc);

    /// <summary>
    /// Inspects the first log file from <paramref name="candidatePaths"/>
    /// that exists on disk. Returns <c>null</c> if none are present or
    /// reads fail for any reason (permission, file locked by Apache, etc.).
    ///
    /// The <c>maxLineScanBytes</c> cap keeps the line count cheap even
    /// for GB-sized logs — when exceeded, line count is estimated from
    /// the first chunk and extrapolated. Default 10 MB is enough for
    /// most dev machines and scans in &lt; 50 ms.
    /// </summary>
    public static AccessLogStats? Inspect(
        IEnumerable<string> candidatePaths,
        long maxLineScanBytes = 10 * 1024 * 1024)
    {
        foreach (var path in candidatePaths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;

            try
            {
                if (!File.Exists(path)) continue;

                var info = new FileInfo(path);
                var size = info.Length;
                var lineCount = CountLines(path, size, maxLineScanBytes);
                return new AccessLogStats(path, size, lineCount, info.LastWriteTimeUtc);
            }
            catch (Exception)
            {
                // Permission, sharing violation, file-locked-by-Apache —
                // continue to the next candidate. This is intentionally
                // silent: metrics are best-effort, never block the caller.
            }
        }

        return null;
    }

    private static long CountLines(string path, long fileSize, long maxScanBytes)
    {
        // Read in shared mode so Apache can still write to the log.
        using var fs = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        var scanLimit = Math.Min(fileSize, maxScanBytes);
        var buffer = new byte[64 * 1024];
        long scanned = 0;
        long lineCount = 0;

        while (scanned < scanLimit)
        {
            var toRead = (int)Math.Min(buffer.Length, scanLimit - scanned);
            var read = fs.Read(buffer, 0, toRead);
            if (read == 0) break;
            for (int i = 0; i < read; i++)
                if (buffer[i] == (byte)'\n')
                    lineCount++;
            scanned += read;
        }

        // Extrapolate when we hit the cap before EOF: assume average line
        // density is uniform across the file. Errs high by ~0.5 lines when
        // the tail half is slightly different, acceptable for a badge.
        if (fileSize > scanLimit && scanned > 0)
            lineCount = (long)(lineCount * ((double)fileSize / scanned));

        return lineCount;
    }
}
