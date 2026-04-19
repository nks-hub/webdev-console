namespace NKS.WebDevConsole.Core.Services;

/// <summary>
/// Reads Apache access log files for a given calendar day, buckets the entries
/// by a configurable granularity, and returns three time-series: request count,
/// bytes transferred, and HTTP 5xx error count. Designed to power the
/// <c>/api/sites/{domain}/metrics/historical</c> endpoint so the frontend can
/// display a full day of metrics history even after a page refresh.
///
/// <para><b>File discovery:</b> For each base log path the caller provides, the
/// aggregator looks for rotated variants alongside it — <c>access.log</c>,
/// <c>access.log.1</c>, and date-suffixed files like
/// <c>access.log.2026-04-18</c> — and reads every file whose last-modified time
/// overlaps with the requested day. Content from all matching files is merged
/// before bucketing so log rotation mid-day does not create a gap.</para>
///
/// <para><b>Time zones:</b> The <paramref name="date"/> parameter is treated as
/// a local calendar day. Internally the day boundaries are converted to UTC
/// before comparing with parsed log timestamps (which Apache stores as UTC via
/// the <c>%t</c> Combined Log Format directive).</para>
/// </summary>
public static class AccessLogAggregator
{
    // ── Public API types ──────────────────────────────────────────────────────

    /// <summary>Supported bucket granularities.</summary>
    public enum Granularity
    {
        OneMinute = 1,
        FiveMinutes = 5,
        FifteenMinutes = 15,
        OneHour = 60,
    }

    /// <summary>A single data point in a metric series.</summary>
    public record DataPoint(DateTimeOffset Timestamp, long Value);

    /// <summary>A named metric series (requests, bytes, errors).</summary>
    public record MetricSeries(string Name, IReadOnlyList<DataPoint> Data);

    /// <summary>
    /// Full response returned by <see cref="AggregateAsync"/> — one entry per
    /// granularity bucket from 00:00 to 23:59 of the requested local day.
    /// </summary>
    public record AggregateResult(
        DateOnly Date,
        Granularity Granularity,
        int BucketCount,
        IReadOnlyList<MetricSeries> Series);

    // ── Granularity helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Parses a granularity string (e.g. <c>"5m"</c>) to the corresponding
    /// enum value. Returns <c>null</c> for unrecognised strings.
    /// </summary>
    public static Granularity? ParseGranularity(string? raw) =>
        raw?.ToLowerInvariant() switch
        {
            "1m" => Granularity.OneMinute,
            "5m" => Granularity.FiveMinutes,
            "15m" => Granularity.FifteenMinutes,
            "1h" => Granularity.OneHour,
            _ => null,
        };

    // ── Core aggregation ──────────────────────────────────────────────────────

    /// <summary>
    /// Aggregates access log data for a single calendar day.
    /// </summary>
    /// <param name="baseLogPaths">
    /// Ordered list of base access log file paths (e.g.
    /// <c>/opt/wdc/apache/2.4.62/logs/example.com-access.log</c>). Each path
    /// is expanded to include rotated variants; files are read with
    /// shared-read semantics so Apache can be writing simultaneously.
    /// </param>
    /// <param name="date">The local calendar day to aggregate.</param>
    /// <param name="granularity">Bucket width.</param>
    /// <param name="localTimeZone">
    /// Local time zone used to convert the calendar day to a UTC range. Pass
    /// <see cref="TimeZoneInfo.Local"/> for production; override in tests.
    /// </param>
    public static AggregateResult Aggregate(
        IEnumerable<string> baseLogPaths,
        DateOnly date,
        Granularity granularity,
        TimeZoneInfo? localTimeZone = null)
    {
        var tz = localTimeZone ?? TimeZoneInfo.Local;

        // Day boundaries in UTC
        var dayStartLocal = new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Unspecified);
        var dayEndLocal = dayStartLocal.AddDays(1);
        var dayStartUtc = TimeZoneInfo.ConvertTimeToUtc(dayStartLocal, tz);
        var dayEndUtc = TimeZoneInfo.ConvertTimeToUtc(dayEndLocal, tz);

        int bucketMinutes = (int)granularity;
        int totalBuckets = 24 * 60 / bucketMinutes;

        // Accumulator arrays indexed [0..totalBuckets)
        var requests = new long[totalBuckets];
        var bytes = new long[totalBuckets];
        var errors = new long[totalBuckets];

        foreach (var basePath in baseLogPaths)
        {
            if (string.IsNullOrWhiteSpace(basePath)) continue;

            foreach (var filePath in DiscoverRotatedFiles(basePath, date))
            {
                ProcessFile(filePath, dayStartUtc, dayEndUtc, bucketMinutes, totalBuckets,
                    requests, bytes, errors);
            }
        }

