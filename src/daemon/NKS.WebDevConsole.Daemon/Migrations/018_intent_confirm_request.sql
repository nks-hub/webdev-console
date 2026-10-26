-- ============================================================================
-- Migration 018 — intent_confirm_request: remember which intents asked for
-- operator approval, and what the agent said
--
-- `POST /api/mcp/intents/confirm-request` only broadcast an SSE event. A WDC
-- window that was closed or reloading when it fired never showed the banner,
-- and the request could not be approved anywhere because nothing recorded
-- that it was waiting for a human. The GUI now loads pending requests from
-- `GET /api/mcp/intents?pendingOnly=true` on start and on daemon reconnect;
-- these columns let it pick exactly the intents that asked for approval
-- (GUI/CI-minted intents never do) and show the agent's description.
--
-- Additive — DEFAULT NULL keeps existing rows valid without backfill.
-- ============================================================================

ALTER TABLE deploy_intents
    ADD COLUMN confirm_requested_at TEXT;

ALTER TABLE deploy_intents
    ADD COLUMN prompt TEXT;
