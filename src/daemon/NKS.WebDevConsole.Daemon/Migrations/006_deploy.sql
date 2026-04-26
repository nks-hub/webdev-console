-- ============================================================================
-- NKS WebDev Console Database — Deploy schema (Migration 006)
-- Tables: deploy_runs (run journal) + deploy_intents (HMAC-signed MCP intents)
--
-- Design notes:
--  - deploy_runs.id is a TEXT UUID (not INTEGER) so the daemon can mint IDs
--    client-side and round-trip them via the IDeployBackend contract before
--    SQLite assigns them. This is the same pattern the SDK's DeployRequest
--    expects (string DeployId, no auto-increment surprises).
--  - The deploy_runs row is itself the audit log for a deploy; we deliberately
--    DO NOT cross-write to config_history because that table's CHECK
--    constraints (entity_type IN ('setting','site',…), operation IN
--    ('INSERT','UPDATE','DELETE')) would reject our domain vocabulary and
--    extending them would force a non-additive migration on shipped DBs.
--    The triggered_by column captures the actor; status transitions are
--    visible in updated_at deltas.
--  - All triggers use IF NOT EXISTS for idempotent re-application during
--    dev resets, and the updated_at trigger uses WHEN OLD = NEW to avoid
--    infinite recursion (the standard wdc trigger pattern from 003).
-- ============================================================================

-- ----------------------------------------------------------------------------
-- deploy_runs — one row per deploy attempt (succeeded, failed, cancelled,
-- rolled-back). The daemon updates status as the run progresses. Reading
-- WHERE status='running' on startup powers the stale-deploy recovery flow.
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS deploy_runs (
    id              TEXT    NOT NULL PRIMARY KEY,                 -- UUID v4 minted by daemon
    domain          TEXT    NOT NULL,
    host            TEXT    NOT NULL,                             -- target host name from deploy.neon
    release_id      TEXT,                                         -- nksdeploy 'YYYYMMDD_HHMMSS' or backend equivalent
    branch          TEXT,
    commit_sha      TEXT,
    status          TEXT    NOT NULL DEFAULT 'queued',
    is_past_ponr    INTEGER NOT NULL DEFAULT 0,                   -- 0/1; flips to 1 after irreversible step
    started_at      TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    completed_at    TEXT,
    exit_code       INTEGER,
    error_message   TEXT,
    duration_ms     INTEGER,
    triggered_by    TEXT    NOT NULL DEFAULT 'gui',
    backend_id      TEXT    NOT NULL DEFAULT 'nks-deploy',        -- which IDeployBackend handled the run
    created_at      TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    updated_at      TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),

    CHECK (status IN (
        'queued',
        'running',
        'awaiting_soak',
        'completed',
        'failed',
        'cancelled',
        'rolling_back',
        'rolled_back'
    )),
    CHECK (is_past_ponr IN (0, 1)),
    CHECK (triggered_by IN ('gui', 'mcp', 'cli', 'restart_recovery'))
);

-- ----------------------------------------------------------------------------
-- deploy_intents — pre-signed MCP intents for headless / CI deploys
-- (Mode C from the v3 plan). The daemon validates HMAC + expiry + single-use
-- before consuming. nonce UNIQUE prevents replay.
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS deploy_intents (
    id              TEXT    NOT NULL PRIMARY KEY,                 -- UUID v4
    domain          TEXT    NOT NULL,
    host            TEXT    NOT NULL,
    release_id      TEXT,
    nonce           TEXT    NOT NULL UNIQUE,
    expires_at      TEXT    NOT NULL,                             -- ISO-8601 UTC
    hmac_signature  TEXT    NOT NULL,
    used_at         TEXT,                                         -- NULL until consumed (single-use)
    created_at      TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
);

-- ----------------------------------------------------------------------------
-- Indexes
-- Queries we expect to hit frequently:
--  - "deploys for site X" (history page filter, MCP wdc_deploy_history)
--  - "is anything running right now?" (startup recovery, lock state)
--  - "newest first" (history listing default order)
--  - "expire stale intents" (sweeper task)
-- ----------------------------------------------------------------------------
CREATE INDEX IF NOT EXISTS idx_deploy_runs_domain
    ON deploy_runs (domain);

CREATE INDEX IF NOT EXISTS idx_deploy_runs_running
    ON deploy_runs (status)
    WHERE status IN ('running', 'awaiting_soak', 'rolling_back');

CREATE INDEX IF NOT EXISTS idx_deploy_runs_started_at
    ON deploy_runs (started_at DESC);

CREATE INDEX IF NOT EXISTS idx_deploy_intents_expires_at
    ON deploy_intents (expires_at);

-- ----------------------------------------------------------------------------
-- Triggers
-- Bump updated_at when status (or any other column) changes. The
-- WHEN OLD.updated_at = NEW.updated_at guard prevents the recursive trigger
-- loop that would otherwise fire when the trigger itself updates updated_at.
-- ----------------------------------------------------------------------------
CREATE TRIGGER IF NOT EXISTS trg_deploy_runs_updated_at
    AFTER UPDATE ON deploy_runs
    FOR EACH ROW
    WHEN OLD.updated_at = NEW.updated_at
BEGIN
    UPDATE deploy_runs
    SET updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    WHERE id = NEW.id;
END;
