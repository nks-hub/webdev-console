using System.Text.RegularExpressions;

namespace NKS.WebDevConsole.Core.Services;

/// <summary>
/// Parses and tails Apache error logs and PHP-FPM/PHP error logs.
/// Mirrors the structure of <see cref="AccessLogInspector"/> — cheap tail
/// of the trailing bytes, regex parse, newest-first output, silent
/// best-effort (no exceptions propagated to the caller).
/// </summary>
public static class ErrorLogInspector
{
    /// <summary>
    /// Unified log entry returned by both Apache and PHP parsers.
    /// </summary>
    public record LogEntry(
        DateTimeOffset Timestamp,
        string Severity,
        string Source,
        string Message,
        string? Pid,
        string? Client);

    // ── Apache error log format ───────────────────────────────────────────────
    // [Day Mon DD HH:MM:SS.uuuuuu YYYY] [module:level] [pid PID] [client IP:port] message
    // Examples:
    //   [Sun Apr 13 10:00:00.123456 2026] [core:error] [pid 1234] [client 127.0.0.1:54321] AH00124: Request exceeded the limit
    //   [Sun Apr 13 10:00:00.000000 2026] [mpm_winnt:notice] [pid 1234] AH00455: Apache lounge started.
    private static readonly Regex ApacheErrorRegex = new(
        @"^\[(?<day>\w+)\s+(?<month>\w+)\s+(?<dd>\d+)\s+(?<time>\d{2}:\d{2}:\d{2})(?:\.\d+)?\s+(?<year>\d{4})\]\s+\[(?<module>[^:]+):(?<level>\w+)\]\s+\[pid\s+(?<pid>\d+)\](?:\s+\[client\s+(?<client>[^\]]+)\])?\s+(?<msg>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // ── PHP-FPM / PHP error log format ────────────────────────────────────────
    // PHP-FPM global: [DD-Mon-YYYY HH:MM:SS] POOL pool_name: LEVEL: message
    //                 [DD-Mon-YYYY HH:MM:SS] ERROR: message
    // PHP web errors: [DD-Mon-YYYY HH:MM:SS Timezone] PHP Notice: message in /file.php on line N
    //                 [DD-Mon-YYYY HH:MM:SS] PHP Fatal error: message in /file.php on line N
    // Covers both with/without timezone suffix.
    private static readonly Regex PhpErrorRegex = new(
        @"^\[(?<date>\d{2}-\w{3}-\d{4}\s+\d{2}:\d{2}:\d{2})(?:\s+[A-Za-z/_]+)?\]\s+(?<msg>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Months used by Apache and PHP date strings
    private static readonly string[] MonthAbbreviations =
    {
        "Jan", "Feb", "Mar", "Apr", "May", "Jun",
        "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"
    };

    /// <summary>
    /// Tails the last <paramref name="limit"/> error log entries from the
    /// first existing path in <paramref name="candidatePaths"/>, keeping
    /// only entries with a timestamp at or after <paramref name="since"/>
    /// (when provided). Returns entries sorted newest-first.
    ///
    /// Reads only the trailing <paramref name="maxTailBytes"/> bytes of each
    /// file — default 512 KB covers several thousand error lines without
    /// loading a GB-sized log into memory. Silent best-effort: any I/O or
    /// parse failure returns an empty list.
    /// </summary>
    public static IReadOnlyList<LogEntry> Tail(
        IEnumerable<string> candidatePaths,
        string source,
        int limit = 100,
        DateTimeOffset? since = null,
        long maxTailBytes = 512 * 1024)
    {
        limit = Math.Clamp(limit, 1, 10_000);
        var parser = ResolveParser(source);

        foreach (var path in candidatePaths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (!File.Exists(path)) continue;

            try
            {
                var lines = ReadTailLines(path, maxTailBytes);
                var parsed = new List<LogEntry>(Math.Min(limit, lines.Count));

                // Walk newest first (from the end of the buffer), parse and
                // apply the `since` filter, stop once we have enough entries.
                for (int i = lines.Count - 1; i >= 0 && parsed.Count < limit; i--)
                {
                    var entry = parser(lines[i], source);
                    if (entry is null) continue;
                    if (since.HasValue && entry.Timestamp < since.Value) continue;
                    parsed.Add(entry);
                }

                // parsed is already newest-first — return as-is
                return parsed;
            }
            catch (Exception)
            {
                // Permission, sharing violation, file-locked — best-effort,
                // fall through to the next candidate.
            }
        }

        return Array.Empty<LogEntry>();
    }

    /// <summary>
    /// Merges error log entries from multiple source paths (each with its
    /// own source tag), applies the <paramref name="since"/> filter, caps
    /// at <paramref name="limit"/>, and returns entries sorted newest-first.
    /// Designed for the /api/sites/{domain}/logs/errors endpoint that
    /// aggregates Apache error + PHP error in one call.
    /// </summary>
    public static IReadOnlyList<LogEntry> TailMultiple(
        IEnumerable<(IEnumerable<string> Paths, string Source)> sources,
        int limit = 100,
        DateTimeOffset? since = null,
        long maxTailBytes = 512 * 1024)
    {
        limit = Math.Clamp(limit, 1, 10_000);

        var all = new List<LogEntry>();
        foreach (var (paths, source) in sources)
        {
            var entries = Tail(paths, source, limit, since, maxTailBytes);
            all.AddRange(entries);
        }

        // Sort newest-first and cap at limit
        all.Sort(static (a, b) => DateTimeOffset.Compare(b.Timestamp, a.Timestamp));
        if (all.Count > limit)
            all.RemoveRange(limit, all.Count - limit);

        return all;
    }

    // ── Parser selection ──────────────────────────────────────────────────────

    private static Func<string, string, LogEntry?> ResolveParser(string source) =>
        source switch
        {
            "apache-error" => ParseApacheLine,
            _ => ParsePhpLine   // php-fpm-error, php-error
        };

    // ── Apache error log parser ───────────────────────────────────────────────

    /// <summary>
    /// Parses a single Apache error log line. Returns null for lines that
    /// do not match the expected format (continuation lines, blank lines).
    /// </summary>
    public static LogEntry? ParseApacheLine(string line, string source = "apache-error")
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        var m = ApacheErrorRegex.Match(line);
        if (!m.Success) return null;

        // Build "13 Apr 2026 10:00:00 +0000" — Apache logs local time;
        // we treat it as UTC for simplicity since WDC is a local dev tool.
        var dateStr = $"{m.Groups["dd"].Value} {m.Groups["month"].Value} {m.Groups["year"].Value} {m.Groups["time"].Value}";
        if (!DateTimeOffset.TryParseExact(
                dateStr,
                "dd MMM yyyy HH:mm:ss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal,
                out var ts))
            return null;

        return new LogEntry(
            Timestamp: ts,
            Severity: m.Groups["level"].Value,
            Source: source,
            Message: m.Groups["msg"].Value.Trim(),
            Pid: m.Groups["pid"].Value,
            Client: m.Groups["client"].Success ? m.Groups["client"].Value : null);
    }

    // ── PHP error log parser ──────────────────────────────────────────────────

    /// <summary>
    /// Parses a single PHP error or PHP-FPM log line. Returns null for
    /// lines that do not match the bracketed-timestamp format.
    /// </summary>
    public static LogEntry? ParsePhpLine(string line, string source = "php-error")
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        var m = PhpErrorRegex.Match(line);
        if (!m.Success) return null;

        // PHP date: "13-Apr-2026 10:00:00"
        if (!DateTimeOffset.TryParseExact(
                m.Groups["date"].Value,
                "dd-MMM-yyyy HH:mm:ss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal,
                out var ts))
            return null;

        var body = m.Groups["msg"].Value.Trim();

        // Extract severity from the message body.
        // PHP-FPM global:  "NOTICE: fpm is running, pid 1234"
        //                  "WARNING: [pool www] child ..."
        //                  "ERROR: ..."
        // PHP web:         "PHP Fatal error: ...", "PHP Notice: ...", "PHP Warning: ..."
        // FPM pool:        "pool nks-wdc-php84: child 1234 started"
        string severity = "info";
        string message = body;

        if (body.StartsWith("PHP Fatal error:", StringComparison.OrdinalIgnoreCase))
        {
            severity = "fatal";
            message = body["PHP Fatal error:".Length..].Trim();
        }
        else if (body.StartsWith("PHP Parse error:", StringComparison.OrdinalIgnoreCase))
        {
            severity = "error";
            message = body["PHP Parse error:".Length..].Trim();
        }
        else if (body.StartsWith("PHP Warning:", StringComparison.OrdinalIgnoreCase))
        {
            severity = "warning";
            message = body["PHP Warning:".Length..].Trim();
        }
        else if (body.StartsWith("PHP Notice:", StringComparison.OrdinalIgnoreCase))
        {
            severity = "notice";
            message = body["PHP Notice:".Length..].Trim();
        }
        else if (body.StartsWith("PHP Deprecated:", StringComparison.OrdinalIgnoreCase))
        {
            severity = "deprecated";
            message = body["PHP Deprecated:".Length..].Trim();
        }
        else if (body.StartsWith("PHP Stack trace:", StringComparison.OrdinalIgnoreCase)
              || body.StartsWith("PHP  ", StringComparison.Ordinal))
        {
            // Stack trace continuation lines — preserve but tag as debug
            severity = "debug";
        }
        else
        {
            // PHP-FPM: "NOTICE:", "WARNING:", "ERROR:", "ALERT:", pool lines
            var colon = body.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0 && colon < 12)
            {
                var word = body[..colon].ToUpperInvariant();
                if (word is "NOTICE" or "WARNING" or "ERROR" or "ALERT" or "EMERG" or "DEBUG")
                {
                    severity = word.ToLowerInvariant();
                    message = body[(colon + 1)..].Trim();
                }
            }
        }

        return new LogEntry(
            Timestamp: ts,
            Severity: severity,
            Source: source,
            Message: message,
            Pid: null,
            Client: null);
    }

    // ── File I/O — shared with AccessLogInspector approach ───────────────────

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

        using var reader = new StreamReader(
            fs,
            System.Text.Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 64 * 1024,
            leaveOpen: false);

        // Skip the first (probably partial) line when we started mid-file
        if (start > 0) reader.ReadLine();

        var lines = new List<string>(512);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length > 0) lines.Add(line);
        }

        return lines;
    }
}
