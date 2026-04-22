using NKS.WebDevConsole.Core.Services;

namespace NKS.WebDevConsole.Core.Tests;

/// <summary>
/// Unit tests for <see cref="AccessLogAggregator"/> — the Phase 7.1 historical
/// metrics endpoint backend. Covers bucket math, error counting, bytes
/// aggregation, empty-day behaviour, and multi-file rotation discovery.
/// </summary>
public sealed class AccessLogAggregatorTests : IDisposable
{
    private readonly string _tempDir;

    // Fixed date used across all tests: 2026-04-19 (a Sunday)
    private static readonly DateOnly TestDate = new(2026, 4, 19);

    // Use UTC as the "local" time zone so bucket boundaries are predictable
    // without OS timezone dependency.
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    public AccessLogAggregatorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"nks-wdc-agg-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes a Combined Log Format line with the given timestamp (UTC) and
    /// HTTP status. ResponseBytes defaults to 512.
    /// </summary>
    private static string MakeLine(DateTime utc, int status = 200, long responseBytes = 512)
    {
        // Apache %t format: [dd/MMM/yyyy:HH:mm:ss +0000]
        var ts = utc.ToString("dd/MMM/yyyy:HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        return $"127.0.0.1 - - [{ts} +0000] \"GET /path HTTP/1.1\" {status} {responseBytes} \"-\" \"TestAgent/1.0\"";
    }

    private string LogFile(string name)
        => Path.Combine(_tempDir, name);

    private static AccessLogAggregator.AggregateResult Aggregate(
        string basePath,
        AccessLogAggregator.Granularity gran = AccessLogAggregator.Granularity.FiveMinutes)
        => AccessLogAggregator.Aggregate([basePath], TestDate, gran, Utc);

    private static long SeriesValue(AccessLogAggregator.AggregateResult result, string name, int bucketIndex)
        => result.Series.First(s => s.Name == name).Data[bucketIndex].Value;

    // ── Tests ──────────────────────────────────────────────────────────────────

    [Fact]
    public void EmptyDay_ReturnsAllZeroSeries()
    {
        // Log file exists but contains no entries — all buckets must be 0
        var path = LogFile("empty.log");
        File.WriteAllText(path, "");

        var result = Aggregate(path);

        Assert.Equal(288, result.BucketCount); // 24h / 5m
        Assert.All(result.Series, s => Assert.All(s.Data, p => Assert.Equal(0, p.Value)));
    }

    [Fact]
    public void MissingLogFile_ReturnsAllZeroSeries()
    {
        // Base path does not exist — no rotation files either
        var path = LogFile("nonexistent.log");

        var result = Aggregate(path);

        Assert.Equal(288, result.BucketCount);
        Assert.All(result.Series, s => Assert.All(s.Data, p => Assert.Equal(0, p.Value)));
    }

    [Fact]
    public void ThreeEntriesAcrossTwoBuckets_CorrectRequestCounts()
    {
        // 2026-04-19 00:01 → bucket 0 (0:00–0:04)
        // 2026-04-19 00:03 → bucket 0
        // 2026-04-19 00:07 → bucket 1 (0:05–0:09)
        var path = LogFile("three.log");
        File.WriteAllLines(path,
        [
            MakeLine(new DateTime(2026, 4, 19, 0, 1, 0, DateTimeKind.Utc)),
            MakeLine(new DateTime(2026, 4, 19, 0, 3, 0, DateTimeKind.Utc)),
            MakeLine(new DateTime(2026, 4, 19, 0, 7, 0, DateTimeKind.Utc)),
        ]);

        var result = Aggregate(path);

        Assert.Equal(2, SeriesValue(result, "requests", 0)); // 00:01 and 00:03
        Assert.Equal(1, SeriesValue(result, "requests", 1)); // 00:07
        Assert.Equal(0, SeriesValue(result, "requests", 2)); // 00:10 — empty
    }

    [Fact]
    public void BucketBoundaryMath_EntryAtX59X59_FallsIntoCorrectBucket()
    {
        // For 5m granularity, 00:59:59 is 59 minutes into the day.
        // 59 / 5 = 11 → bucket index 11 (the :55 bucket, 0:55–0:59)
        var path = LogFile("boundary.log");
        File.WriteAllLines(path,
        [
            MakeLine(new DateTime(2026, 4, 19, 0, 59, 59, DateTimeKind.Utc)),
        ]);

        var result = Aggregate(path);

        Assert.Equal(1, SeriesValue(result, "requests", 11));
        Assert.Equal(0, SeriesValue(result, "requests", 12));
    }

    [Fact]
    public void Http5xx_CountedAsError_4xxNot()
    {
        var path = LogFile("errors.log");
        File.WriteAllLines(path,
        [
            MakeLine(new DateTime(2026, 4, 19, 1, 0, 0, DateTimeKind.Utc), status: 200),
            MakeLine(new DateTime(2026, 4, 19, 1, 0, 0, DateTimeKind.Utc), status: 404),
            MakeLine(new DateTime(2026, 4, 19, 1, 0, 0, DateTimeKind.Utc), status: 500),
            MakeLine(new DateTime(2026, 4, 19, 1, 0, 0, DateTimeKind.Utc), status: 503),
        ]);

        var result = Aggregate(path);

        int bucket = 60 / 5; // 1h into day = bucket 12
        Assert.Equal(4, SeriesValue(result, "requests", bucket));
        Assert.Equal(2, SeriesValue(result, "errors", bucket));   // 500 + 503
        Assert.Equal(0, SeriesValue(result, "errors", bucket - 1)); // previous bucket clean
    }

    [Fact]
    public void BytesAggregation_SumsCorrectly()
    {
        var path = LogFile("bytes.log");
        File.WriteAllLines(path,
        [
            MakeLine(new DateTime(2026, 4, 19, 2, 0, 0, DateTimeKind.Utc), responseBytes: 1000),
            MakeLine(new DateTime(2026, 4, 19, 2, 1, 0, DateTimeKind.Utc), responseBytes: 2500),
            MakeLine(new DateTime(2026, 4, 19, 2, 2, 0, DateTimeKind.Utc), responseBytes: 750),
        ]);

        var result = Aggregate(path);

        int bucket = (2 * 60) / 5; // 120 min / 5 = bucket 24
        Assert.Equal(4250, SeriesValue(result, "bytes", bucket));
    }

    [Fact]
    public void EntriesOutsideDay_AreIgnored()
    {
        var path = LogFile("outofday.log");
        File.WriteAllLines(path,
        [
            // Day before
            MakeLine(new DateTime(2026, 4, 18, 23, 59, 59, DateTimeKind.Utc)),
            // Day after
            MakeLine(new DateTime(2026, 4, 20, 0, 0, 0, DateTimeKind.Utc)),
            // Within day — only this one should be counted
            MakeLine(new DateTime(2026, 4, 19, 12, 0, 0, DateTimeKind.Utc)),
        ]);

        var result = Aggregate(path);

        long totalRequests = result.Series.First(s => s.Name == "requests").Data.Sum(p => p.Value);
        Assert.Equal(1, totalRequests);
    }

    [Fact]
    public void MultiFileRotation_ReadsFromBothCurrentAndRotated()
    {
        // Simulate rotation: current log has today's afternoon, rotated .1 has morning
        var basePath = LogFile("site-access.log");
        var rotatedPath = LogFile("site-access.log.1");

        // Morning entry in the rotated file
        File.WriteAllLines(rotatedPath,
        [
            MakeLine(new DateTime(2026, 4, 19, 8, 0, 0, DateTimeKind.Utc)),
        ]);

        // Afternoon entry in the active log
        File.WriteAllLines(basePath,
        [
            MakeLine(new DateTime(2026, 4, 19, 14, 0, 0, DateTimeKind.Utc)),
        ]);

        // DiscoverRotatedFiles filters siblings by last-write-time falling within
        // TestDate ± 1 day. Freshly-written temp files have mtime = now, so stamp
        // them to TestDate explicitly to keep the test time-independent.
        File.SetLastWriteTimeUtc(rotatedPath, TestDate.ToDateTime(new TimeOnly(8, 0), DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(basePath, TestDate.ToDateTime(new TimeOnly(14, 0), DateTimeKind.Utc));

        var result = Aggregate(basePath);

        long totalRequests = result.Series.First(s => s.Name == "requests").Data.Sum(p => p.Value);
        Assert.Equal(2, totalRequests); // morning + afternoon

        int morningBucket = (8 * 60) / 5;       // bucket 96
        int afternoonBucket = (14 * 60) / 5;     // bucket 168
        Assert.Equal(1, SeriesValue(result, "requests", morningBucket));
        Assert.Equal(1, SeriesValue(result, "requests", afternoonBucket));
    }

    [Fact]
    public void OneHourGranularity_Produces24Buckets()
    {
        var path = LogFile("hourly.log");
        File.WriteAllLines(path,
        [
            MakeLine(new DateTime(2026, 4, 19, 0, 30, 0, DateTimeKind.Utc)),  // bucket 0
            MakeLine(new DateTime(2026, 4, 19, 1, 45, 0, DateTimeKind.Utc)),  // bucket 1
            MakeLine(new DateTime(2026, 4, 19, 23, 59, 0, DateTimeKind.Utc)), // bucket 23
        ]);

        var result = AccessLogAggregator.Aggregate([path], TestDate, AccessLogAggregator.Granularity.OneHour, Utc);

        Assert.Equal(24, result.BucketCount);
        Assert.Equal(1, SeriesValue(result, "requests", 0));
        Assert.Equal(1, SeriesValue(result, "requests", 1));
        Assert.Equal(1, SeriesValue(result, "requests", 23));
    }

    [Fact]
    public void BucketTimestamps_StartAtMidnightUtc()
    {
        var path = LogFile("ts.log");
        File.WriteAllText(path, "");

        var result = Aggregate(path);

        var firstBucket = result.Series[0].Data[0].Timestamp;
        var expected = new DateTimeOffset(2026, 4, 19, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(expected, firstBucket);

        // Second bucket for 5m granularity is 00:05
        var secondBucket = result.Series[0].Data[1].Timestamp;
        Assert.Equal(expected.AddMinutes(5), secondBucket);
    }

    [Fact]
    public void ParseGranularity_AllValidTokens_Recognised()
    {
        Assert.Equal(AccessLogAggregator.Granularity.OneMinute, AccessLogAggregator.ParseGranularity("1m"));
        Assert.Equal(AccessLogAggregator.Granularity.FiveMinutes, AccessLogAggregator.ParseGranularity("5m"));
        Assert.Equal(AccessLogAggregator.Granularity.FifteenMinutes, AccessLogAggregator.ParseGranularity("15m"));
        Assert.Equal(AccessLogAggregator.Granularity.OneHour, AccessLogAggregator.ParseGranularity("1h"));
        Assert.Null(AccessLogAggregator.ParseGranularity("30m"));
        Assert.Null(AccessLogAggregator.ParseGranularity(null));
        Assert.Null(AccessLogAggregator.ParseGranularity(""));
    }

    // ── Granularity edge cases ─────────────────────────────────────────────────

    [Theory]
    [InlineData("13m")]
    [InlineData("90m")]
    [InlineData("2h")]
    [InlineData("0m")]
    public void Aggregate_UnsupportedGranularityToken_ParseGranularityReturnsNull(string token)
    {
        // Unsupported tokens must return null — callers must handle this and
        // fall back themselves. ParseGranularity must not throw.
        Assert.Null(AccessLogAggregator.ParseGranularity(token));
    }

    [Fact]
    public void Aggregate_OneMinuteGranularity_Produces1440Buckets()
    {
        var path = LogFile("1m.log");
        File.WriteAllText(path, "");

        var result = AccessLogAggregator.Aggregate([path], TestDate, AccessLogAggregator.Granularity.OneMinute, Utc);

        Assert.Equal(1440, result.BucketCount); // 24h × 60min
        Assert.All(result.Series, s => Assert.Equal(1440, s.Data.Count));
    }

    [Fact]
    public void Aggregate_OneHourGranularity_BucketTimestampsAreOnTheHour()
    {
        var path = LogFile("1h-ts.log");
        File.WriteAllText(path, "");

        var result = AccessLogAggregator.Aggregate([path], TestDate, AccessLogAggregator.Granularity.OneHour, Utc);

        Assert.Equal(24, result.BucketCount);
        for (int i = 0; i < 24; i++)
        {
            var ts = result.Series[0].Data[i].Timestamp;
            Assert.Equal(0, ts.Minute);
            Assert.Equal(0, ts.Second);
            Assert.Equal(i, ts.Hour);
        }
    }

    [Fact]
    public void Aggregate_FifteenMinuteGranularity_Produces96Buckets()
    {
        var path = LogFile("15m.log");
        File.WriteAllText(path, "");

        var result = AccessLogAggregator.Aggregate([path], TestDate, AccessLogAggregator.Granularity.FifteenMinutes, Utc);

        Assert.Equal(96, result.BucketCount); // 24h × 4 buckets/h
        Assert.All(result.Series, s => Assert.Equal(96, s.Data.Count));
    }

    // ── Log content edge cases ─────────────────────────────────────────────────

    [Fact]
    public void Aggregate_MalformedLogLine_SkippedWithoutError()
    {
        var path = LogFile("malformed.log");
        File.WriteAllLines(path,
        [
            "this is not a valid log line at all",
            ":::garbage:::",
            MakeLine(new DateTime(2026, 4, 19, 6, 0, 0, DateTimeKind.Utc)), // valid — must be counted
            "another { malformed } line",
        ]);

        var result = Aggregate(path);

        long total = result.Series.First(s => s.Name == "requests").Data.Sum(p => p.Value);
        Assert.Equal(1, total); // only the valid line is counted; no exception thrown
    }

    [Fact]
    public void Aggregate_EmptyFile_SameAsMissingFile_AllZeros()
    {
        // File exists but has zero bytes — distinct from the missing-file case
        // but the contract (all buckets zero) must be the same.
        var path = LogFile("zero-bytes.log");
        File.WriteAllText(path, "");

        var result = Aggregate(path);

        Assert.Equal(288, result.BucketCount);
        Assert.All(result.Series, s => Assert.All(s.Data, p => Assert.Equal(0, p.Value)));
    }

    [Fact]
    public void Aggregate_LineWithMissingBytesField_CountedAsZeroBytes()
    {
        // Apache emits "-" for ResponseBytes on 304 Not Modified and some redirects.
        var ts = new DateTime(2026, 4, 19, 9, 0, 0, DateTimeKind.Utc);
        var timeStr = ts.ToString("dd/MMM/yyyy:HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        var line = $"127.0.0.1 - - [{timeStr} +0000] \"GET /favicon.ico HTTP/1.1\" 304 - \"-\" \"Browser/1.0\"";

        var path = LogFile("dash-bytes.log");
        File.WriteAllLines(path, [line]);

        var result = Aggregate(path);

        int bucket = (9 * 60) / 5; // 09:00 → bucket 108
        Assert.Equal(1, SeriesValue(result, "requests", bucket));
        Assert.Equal(0, SeriesValue(result, "bytes", bucket));
    }

    [Fact]
    public void Aggregate_LineWithQuotedUrlContainingBrackets_ParsesCorrectly()
    {
        // Square brackets in the URL query string must not confuse the timestamp regex
        // (which relies on [^\]] to find the timestamp field).
        var ts = new DateTime(2026, 4, 19, 10, 0, 0, DateTimeKind.Utc);
        var timeStr = ts.ToString("dd/MMM/yyyy:HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        var line = $"127.0.0.1 - - [{timeStr} +0000] \"GET /path?q=[test]&r=[foo] HTTP/1.1\" 200 512 \"-\" \"TestAgent/1.0\"";

        var path = LogFile("brackets-url.log");
        File.WriteAllLines(path, [line]);

        var result = Aggregate(path);

        int bucket = (10 * 60) / 5; // 10:00 → bucket 120
        Assert.Equal(1, SeriesValue(result, "requests", bucket));
    }

    [Fact]
    public void Aggregate_UnicodeInUrl_HandledCorrectly()
    {
        // Czech characters percent-encoded or raw in the URL must not break UTF-8 decode.
        var ts = new DateTime(2026, 4, 19, 11, 0, 0, DateTimeKind.Utc);
        var timeStr = ts.ToString("dd/MMM/yyyy:HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        var line = $"127.0.0.1 - - [{timeStr} +0000] \"GET /stranky/o-n%C3%A1s HTTP/1.1\" 200 1024 \"-\" \"TestAgent/1.0\"";

        var path = LogFile("unicode-url.log");
        File.WriteAllText(path, line + "\n", System.Text.Encoding.UTF8);

        var result = Aggregate(path);

        int bucket = (11 * 60) / 5; // 11:00 → bucket 132
        Assert.Equal(1, SeriesValue(result, "requests", bucket));
    }

    // ── Boundary / file-rotation tests ────────────────────────────────────────

    [Fact]
    public void DiscoverRotatedFiles_MtimeWindowExcludesTwoDaysOld()
    {
        // The mtime window is [date-1 day, date+2 days) in UTC.
        // A sibling file whose mtime is 2 days before the requested date must NOT
        // contribute entries — it falls before windowStart and is excluded.
        // A sibling file with mtime 1 day before (yesterday) IS inside the window
        // and its entries must be counted.
        var basePath = LogFile("rot.log");
        File.WriteAllText(basePath, ""); // active log, no entries

        // "yesterday" rotated file (1 day old) — within the window
        var oneDayOld = LogFile("rot.log.1");
        File.WriteAllLines(oneDayOld,
        [
            MakeLine(new DateTime(2026, 4, 19, 7, 0, 0, DateTimeKind.Utc)),
        ]);
        File.SetLastWriteTimeUtc(oneDayOld,
            TestDate.AddDays(-1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));

        // "two days old" rotated file — outside the window, must be skipped
        var twoDaysOld = LogFile("rot.log.2");
        File.WriteAllLines(twoDaysOld,
        [
            MakeLine(new DateTime(2026, 4, 19, 8, 0, 0, DateTimeKind.Utc)),
        ]);
        File.SetLastWriteTimeUtc(twoDaysOld,
            TestDate.AddDays(-2).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));

        var result = Aggregate(basePath);

        long total = result.Series.First(s => s.Name == "requests").Data.Sum(p => p.Value);
        // Only the entry from the 1-day-old file should be counted;
        // the 2-days-old file must be silently excluded.
        Assert.Equal(1, total);
    }

    [Fact]
    public void Aggregate_EntryExactlyAtMidnight_IncludedInFirstBucket()
    {
        // 00:00:00.000 UTC on the test day is the lower boundary of the day window.
        // The check is ts >= dayStartUtc, so this entry MUST land in bucket 0.
        var path = LogFile("midnight.log");
        File.WriteAllLines(path,
        [
            MakeLine(new DateTime(2026, 4, 19, 0, 0, 0, DateTimeKind.Utc)),
        ]);

        var result = Aggregate(path);

        Assert.Equal(1, SeriesValue(result, "requests", 0));
    }

    [Fact]
    public void Aggregate_EntryAtDayRolloverMinus1Second_ExcludedFromNextDay()
    {
        // 23:59:59 UTC on 2026-04-19 is inside the day window for TestDate.
        // It must NOT appear when we aggregate the following day (2026-04-20).
        var path = LogFile("rollover.log");
        File.WriteAllLines(path,
        [
            MakeLine(new DateTime(2026, 4, 19, 23, 59, 59, DateTimeKind.Utc)),
        ]);

        var nextDay = new DateOnly(2026, 4, 20);
        var result = AccessLogAggregator.Aggregate([path], nextDay, AccessLogAggregator.Granularity.FiveMinutes, Utc);

        long total = result.Series.First(s => s.Name == "requests").Data.Sum(p => p.Value);
        Assert.Equal(0, total);
    }
}
