using NKS.WebDevConsole.Daemon.Services;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Pure unit tests for <see cref="MetricsHistoryAggregator.ComputeDeltas"/>.
/// These cover the delta-rate math used by the <c>/api/sites/{domain}/metrics/history</c>
/// endpoint. The logic was previously inline in Program.cs and was the site
/// of a silent bug (commit 0e28cc2) where <see cref="DateTime.TryParse"/>'s
/// return value was ignored and bad timestamps poisoned every subsequent
/// rate calculation. Every invariant has a dedicated test here.
/// </summary>
public sealed class MetricsHistoryAggregatorTests
{
    private static MetricsHistoryAggregator.RawRow Row(
        string t, long count = 0, long bytes = 0, string? lw = null) =>
        new(t, count, bytes, lw);

    [Fact]
    public void Empty_ReturnsEmpty()
    {
        var result = MetricsHistoryAggregator.ComputeDeltas(Array.Empty<MetricsHistoryAggregator.RawRow>());
        Assert.Empty(result);
    }

    [Fact]
    public void SingleRow_HasZeroRequestsPerMin()
    {
        var result = MetricsHistoryAggregator.ComputeDeltas(new[]
        {
            Row("2026-04-13T10:00:00Z", count: 500),
        });
        var s = Assert.Single(result);
        Assert.Equal("2026-04-13T10:00:00Z", s.SampledAt);
        Assert.Equal(500, s.RequestCount);
        Assert.Equal(0, s.RequestsPerMin);
    }

    [Fact]
    public void TwoRowsOneMinuteApart_ComputesRate()
    {
        // 100 requests over 60 seconds = 100 rpm
        var result = MetricsHistoryAggregator.ComputeDeltas(new[]
        {
            Row("2026-04-13T10:00:00Z", count: 0),
            Row("2026-04-13T10:01:00Z", count: 100),
        });
        Assert.Equal(2, result.Count);
        Assert.Equal(0, result[0].RequestsPerMin);
        Assert.Equal(100, result[1].RequestsPerMin);
    }

    [Fact]
    public void HalfMinuteGap_ScalesRate()
    {
        // 50 requests over 30 seconds = 100 rpm
        var result = MetricsHistoryAggregator.ComputeDeltas(new[]
        {
            Row("2026-04-13T10:00:00Z", count: 1000),
            Row("2026-04-13T10:00:30Z", count: 1050),
        });
        Assert.Equal(100, result[1].RequestsPerMin);
    }

    [Fact]
    public void CounterReset_ClampedToZero()
    {
        // Apache log rotation — counter goes from 5000 back to 10.
        // Naive (delta = 10 - 5000) would report negative rate; we clamp to 0.
        var result = MetricsHistoryAggregator.ComputeDeltas(new[]
        {
            Row("2026-04-13T10:00:00Z", count: 5000),
            Row("2026-04-13T10:01:00Z", count: 10),
        });
        Assert.Equal(2, result.Count);
        Assert.Equal(0, result[1].RequestsPerMin);
        // Second row's count must still be 10 — we don't lie about the raw value.
        Assert.Equal(10, result[1].RequestCount);
    }

    [Fact]
    public void BadTimestamp_SkippedEntirely_DoesNotPoisonSubsequent()
    {
        // Regression test for commit 0e28cc2: a row with an unparseable
        // timestamp must not advance the rolling baseline, otherwise the
        // next valid row computes its delta against default(DateTime)
        // (year 0001) and produces a huge bogus requestsPerMin.
        var result = MetricsHistoryAggregator.ComputeDeltas(new[]
        {
            Row("2026-04-13T10:00:00Z", count: 0),
            Row("not-a-timestamp", count: 9999),
            Row("2026-04-13T10:01:00Z", count: 60),
        });
        Assert.Equal(2, result.Count);
        // Second emitted sample computed against the FIRST (valid) row,
        // not the skipped bad one: 60 requests / 60 seconds = 60 rpm.
        Assert.Equal("2026-04-13T10:01:00Z", result[1].SampledAt);
        Assert.Equal(60, result[1].RequestsPerMin);
    }

    [Fact]
    public void AllBadTimestamps_ReturnsEmpty()
    {
        var result = MetricsHistoryAggregator.ComputeDeltas(new[]
        {
            Row("garbage-1", count: 1),
            Row("garbage-2", count: 2),
            Row("", count: 3),
        });
        Assert.Empty(result);
    }

    [Fact]
    public void IdenticalTimestamps_ZeroGap_RequestsPerMinIsZero()
    {
        // If two samples land in the exact same instant (shouldn't happen
        // in practice but let's not divide by zero), rate = 0.
        var result = MetricsHistoryAggregator.ComputeDeltas(new[]
        {
            Row("2026-04-13T10:00:00Z", count: 0),
            Row("2026-04-13T10:00:00Z", count: 500),
        });
        Assert.Equal(2, result.Count);
        Assert.Equal(0, result[1].RequestsPerMin);
    }

    [Fact]
    public void PreservesRawFields_PassThrough()
    {
        // sizeBytes and lastWriteUtc must round-trip unchanged.
        var result = MetricsHistoryAggregator.ComputeDeltas(new[]
        {
            Row("2026-04-13T10:00:00Z", count: 1, bytes: 12345, lw: "2026-04-13T09:59:59Z"),
        });
        var s = Assert.Single(result);
        Assert.Equal(12345, s.SizeBytes);
        Assert.Equal("2026-04-13T09:59:59Z", s.LastWriteUtc);
    }

    [Fact]
    public void LongWindow_ComputesPerPairDeltas()
    {
        // Four samples over four minutes with varying rates.
        var result = MetricsHistoryAggregator.ComputeDeltas(new[]
        {
            Row("2026-04-13T10:00:00Z", count: 0),
            Row("2026-04-13T10:01:00Z", count: 30),   // 30 rpm
            Row("2026-04-13T10:02:00Z", count: 120),  // 90 rpm
            Row("2026-04-13T10:03:00Z", count: 120),  // 0 rpm (idle)
        });
        Assert.Equal(4, result.Count);
        Assert.Equal(0, result[0].RequestsPerMin);
        Assert.Equal(30, result[1].RequestsPerMin);
        Assert.Equal(90, result[2].RequestsPerMin);
        Assert.Equal(0, result[3].RequestsPerMin);
    }
}
