-- ============================================================================
-- Migration 011 — extend deploy_intents.kind CHECK to allow 'restore'.
--
-- Phase 6.4 introduces operator-driven snapshot restore as its own intent
-- kind so a deploy/rollback intent can never be silently re-used to
-- overwrite live data. SQLite cannot ALTER a CHECK constraint in place,
-- so we rebuild the table via the canonical 12-step pattern.
--
-- Additive only — every previously-issued intent retains its rows; the
-- nonce UNIQUE / FK relationships unchanged.
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
    kind            TEXT    NOT NULL DEFAULT 'deploy'
        CHECK (kind IN ('deploy', 'rollback', 'cancel', 'restore')),
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
