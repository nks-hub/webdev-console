-- ============================================================================
-- Migration 008 — record GUI confirmation on a deploy_intents row.
--
-- Phase 5.5 (Mode A hybrid confirmation): when an MCP client crafts a
-- destructive intent the daemon now broadcasts an `mcp:confirm-request`
-- SSE event that the wdc GUI surfaces as a banner. Approving the banner
-- stamps `confirmed_at` here; the destructive route refuses to fire
-- (425 Too Early) until that timestamp is non-null OR the caller opts
-- out via the `X-Allow-Unconfirmed: true` header (CI / headless flows
-- gated separately by `MCP_DEPLOY_AUTO_APPROVE`).
--
-- Additive only — pre-existing rows from a daemon that already shipped
-- 006/007 simply have NULL confirmed_at and would now block any
-- in-flight unconfirmed intents from succeeding. Acceptable: any such
-- intent was either already used (used_at NOT NULL) or expired by now.
-- ============================================================================

ALTER TABLE deploy_intents
    ADD COLUMN confirmed_at TEXT;

-- Partial index for the "needs GUI banner" lookup. Most rows hit
-- confirmed_at IS NOT NULL within seconds of the broadcast, so the
-- index stays tiny and selective.
CREATE INDEX IF NOT EXISTS idx_deploy_intents_unconfirmed
    ON deploy_intents (id)
    WHERE confirmed_at IS NULL;
