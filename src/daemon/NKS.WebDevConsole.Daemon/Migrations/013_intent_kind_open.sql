-- ============================================================================
-- Migration 013 — relax deploy_intents.kind CHECK to allow plugin-defined kinds.
--
-- Phase 7.4 step 1. The table started as deploy-specific (006), then grew
-- 'restore' (011). Each new destructive operation kind required a daemon
-- migration. That breaks the user's actual ask in Phase 7: "MCP intents
-- should be globally usable for other potentially-dangerous operations
-- through MCP" — meaning ANY plugin should be able to mint intents for
-- its own kinds (db:drop_table, site:delete, plugin:reset, …) without
-- shipping a daemon-side migration.
--
-- The new constraint is a charset+length sanity check, not a value
-- whitelist: lowercase letters / digits / underscore / colon, 1-64 chars,
-- starting with a letter. The colon is the conventional namespace
-- separator (e.g. "deploy:full", "db:drop_table"). App-layer validation
-- in /api/mcp/intents enforces the same regex so daemon and schema
-- agree on what's a valid kind.
--
-- Existing rows ('deploy'/'rollback'/'cancel'/'restore') trivially pass
-- the new charset rule, so the rebuild is data-preserving.
--
-- SQLite cannot ALTER a CHECK in place — same 12-step rebuild pattern as
-- migration 011.
-- ============================================================================

PRAGMA foreign_keys = OFF;

CREATE TABLE deploy_intents__new (
    id              TEXT    NOT NULL PRIMARY KEY,
    domain          TEXT    NOT NULL,
    host            TEXT    NOT NULL,
    release_id      TEXT,
    nonce           TEXT    NOT NULL UNIQUE,
    expires_at      TEXT    NOT NULL,
    hmac_signature  TEXT    NOT NULL,
    used_at         TEXT,
    -- Phase 7.4: kind is now an open namespace ("deploy", "db:drop_table",
    -- "plugin:reset", …). Charset CHECK keeps the column safe to interpolate
    -- into log lines / SSE events / file paths without escaping concerns.
    kind            TEXT    NOT NULL DEFAULT 'deploy'
        CHECK (
            length(kind) BETWEEN 1 AND 64
            AND kind GLOB '[a-z]*'
            AND NOT kind GLOB '*[^a-z0-9_:]*'
        ),
    confirmed_at    TEXT,
    created_at      TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
);

INSERT INTO deploy_intents__new
    (id, domain, host, release_id, nonce, expires_at, hmac_signature, used_at, kind, confirmed_at, created_at)
SELECT
    id, domain, host, release_id, nonce, expires_at, hmac_signature, used_at, kind, confirmed_at, created_at
FROM deploy_intents;

DROP TABLE deploy_intents;
ALTER TABLE deploy_intents__new RENAME TO deploy_intents;

CREATE INDEX IF NOT EXISTS idx_deploy_intents_expires_at
    ON deploy_intents (expires_at);
CREATE INDEX IF NOT EXISTS idx_deploy_intents_kind
    ON deploy_intents (kind);
CREATE INDEX IF NOT EXISTS idx_deploy_intents_unconfirmed
    ON deploy_intents (id)
    WHERE confirmed_at IS NULL;

PRAGMA foreign_keys = ON;
