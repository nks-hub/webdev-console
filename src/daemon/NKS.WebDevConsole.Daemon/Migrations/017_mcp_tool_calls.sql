-- ============================================================================
-- Migration 017 — full MCP tool call audit log.
--
-- Phase 8 (MCP redesign): the existing `deploy_intents` table only records
-- SIGNED requests for destructive actions. ~99% of MCP traffic is read-only
-- (list_sites, get_status) and never touched the audit log — operators had
-- no way to see what AI assistants were actually doing.
--
-- This table records every MCP tool call regardless of danger level so the
-- Activity feed can show a complete picture. Records are written by the
-- MCP server (via POST /api/mcp/tool-calls) AFTER the daemon-side action
-- completes — fire-and-forget so a logging failure can't break the call.
--
-- Retention is bounded: a sweeper trims rows older than `retention_days`
-- so the table stays cheap to query. Default 30 days; adjustable via the
-- `mcp.toolCallRetentionDays` setting.
-- ============================================================================

CREATE TABLE IF NOT EXISTS mcp_tool_calls (
    id              TEXT    PRIMARY KEY,
    called_at       TEXT    NOT NULL,
    -- MCP server-generated session identifier (one per MCP client connection).
    -- Allows grouping consecutive calls from the same AI assistant in the UI.
    session_id      TEXT,
    -- Caller identity if the MCP client identified itself (e.g. "claude-code").
    -- Defaults to "unknown" rather than NULL so filters don't need IS NULL handling.
    caller          TEXT    NOT NULL DEFAULT 'unknown',
    -- Tool name as registered in the MCP server (e.g. "wdc_list_sites").
    tool_name       TEXT    NOT NULL,
    -- First 500 chars of the JSON-stringified args, with secrets redacted by
    -- the MCP server before send. Used for the "what did it ask for" preview.
    args_summary    TEXT,
    -- SHA-256 of the FULL args JSON. Lets the UI collapse repeated identical
    -- read calls without storing the full payload N times.
    args_hash       TEXT,
    -- Wall-clock duration of the daemon-side handler.
    duration_ms     INTEGER NOT NULL DEFAULT 0,
    -- "ok" | "error" | "denied" (gated by intent system).
    result_code     TEXT    NOT NULL DEFAULT 'ok',
    -- Error.message (truncated to 500) when result_code != 'ok'.
    error_message   TEXT,
    -- "read" | "mutate" | "destructive" — set by the MCP server based on
    -- the tool's category. Lets the Activity feed collapse reads.
    danger_level    TEXT    NOT NULL DEFAULT 'read',
    -- Optional cross-link: when this call also produced a deploy_intents row
    -- (for destructive operations), reference it for drill-down.
    intent_id       TEXT
);

CREATE INDEX IF NOT EXISTS idx_mcp_tool_calls_called_at
    ON mcp_tool_calls (called_at DESC);

CREATE INDEX IF NOT EXISTS idx_mcp_tool_calls_session
    ON mcp_tool_calls (session_id, called_at);

CREATE INDEX IF NOT EXISTS idx_mcp_tool_calls_danger
    ON mcp_tool_calls (danger_level, called_at DESC);

CREATE INDEX IF NOT EXISTS idx_mcp_tool_calls_tool
    ON mcp_tool_calls (tool_name, called_at DESC);
