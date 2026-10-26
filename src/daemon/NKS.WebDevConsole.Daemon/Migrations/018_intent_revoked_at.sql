-- ============================================================================
-- Migration 018 — intent_revoked_at: tell a revoked intent from a used one
--
-- POST /api/mcp/intents/{id}/revoke neutralises an intent by stamping
-- `used_at`, the same column the validator stamps when the intent is
-- consumed. GET /api/mcp/intents/{id} reports the intent's state to the MCP
-- agent, which needs to know whether the user revoked the request (do not
-- retry) or it was already used. The revoke endpoint now stamps both columns,
-- so every existing check on `used_at` keeps refusing revoked intents.
--
-- Additive — DEFAULT NULL keeps existing rows valid without backfill.
-- ============================================================================

ALTER TABLE deploy_intents
    ADD COLUMN revoked_at TEXT;
