-- ============================================================================
-- Migration 010 — pre-deploy DB snapshot tracking (Phase 6.2).
--
-- The plugin's deploy backend can opt-in to take a database snapshot
-- BEFORE spawning the deploy subprocess. The result is recorded on the
-- deploy_runs row so the GUI history page + the MCP wdc_deploy_get_status
-- tool can surface "this deploy has a restorable snapshot at X".
--
-- Restore is operator-driven (separate Phase 6.3 endpoint) — we never
-- auto-restore on rollback because a DB restore is irreversible and the
-- operator must consciously choose between rollback-to-previous-release
-- (preserving DB state) and rollback-with-DB-restore.
-- ============================================================================

ALTER TABLE deploy_runs
    ADD COLUMN pre_deploy_backup_path TEXT;

ALTER TABLE deploy_runs
    ADD COLUMN pre_deploy_backup_size_bytes INTEGER;

-- Look up "deploys with restorable snapshots" for the GUI history filter.
CREATE INDEX IF NOT EXISTS idx_deploy_runs_pre_deploy_backup
    ON deploy_runs (pre_deploy_backup_path)
    WHERE pre_deploy_backup_path IS NOT NULL;
