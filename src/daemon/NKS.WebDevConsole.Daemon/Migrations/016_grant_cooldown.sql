-- ============================================================================
-- Migration 016 — grant_cooldown: per-grant rate limit
--
-- Phase 7.5+++. A grant currently auto-confirms ANY matching intent the
-- moment it fires. For AI clients that loop (test suite, agent retry),
-- this can mean dozens of destructive ops in seconds without ever
-- bothering the operator. The grant was supposed to be a per-rule trust
-- decision, not a rubber stamp on a flood.
--
-- A `min_cooldown_seconds` column lets the operator specify "after one
-- match, this grant is dormant for N seconds". The validator skips the
-- grant if `last_matched_at + cooldown > now`, falling back to the GUI
-- banner — which is exactly what operators want when an AI suddenly
-- starts hammering: human in the loop until they decide if it's normal.
--
-- Defaults:
--   * 0 = no cooldown (current behaviour, backwards compatible)
--   * Operator sets a positive value when minting a grant they expect
--     to fire occasionally (deploy: 60s feels right for a CI pipeline
--     that retries on transient failure; restore: 600s for a destructive
--     op that should never burst)
--
-- Additive — DEFAULT 0 keeps existing rows valid without backfill.
-- ============================================================================

ALTER TABLE mcp_session_grants
    ADD COLUMN min_cooldown_seconds INTEGER NOT NULL DEFAULT 0;
