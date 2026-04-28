-- ============================================================================
-- Migration 007 — extend deploy_intents with `kind` column.
--
-- The original 006 schema scoped intents to (domain, host, release_id) only,
-- which means a single token could be replayed across deploy / rollback /
-- cancel for the same target. Phase 4d's MCP destructive guard needs to bind
-- each intent to one verb, so we add an explicit `kind` column with a CHECK
-- constraint matching the IDeployIntentValidator contract.
--
-- Additive-only: existing rows from a daemon that already shipped 006 (none
-- in production yet, but staging may have some) get `kind='deploy'` by
-- default. This matches the only verb that 006 ever supported in practice.
-- ============================================================================

-- SQLite supports ALTER TABLE ADD COLUMN with constraints since 3.25.
-- The default value is needed so the column NOT NULL CHECK can be applied
-- to rows inserted before the migration.
ALTER TABLE deploy_intents
    ADD COLUMN kind TEXT NOT NULL DEFAULT 'deploy'
    CHECK (kind IN ('deploy', 'rollback', 'cancel'));

-- Useful when sweeping expired intents by verb.
CREATE INDEX IF NOT EXISTS idx_deploy_intents_kind
    ON deploy_intents (kind);
