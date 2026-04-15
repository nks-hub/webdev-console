using System.Text.RegularExpressions;

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
/// In addition to byte/line counts (see <see cref="Inspect"/>) this
/// class can now tail the last N requests out of a log with a minimal
/// Combined Log Format parser — enough to power a per-site "last
/// visitors" panel in the UI (ip, path, status, user-agent, timestamp).
/// </summary>
public static class AccessLogInspector
{
    public record AccessLogStats(
        string Path,
        long SizeBytes,
        long LineCount,
        DateTime LastWrittenUtc);

    /// <summary>
    /// Single parsed entry out of an Apache Combined Log Format line.
    /// Anything that fails to match is dropped silently so a handful of
    /// malformed lines don't poison the entire response.
    /// </summary>
    public record AccessLogEntry(
        DateTime TimestampUtc,
        string RemoteAddr,
        string Method,
        string Path,
        string Protocol,
        int Status,
        long ResponseBytes,
        string Referer,
        string UserAgent);

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

    /// <summary>
    /// Tails the last <paramref name="limit"/> access log entries from
    /// the first existing path in <paramref name="candidatePaths"/>.
    /// Returns oldest → newest (natural log order).
    ///
    /// Reads only the trailing <paramref name="maxTailBytes"/> bytes of
    /// the file — default 256 KB is enough for ~1000 request lines on
    /// a Combined-Log-Format log, fast even on GB-sized files, and
    /// respects shared-read semantics so Apache can still write while
    /// we read.
    /// </summary>
    public static IReadOnlyList<AccessLogEntry> Tail(
        IEnumerable<string> candidatePaths,
        int limit = 100,
        long maxTailBytes = 256 * 1024)
    {
        limit = Math.Clamp(limit, 1, 10_000);

        foreach (var path in candidatePaths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (!File.Exists(path)) continue;

            try
            {
                var lines = ReadTailLines(path, maxTailBytes);
                // Parse newest→oldest, then reverse so the caller sees
                // chronological order (oldest first). We also cap at
                // `limit` entries BEFORE parsing so a huge buffer doesn't
                // drive allocations through the roof.
                var parsed = new List<AccessLogEntry>(Math.Min(limit, lines.Count));
                for (int i = lines.Count - 1; i >= 0 && parsed.Count < limit; i--)
                {
                    var entry = ParseCombinedLine(lines[i]);
                    if (entry is not null) parsed.Add(entry);
                }
                parsed.Reverse();
                return parsed;
            }
            catch (Exception)
            {
                // Fall through to the next candidate on any filesystem
                // hiccup. Best-effort parsing never throws.
            }
        }

        return Array.Empty<AccessLogEntry>();
    }

    private static List<string> ReadTailLines(string path, long maxTailBytes)
    {
        using var fs = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        var length = fs.Length;
        var start = Math.Max(0, length - maxTailBytes);
        fs.Seek(start, SeekOrigin.Begin);

        using var reader = new StreamReader(fs, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 64 * 1024, leaveOpen: false);
        // Skip the (probably partial) first line when we didn't start at
        // the beginning — we don't want to return a truncated record.
        if (start > 0) reader.ReadLine();

        var lines = new List<string>(512);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length > 0) lines.Add(line);
        }
        return lines;
    }

    // Combined Log Format:
    //   remote-ip ident user [timestamp] "METHOD path HTTP/1.x" status bytes "referer" "user-agent"
    // Tolerant regex — captures the important bits, dashes are mapped
    // to empty strings for referer / user-agent, and a handful of
    // corner cases (no bytes → "-", space in path) are handled.
    private static readonly Regex CombinedLineRegex = new(
        @"^(?<ip>\S+)\s+\S+\s+\S+\s+\[(?<time>[^\]]+)\]\s+""(?<method>[A-Z]+)\s+(?<path>[^""]*?)\s+(?<proto>HTTP/[\d.]+)""\s+(?<status>\d+)\s+(?<bytes>\d+|-)\s+""(?<referer>[^""]*)""\s+""(?<ua>[^""]*)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Apache's %t format: "[10/Oct/2000:13:55:36 -0700]"
    private static readonly string[] ApacheTimeFormats =
    {
        "dd/MMM/yyyy:HH:mm:ss zzz",
        "dd/MMM/yyyy:HH:mm:ss",
    };

    private static AccessLogEntry? ParseCombinedLine(string line)
    {
        var m = CombinedLineRegex.Match(line);
        if (!m.Success) return null;

        DateTime ts;
        var timeRaw = m.Groups["time"].Value;
        if (!DateTime.TryParseExact(
                timeRaw,
                ApacheTimeFormats,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out ts))
        {
            // Unknown timestamp shape — skip the line entirely rather
            // than return a bogus year-0001 record.
            return null;
        }

        long bytes = 0;
        var bytesRaw = m.Groups["bytes"].Value;
        if (bytesRaw != "-")
            long.TryParse(bytesRaw, out bytes);

        int status = 0;
        int.TryParse(m.Groups["status"].Value, out status);

        return new AccessLogEntry(
            TimestampUtc: ts,
            RemoteAddr: m.Groups["ip"].Value,
            Method: m.Groups["method"].Value,
            Path: m.Groups["path"].Value,
            Protocol: m.Groups["proto"].Value,
            Status: status,
            ResponseBytes: bytes,
            Referer: m.Groups["referer"].Value == "-" ? "" : m.Groups["referer"].Value,
            UserAgent: m.Groups["ua"].Value == "-" ? "" : m.Groups["ua"].Value);
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
