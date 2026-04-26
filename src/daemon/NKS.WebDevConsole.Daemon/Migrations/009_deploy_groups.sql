-- ============================================================================
-- Migration 009 — atomic multi-host deploy groups (Phase 6.1).
--
-- A `deploy_groups` row tracks the state of a fan-out across N hosts. Per-
-- host work still lives in `deploy_runs`; the new `group_id` FK lets us
-- collapse "show me everything that happened in group X" into one indexed
-- query.
--
-- Design notes:
--  - Hosts are stored as a JSON array string (hosts_json) rather than as a
--    join table because the GUI only ever reads them as a single payload
--    and we don't need to query "which groups include host Y" — that lookup
--    flows through deploy_runs anyway.
--  - deploy_ids_json is the {host: deployId} map that grows as each per-host
--    deploy starts. UPDATE rewrites the whole blob; the table is small
--    enough (one row per multi-host fan-out) that a JSON-patch primitive
--    isn't worth the SQLite version-floor pain.
--  - phase CHECK enumerates exactly the states the SDK enum exposes
--    (DeployGroupPhase). New states require a non-additive migration —
--    intentional, so the code-side enum and DB constraint can never drift.
--  - The partial index on active phases mirrors deploy_runs's
--    idx_deploy_runs_running so startup recovery is fast.
-- ============================================================================

CREATE TABLE IF NOT EXISTS deploy_groups (
    id              TEXT    NOT NULL PRIMARY KEY,
    domain          TEXT    NOT NULL,
    hosts_json      TEXT    NOT NULL,
    deploy_ids_json TEXT    NOT NULL DEFAULT '{}',
    phase           TEXT    NOT NULL DEFAULT 'initializing',
    started_at      TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    completed_at    TEXT,
    error_message   TEXT,
    triggered_by    TEXT    NOT NULL DEFAULT 'gui',
    created_at      TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    updated_at      TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),

    CHECK (phase IN (
        'initializing',
        'preflight',
        'deploying',
        'awaiting_all_soak',
        'all_succeeded',
        'partial_failure',
        'rolling_back_all',
        'rolled_back',
        'group_failed'
    )),
    CHECK (triggered_by IN ('gui', 'mcp', 'cli'))
);

-- Per-host deploy_runs row gets an optional FK to its group (NULL for
-- single-host deploys, non-NULL for fan-out children).
ALTER TABLE deploy_runs
    ADD COLUMN group_id TEXT REFERENCES deploy_groups(id);

CREATE INDEX IF NOT EXISTS idx_deploy_runs_group_id
    ON deploy_runs (group_id)
    WHERE group_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_deploy_groups_active
    ON deploy_groups (phase)
    WHERE phase IN (
        'initializing', 'preflight', 'deploying', 'awaiting_all_soak', 'rolling_back_all'
    );

-- Newest-first scan for the GUI's group history page.
CREATE INDEX IF NOT EXISTS idx_deploy_groups_started_at
    ON deploy_groups (started_at DESC);

-- updated_at bump trigger (same pattern as deploy_runs from migration 006).
CREATE TRIGGER IF NOT EXISTS trg_deploy_groups_updated_at
    AFTER UPDATE ON deploy_groups
    FOR EACH ROW
    WHEN OLD.updated_at = NEW.updated_at
BEGIN
    UPDATE deploy_groups
    SET updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    WHERE id = NEW.id;
END;
