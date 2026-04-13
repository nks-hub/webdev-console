-- Phase 11 perf monitoring: server-side request-count history.
-- Append-only time series so the metrics tab can render windows longer
-- than the 5-minute client-side ring buffer (e.g. last hour, last day).
--
-- Bookkeeping: a background poller writes one row per (site, sample-tick)
-- with the cumulative request count at that moment. Rate-per-minute is
-- computed by the read endpoint as the delta between consecutive rows.
-- Storing cumulative counts (instead of pre-computed rates) means we can
-- aggregate at any window granularity without re-walking raw logs.
--
-- Retention: the poller prunes rows older than 7 days at the end of each
-- tick — keeps the table bounded for long-running daemons without giving
-- up the "last day" view that powers the dashboard.
CREATE TABLE IF NOT EXISTS metrics_history (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    domain        TEXT    NOT NULL,
    sampled_at    TEXT    NOT NULL,  -- ISO-8601 UTC
    request_count INTEGER NOT NULL,  -- cumulative line count of access log
    size_bytes    INTEGER NOT NULL,  -- cumulative bytes
    last_write_utc TEXT
);

-- Range queries scope by (domain, sampled_at) — covering index for the
-- typical "last N samples for site X" read pattern.
CREATE INDEX IF NOT EXISTS idx_metrics_history_domain_time
    ON metrics_history(domain, sampled_at DESC);
