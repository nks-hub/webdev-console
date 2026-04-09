-- ============================================================================
-- DevForge Database Schema - Initial Migration
-- Version: 001
-- Engine: SQLite 3.38+ (requires json_valid, STRICT mode awareness)
-- Date format: ISO 8601 TEXT (YYYY-MM-DDTHH:MM:SS.sssZ)
-- ============================================================================

PRAGMA foreign_keys = ON;
PRAGMA journal_mode = WAL;
PRAGMA busy_timeout = 5000;

-- ============================================================================
-- schema_migrations — tracks applied migrations
-- ============================================================================
CREATE TABLE IF NOT EXISTS schema_migrations (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    version     TEXT    NOT NULL UNIQUE,
    name        TEXT    NOT NULL,
    applied_at  TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    checksum    TEXT,
    execution_ms INTEGER,

    CHECK (version GLOB '[0-9][0-9][0-9]*')
);

-- ============================================================================
-- settings — key/value global configuration, grouped by category
-- ============================================================================
CREATE TABLE settings (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    category    TEXT    NOT NULL,
    key         TEXT    NOT NULL,
    value       TEXT    NOT NULL,
    value_type  TEXT    NOT NULL DEFAULT 'string',
    description TEXT,
    is_readonly INTEGER NOT NULL DEFAULT 0,
    created_at  TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    updated_at  TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),

    UNIQUE (category, key),
    CHECK (value_type IN ('string', 'integer', 'boolean', 'json', 'path')),
    CHECK (is_readonly IN (0, 1)),
    CHECK (category <> ''),
    CHECK (key <> ''),
    CHECK (
        (value_type != 'json')
        OR json_valid(value)
    ),
    CHECK (
        (value_type != 'boolean')
        OR value IN ('true', 'false', '0', '1')
    )
);

-- ============================================================================
-- php_versions — installed PHP versions with extensions and ini overrides
-- ============================================================================
CREATE TABLE php_versions (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    version         TEXT    NOT NULL UNIQUE,
    install_path    TEXT    NOT NULL,
    binary_path     TEXT    NOT NULL,
    fpm_binary_path TEXT,
    extensions      TEXT    NOT NULL DEFAULT '[]',
    ini_overrides   TEXT    NOT NULL DEFAULT '{}',
    is_default      INTEGER NOT NULL DEFAULT 0,
    is_active       INTEGER NOT NULL DEFAULT 1,
    installed_at    TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    created_at      TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    updated_at      TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),

    CHECK (version GLOB '[0-9]*.[0-9]*' OR version GLOB '[0-9]*.[0-9]*.[0-9]*'),
    CHECK (is_default IN (0, 1)),
    CHECK (is_active IN (0, 1)),
    CHECK (json_valid(extensions)),
    CHECK (json_valid(ini_overrides)),
    CHECK (install_path <> ''),
    CHECK (binary_path <> '')
);

-- ============================================================================
-- services — managed services (Apache, Nginx, MySQL, MariaDB, PHP-FPM, dnsmasq)
-- ============================================================================
CREATE TABLE services (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    name            TEXT    NOT NULL UNIQUE,
    display_name    TEXT    NOT NULL,
    service_type    TEXT    NOT NULL,
    binary_path     TEXT,
    config_path     TEXT,
    pid_file        TEXT,
    log_path        TEXT,
    port            INTEGER,
    ssl_port        INTEGER,
    status          TEXT    NOT NULL DEFAULT 'stopped',
    auto_start      INTEGER NOT NULL DEFAULT 0,
    config_overrides TEXT   NOT NULL DEFAULT '{}',
    last_started_at TEXT,
    last_stopped_at TEXT,
    last_error      TEXT,
    created_at      TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    updated_at      TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),

    CHECK (service_type IN ('webserver', 'database', 'php-fpm', 'dns', 'cache', 'mail', 'custom')),
    CHECK (status IN ('running', 'stopped', 'starting', 'stopping', 'error', 'unknown')),
    CHECK (auto_start IN (0, 1)),
    CHECK (port IS NULL OR (port >= 1 AND port <= 65535)),
    CHECK (ssl_port IS NULL OR (ssl_port >= 1 AND ssl_port <= 65535)),
    CHECK (port IS NULL OR ssl_port IS NULL OR port != ssl_port),
    CHECK (json_valid(config_overrides)),
    CHECK (name <> ''),
    CHECK (display_name <> '')
);

-- ============================================================================
-- sites — virtual hosts (domain, docroot, PHP version, SSL, status)
-- ============================================================================
CREATE TABLE sites (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    domain          TEXT    NOT NULL UNIQUE,
    aliases         TEXT    NOT NULL DEFAULT '[]',
    document_root   TEXT    NOT NULL,
    webserver       TEXT    NOT NULL DEFAULT 'apache',
    php_version_id  INTEGER,
    ssl_enabled     INTEGER NOT NULL DEFAULT 0,
    certificate_id  INTEGER,
    status          TEXT    NOT NULL DEFAULT 'active',
    auto_created    INTEGER NOT NULL DEFAULT 0,
    nginx_config    TEXT,
    apache_config   TEXT,
    env_variables   TEXT    NOT NULL DEFAULT '{}',
    notes           TEXT,
    created_at      TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    updated_at      TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),

    FOREIGN KEY (php_version_id) REFERENCES php_versions(id) ON DELETE SET NULL,
    FOREIGN KEY (certificate_id) REFERENCES certificates(id) ON DELETE SET NULL,

    CHECK (domain <> ''),
    CHECK (document_root <> ''),
    CHECK (webserver IN ('apache', 'nginx')),
    CHECK (ssl_enabled IN (0, 1)),
    CHECK (auto_created IN (0, 1)),
    CHECK (status IN ('active', 'inactive', 'error', 'maintenance')),
    CHECK (json_valid(aliases)),
    CHECK (json_valid(env_variables)),
    CHECK (
        (ssl_enabled = 0)
        OR (ssl_enabled = 1 AND certificate_id IS NOT NULL)
    )
);