        // Build series with DateTimeOffset bucket timestamps in UTC
        var requestSeries = new List<DataPoint>(totalBuckets);
        var bytesSeries = new List<DataPoint>(totalBuckets);
        var errorSeries = new List<DataPoint>(totalBuckets);

        for (int i = 0; i < totalBuckets; i++)
        {
            var bucketUtc = new DateTimeOffset(dayStartUtc.AddMinutes(i * bucketMinutes), TimeSpan.Zero);
            requestSeries.Add(new DataPoint(bucketUtc, requests[i]));
            bytesSeries.Add(new DataPoint(bucketUtc, bytes[i]));
            errorSeries.Add(new DataPoint(bucketUtc, errors[i]));
        }

        return new AggregateResult(
            Date: date,
            Granularity: granularity,
            BucketCount: totalBuckets,
            Series:
            [
                new MetricSeries("requests", requestSeries),
                new MetricSeries("bytes", bytesSeries),
                new MetricSeries("errors", errorSeries),
            ]);
    }

    // ── File discovery ────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all rotated variants of <paramref name="basePath"/> that might
    /// contain entries for <paramref name="date"/>. Candidates checked:
    /// <list type="bullet">
    ///   <item><c>access.log</c> — active log (may span today)</item>
    ///   <item><c>access.log.1</c> — typical logrotate yesterday file</item>
    ///   <item><c>access.log.YYYY-MM-DD</c> — date-stamped rotation</item>
    ///   <item><c>access.log-YYYYMMDD</c> — alternate date-stamped format</item>
    /// </list>
    /// A file is included when it exists and its last-write time falls within a
    /// two-day window (the requested day ± 1 day) to handle timezone edge cases.
    /// The active log is always included if it exists — it may contain today's
    /// entries even when its mtime is tomorrow due to OS caching.
    /// </summary>
    internal static IReadOnlyList<string> DiscoverRotatedFiles(string basePath, DateOnly date)
    {
        var result = new List<string>();

        // Active log — include unconditionally if present
        if (File.Exists(basePath))
            result.Add(basePath);

        var dir = Path.GetDirectoryName(basePath) ?? ".";
        var fileName = Path.GetFileName(basePath);

        // Two-day window in UTC for last-write filtering: yesterday 00:00 .. tomorrow 00:00
        var windowStart = date.AddDays(-1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var windowEnd = date.AddDays(2).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        // Enumerate sibling files that share the base name as a prefix
        IEnumerable<string> siblings;
        try
        {
            siblings = Directory.EnumerateFiles(dir, $"{fileName}*").ToList();
        }
        catch (Exception)
        {
            return result;
        }

        foreach (var sibling in siblings)
        {
            // Skip the base file itself — already added above
            if (string.Equals(sibling, basePath, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var mtime = File.GetLastWriteTimeUtc(sibling);
                if (mtime < windowStart || mtime > windowEnd) continue;
                result.Add(sibling);
            }
            catch (Exception)
            {
                // Skip inaccessible files silently
            }
        }

        return result;
    }

    // ── Per-file processing ───────────────────────────────────────────────────

    private static void ProcessFile(
        string path,
        DateTime dayStartUtc,
        DateTime dayEndUtc,
        int bucketMinutes,
        int totalBuckets,
        long[] requests,
        long[] bytes,
        long[] errors)
    {
        try
        {
            using var fs = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            using var reader = new StreamReader(
                fs,
                System.Text.Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 64 * 1024,
                leaveOpen: false);

            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.Length == 0) continue;

                var entry = AccessLogInspector.ParseLine(line);
                if (entry is null) continue;

                var ts = entry.TimestampUtc;
                if (ts < dayStartUtc || ts >= dayEndUtc) continue;

                // Bucket index: minutes since start-of-day ÷ granularity
                int minutesIntoDay = (int)(ts - dayStartUtc).TotalMinutes;
                int bucketIndex = minutesIntoDay / bucketMinutes;

                // Guard against rounding at day boundary
                if ((uint)bucketIndex >= (uint)totalBuckets) continue;

                requests[bucketIndex]++;
                bytes[bucketIndex] += entry.ResponseBytes;
                if (entry.Status >= 500)
                    errors[bucketIndex]++;
            }
        }
        catch (Exception)
        {
            // Permission, file locked, parse error — skip this file silently.
            // Best-effort aggregation never throws to the endpoint handler.
        }
    }
}
