-- ============================================================================
-- Migration 020 — intent_requested_by: remember which MCP session and AI
-- client asked for operator approval
--
-- The approval banner offers "approve and stop asking" shortcuts that create
-- a trust grant. Without the caller identity the only possible scope was
-- 'always' (every AI client). `POST /api/mcp/intents/confirm-request` now
-- stores the X-Mcp-Session-Id / X-Mcp-Api-Key-Id the MCP server sent, so a
-- grant can be scoped to that run or that client, also for requests the GUI
-- loads after a restart.
--
-- Additive — DEFAULT NULL keeps existing rows valid without backfill.
-- ============================================================================

ALTER TABLE deploy_intents
    ADD COLUMN requested_by_session TEXT;

ALTER TABLE deploy_intents
    ADD COLUMN requested_by_client TEXT;