-- ============================================================================
-- certificates — SSL certificates per domain
-- ============================================================================
CREATE TABLE certificates (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    domain          TEXT    NOT NULL,
    cert_path       TEXT    NOT NULL,
    key_path        TEXT    NOT NULL,
    ca_bundle_path  TEXT,
    issuer          TEXT    NOT NULL DEFAULT 'self-signed',
    serial_number   TEXT,
    fingerprint     TEXT,
    valid_from      TEXT    NOT NULL,
    valid_until     TEXT    NOT NULL,
    is_wildcard     INTEGER NOT NULL DEFAULT 0,
    auto_renew      INTEGER NOT NULL DEFAULT 0,
    status          TEXT    NOT NULL DEFAULT 'valid',
    created_at      TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    updated_at      TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),

    CHECK (domain <> ''),
    CHECK (cert_path <> ''),
    CHECK (key_path <> ''),
    CHECK (issuer IN ('self-signed', 'mkcert', 'letsencrypt', 'acme', 'custom')),
    CHECK (is_wildcard IN (0, 1)),
    CHECK (auto_renew IN (0, 1)),
    CHECK (status IN ('valid', 'expired', 'revoked', 'pending')),
    CHECK (valid_from < valid_until)
);

-- ============================================================================
-- databases — managed MySQL/MariaDB databases
-- ============================================================================
CREATE TABLE databases (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    name            TEXT    NOT NULL,
    service_id      INTEGER NOT NULL,
    charset         TEXT    NOT NULL DEFAULT 'utf8mb4',
    collation       TEXT    NOT NULL DEFAULT 'utf8mb4_unicode_ci',
    size_bytes      INTEGER,
    table_count     INTEGER,
    site_id         INTEGER,
    username        TEXT,
    password_hash   TEXT,
    notes           TEXT,
    created_at      TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    updated_at      TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),

    FOREIGN KEY (service_id) REFERENCES services(id) ON DELETE CASCADE,
    FOREIGN KEY (site_id)    REFERENCES sites(id)    ON DELETE SET NULL,

    UNIQUE (name, service_id),
    CHECK (name <> ''),
    CHECK (name NOT GLOB '*[^a-zA-Z0-9_]*'),
    CHECK (charset <> ''),
    CHECK (collation <> ''),
    CHECK (size_bytes IS NULL OR size_bytes >= 0),
    CHECK (table_count IS NULL OR table_count >= 0)
);

-- ============================================================================
-- plugins — installed plugins
-- ============================================================================
CREATE TABLE plugins (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    slug            TEXT    NOT NULL UNIQUE,
    name            TEXT    NOT NULL,
    version         TEXT    NOT NULL,
    description     TEXT,
    author          TEXT,
    homepage_url    TEXT,
    install_path    TEXT    NOT NULL,
    entry_point     TEXT,
    config          TEXT    NOT NULL DEFAULT '{}',
    permissions     TEXT    NOT NULL DEFAULT '[]',
    is_enabled      INTEGER NOT NULL DEFAULT 1,
    is_builtin      INTEGER NOT NULL DEFAULT 0,
    installed_at    TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    updated_at      TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),

    CHECK (slug <> ''),
    CHECK (slug NOT GLOB '*[^a-z0-9-]*'),
    CHECK (name <> ''),
    CHECK (version <> ''),
    CHECK (install_path <> ''),
    CHECK (is_enabled IN (0, 1)),
    CHECK (is_builtin IN (0, 1)),
    CHECK (json_valid(config)),
    CHECK (json_valid(permissions))
);

-- ============================================================================
-- config_history — audit trail for configuration changes
-- ============================================================================
CREATE TABLE config_history (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    entity_type     TEXT    NOT NULL,
    entity_id       INTEGER NOT NULL,
    entity_name     TEXT,
    operation       TEXT    NOT NULL,
    old_values      TEXT,
    new_values      TEXT,
    changed_fields  TEXT,
    source          TEXT    NOT NULL DEFAULT 'app',
    ip_address      TEXT,
    user_agent      TEXT,
    created_at      TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),

    CHECK (entity_type IN ('setting', 'site', 'service', 'php_version', 'certificate', 'database', 'plugin')),
    CHECK (operation IN ('INSERT', 'UPDATE', 'DELETE')),
    CHECK (old_values IS NULL OR json_valid(old_values)),
    CHECK (new_values IS NULL OR json_valid(new_values)),
    CHECK (changed_fields IS NULL OR json_valid(changed_fields)),
    CHECK (source IN ('app', 'api', 'cli', 'trigger', 'migration', 'import'))
);

-- Record this migration
INSERT INTO schema_migrations (version, name)
VALUES ('001', 'initial');
