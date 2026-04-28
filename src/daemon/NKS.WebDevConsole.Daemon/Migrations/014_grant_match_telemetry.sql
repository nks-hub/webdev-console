-- ============================================================================
-- Migration 014 — grant_match_telemetry: match_count + last_matched_at
--
-- Phase 7.5+++. Once a grant is in the table, operators have no signal about
-- whether it's actually being used. A grant that's matched 47 times in the
-- last 24h is load-bearing; a grant minted three months ago that has never
-- matched anything is dead weight (and a small attack surface — every active
-- grant widens the auto-confirm path).
--
-- Tracking match telemetry per row gives operators concrete evidence to:
--   * Spot grants safe to revoke ("never matched + older than 7d")
--   * Audit who is leaning on grants vs going through the GUI banner
--   * Quantify the "was rolling out grants worth it?" UX question
--
-- Both columns are additive — DEFAULT 0 / NULL keeps existing rows valid
-- without backfill. The validator bumps them in the same transaction as
-- the auto-confirm UPDATE; if the validator path doesn't run (the grant
-- never matches anything), the columns stay at their DEFAULTs.
-- ============================================================================

ALTER TABLE mcp_session_grants
    ADD COLUMN match_count INTEGER NOT NULL DEFAULT 0;

ALTER TABLE mcp_session_grants
    ADD COLUMN last_matched_at TEXT;
