-- ============================================================================
-- Migration 012 — mcp_session_grants: persistent trust for MCP destructive ops
--
-- Phase 7.3. Today every destructive MCP intent must be confirmed via the GUI
-- banner (`mcp:confirm-request` SSE → 425 Too Early until confirmed_at is
-- stamped). That works for one-off operator review but is hostile for trusted
-- agents that operate on a long-running session — every chained step pops a
-- new banner.
--
-- A grant is a stored "I trust caller X to perform kind Y against target Z
-- for the next N minutes (or forever)" rule. The intent validator consults
-- the grants table BEFORE returning `pending_confirmation`; a matching grant
-- auto-stamps confirmed_at and the intent proceeds without a banner click.
--
-- Scope axes (any combination via wildcards):
--   * scope_type   = 'session'  → match by mcp_session_id (the 32-byte token
--                                  the MCP server hands the agent at start)
--                  = 'api_key'  → match by api_key_id (durable identity, e.g.
--                                  "claude-code-LuRy" — a fingerprint of the
--                                  API key, never the key itself)
--                  = 'instance' → match by wdc instance UUID (anyone using
--                                  this WDC install; coarsest grant)
--                  = 'always'   → ignore caller identity entirely (most
--                                  permissive; UI requires extra confirmation)
--   * kind_pattern = '*' or a specific intent kind ('deploy', 'restore', …)
--   * target_pattern = '*' or a specific target identifier (currently
--                      "{domain}" for deploy intents; future categories
--                      may use "site:blog.loc", "db:foo", etc.)
--
-- Lookup is "first non-revoked, non-expired grant whose patterns all match",
-- with NULL expires_at meaning permanent. The partial index on (scope_type,
-- scope_value) keeps the hot lookup tight.
-- ============================================================================

CREATE TABLE IF NOT EXISTS mcp_session_grants (
    id              TEXT    NOT NULL PRIMARY KEY,                 -- UUID v4
    scope_type      TEXT    NOT NULL,
    scope_value     TEXT,                                         -- NULL for 'always'
    kind_pattern    TEXT    NOT NULL DEFAULT '*',
    target_pattern  TEXT    NOT NULL DEFAULT '*',
    granted_at      TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    expires_at      TEXT,                                         -- NULL = permanent
    granted_by      TEXT    NOT NULL DEFAULT 'gui',
    revoked_at      TEXT,                                         -- NULL = active
    note            TEXT,                                         -- free-form operator note

    CHECK (scope_type IN ('session', 'instance', 'api_key', 'always')),
    -- 'always' grants must have NULL scope_value; everything else must have one.
    CHECK (
        (scope_type = 'always' AND scope_value IS NULL) OR
        (scope_type != 'always' AND scope_value IS NOT NULL AND length(scope_value) > 0)
    )
);

-- Hot path: validator looks up "is there an active grant for this caller
-- that covers this kind+target?". Partial index over active rows only.
CREATE INDEX IF NOT EXISTS idx_mcp_session_grants_active
    ON mcp_session_grants (scope_type, scope_value)
    WHERE revoked_at IS NULL;

-- For the GUI grants page (list newest first, group by scope).
CREATE INDEX IF NOT EXISTS idx_mcp_session_grants_granted_at
    ON mcp_session_grants (granted_at DESC);
