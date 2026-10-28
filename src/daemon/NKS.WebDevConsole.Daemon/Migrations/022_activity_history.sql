-- ============================================================================
-- Migration 022 — let config_history hold what the Overview "recent activity"
-- feed lists.
--
-- Until now only the settings/services/php_versions audit triggers wrote to
-- config_history, so the feed never showed a site, a backup or a database.
-- The daemon now records those itself (ConfigHistoryRepository). Three CHECK
-- constraints refused its vocabulary:
--   entity_type  + 'backup'
--   operation    + 'RESTORE'   (a backup restore is neither an update nor a delete)
--   source       + 'mcp'       (the daemon knows the MCP token from the GUI token)
-- entity_id gets DEFAULT 0 because sites, backups and databases live in files
-- or in MySQL and have no row id.
--
-- SQLite cannot alter a CHECK in place, so the table is rebuilt the way 011
-- did it. legacy_alter_table keeps RENAME from validating the audit triggers
-- and v_recent_changes, which refer to config_history while it does not exist.
-- Every existing row, index and trigger survives.
-- ============================================================================

PRAGMA legacy_alter_table = ON;

CREATE TABLE config_history__new (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    entity_type     TEXT    NOT NULL,
    entity_id       INTEGER NOT NULL DEFAULT 0,
    entity_name     TEXT,
    operation       TEXT    NOT NULL,
    old_values      TEXT,
    new_values      TEXT,
    changed_fields  TEXT,
    source          TEXT    NOT NULL DEFAULT 'app',
    ip_address      TEXT,
    user_agent      TEXT,
    created_at      TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),

    CHECK (entity_type IN ('setting', 'site', 'service', 'php_version', 'certificate', 'database', 'plugin', 'backup')),
    CHECK (operation IN ('INSERT', 'UPDATE', 'DELETE', 'RESTORE')),
    CHECK (old_values IS NULL OR json_valid(old_values)),
    CHECK (new_values IS NULL OR json_valid(new_values)),
    CHECK (changed_fields IS NULL OR json_valid(changed_fields)),
    CHECK (source IN ('app', 'api', 'cli', 'trigger', 'migration', 'import', 'mcp'))
);

INSERT INTO config_history__new
    (id, entity_type, entity_id, entity_name, operation, old_values, new_values,
     changed_fields, source, ip_address, user_agent, created_at)
SELECT
    id, entity_type, entity_id, entity_name, operation, old_values, new_values,
    changed_fields, source, ip_address, user_agent, created_at
FROM config_history;

DROP TABLE config_history;
ALTER TABLE config_history__new RENAME TO config_history;

CREATE INDEX IF NOT EXISTS idx_config_history_entity
    ON config_history (entity_type, entity_id);
CREATE INDEX IF NOT EXISTS idx_config_history_entity_name
    ON config_history (entity_name) WHERE entity_name IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_config_history_created_at
    ON config_history (created_at DESC);
CREATE INDEX IF NOT EXISTS idx_config_history_operation
    ON config_history (operation);
CREATE INDEX IF NOT EXISTS idx_config_history_entity_time
    ON config_history (entity_type, entity_id, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_config_history_source
    ON config_history (source);

PRAGMA legacy_alter_table = OFF;
