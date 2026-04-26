-- ============================================================================
-- Migration 015 — intent_matched_grant: auto-confirm audit trail
--
-- Phase 7.5+++. When the validator's grant pre-check returns a hit and pre-
-- stamps `confirmed_at`, we currently lose track of WHICH grant did the
-- approving. The McpIntents inventory shows "confirmed" but not "by whom" —
-- forcing operators to manually correlate timestamps to figure out which
-- grant rule was responsible.
--
-- Adding a `matched_grant_id` column lets the validator stamp the grant id
-- alongside `confirmed_at` in the same UPDATE, giving operators a clean
-- audit chain: intent X was auto-confirmed by grant Y at time T. The
-- field stays NULL when the operator confirmed manually via the GUI banner.
--
-- Additive — DEFAULT NULL keeps existing rows valid without backfill, and
-- there's no FK to `mcp_session_grants` (grants get hard-deleted by the
-- janitor; we don't want CASCADE to wipe intent audit rows just because
-- the originating grant aged out).
-- ============================================================================

ALTER TABLE deploy_intents
    ADD COLUMN matched_grant_id TEXT;
