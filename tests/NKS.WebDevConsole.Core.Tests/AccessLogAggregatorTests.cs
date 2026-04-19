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
}
