using System.Globalization;

namespace NKS.WebDevConsole.Daemon.Services;

/// <summary>
/// Pure delta-computation helper for the <c>/api/sites/{domain}/metrics/history</c>
/// endpoint. Lives outside <c>Program.cs</c> so it can be unit-tested without
/// spinning up the web host.
///
/// The helper walks a time-sorted sequence of raw metrics_history rows and
/// produces one <see cref="HistorySample"/> per valid row with a sliding
/// pairwise delta → <c>requestsPerMin</c> rate.
///
/// Invariants enforced here (each has a regression test):
/// 1. First valid row gets <c>requestsPerMin = 0</c> — no baseline available.
/// 2. Negative count deltas (e.g. log rotation reset the counter) are
///    clamped to 0 so the chart never shows negative rates.
/// 3. Bad/malformed timestamps are skipped entirely — never used as a
///    baseline and never emitted. Missing this check (original bug,
///    commit <c>0e28cc2</c>) caused <c>default(DateTime)</c> (year 0001) to
///    poison every subsequent delta in the same window.
/// 4. Zero time gap between two samples yields <c>requestsPerMin = 0</c>
///    (avoid divide-by-zero).
/// </summary>
public static class MetricsHistoryAggregator
{
    public readonly record struct RawRow(
        string SampledAt,
        long RequestCount,
        long SizeBytes,
        string? LastWriteUtc);

    public sealed record HistorySample(
        string SampledAt,
        long RequestCount,
        long SizeBytes,
        string? LastWriteUtc,
        double RequestsPerMin);

    public static IReadOnlyList<HistorySample> ComputeDeltas(IEnumerable<RawRow> rows)
    {
        var samples = new List<HistorySample>();
        long? prevCount = null;
        DateTime? prevTime = null;

        foreach (var r in rows)
        {
            if (!DateTime.TryParse(r.SampledAt, null, DateTimeStyles.RoundtripKind, out var t))
            {
                // Bad timestamp — skip this row entirely. Never advance the
                // baseline from a row we couldn't place on the time axis.
                continue;
            }

            double requestsPerMin = 0;
            if (prevCount.HasValue && prevTime.HasValue)
            {
                var deltaCount = Math.Max(0, r.RequestCount - prevCount.Value);
                var deltaSec = (t - prevTime.Value).TotalSeconds;
                if (deltaSec > 0)
                {
                    requestsPerMin = deltaCount * 60.0 / deltaSec;
                }
            }

            samples.Add(new HistorySample(
                r.SampledAt,
                r.RequestCount,
                r.SizeBytes,
                r.LastWriteUtc,
                requestsPerMin));

            prevCount = r.RequestCount;
            prevTime = t;
        }

        return samples;
    }
}
