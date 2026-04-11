-- ============================================================================
-- NKS WebDev Console Database — Indexes
-- Performance indexes for common query patterns
-- ============================================================================

-- ============================================================================
-- settings
-- ============================================================================

-- Fast lookup by category + key (most common query pattern)
CREATE UNIQUE INDEX IF NOT EXISTS idx_settings_category_key
    ON settings (category, key);

-- Filter by category
CREATE INDEX IF NOT EXISTS idx_settings_category
    ON settings (category);

-- ============================================================================
-- php_versions
-- ============================================================================

-- Find the default PHP version
CREATE INDEX IF NOT EXISTS idx_php_versions_default
    ON php_versions (is_default) WHERE is_default = 1;

-- Filter active versions
CREATE INDEX IF NOT EXISTS idx_php_versions_active
    ON php_versions (is_active) WHERE is_active = 1;

-- ============================================================================
-- services
-- ============================================================================

-- Lookup by service type
CREATE INDEX IF NOT EXISTS idx_services_type
    ON services (service_type);

-- Find running services
CREATE INDEX IF NOT EXISTS idx_services_status
    ON services (status);

-- Find auto-start services
CREATE INDEX IF NOT EXISTS idx_services_auto_start
    ON services (auto_start) WHERE auto_start = 1;

-- Port availability check (unique ports across all services)
CREATE INDEX IF NOT EXISTS idx_services_port
    ON services (port) WHERE port IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_services_ssl_port
    ON services (ssl_port) WHERE ssl_port IS NOT NULL;

-- ============================================================================
-- sites
-- ============================================================================

-- Domain lookup (already UNIQUE, but explicit for clarity)
-- The UNIQUE constraint on domain creates an implicit index

-- Filter by status
CREATE INDEX IF NOT EXISTS idx_sites_status
    ON sites (status);

-- Filter by webserver type
CREATE INDEX IF NOT EXISTS idx_sites_webserver
    ON sites (webserver);

-- Find sites using a specific PHP version
CREATE INDEX IF NOT EXISTS idx_sites_php_version_id
    ON sites (php_version_id) WHERE php_version_id IS NOT NULL;

-- Find sites with SSL
CREATE INDEX IF NOT EXISTS idx_sites_ssl
    ON sites (ssl_enabled) WHERE ssl_enabled = 1;

-- Find sites by certificate
CREATE INDEX IF NOT EXISTS idx_sites_certificate_id
    ON sites (certificate_id) WHERE certificate_id IS NOT NULL;

-- Combined: active sites with webserver (dashboard query)
CREATE INDEX IF NOT EXISTS idx_sites_active_webserver
    ON sites (webserver, status) WHERE status = 'active';

-- ============================================================================
-- certificates
-- ============================================================================

-- Lookup by domain
CREATE INDEX IF NOT EXISTS idx_certificates_domain
    ON certificates (domain);

-- Find expiring certificates
CREATE INDEX IF NOT EXISTS idx_certificates_valid_until
    ON certificates (valid_until);

-- Filter by status
CREATE INDEX IF NOT EXISTS idx_certificates_status
    ON certificates (status);

-- Combined: valid certificates by expiry (for expiring cert check)
CREATE INDEX IF NOT EXISTS idx_certificates_valid_expiry
    ON certificates (valid_until, status) WHERE status = 'valid';

-- ============================================================================
-- databases
-- ============================================================================

-- Lookup by service
CREATE INDEX IF NOT EXISTS idx_databases_service_id
    ON databases (service_id);

-- Lookup by associated site
CREATE INDEX IF NOT EXISTS idx_databases_site_id
    ON databases (site_id) WHERE site_id IS NOT NULL;

-- Unique name per service (already has UNIQUE constraint)
-- CREATE UNIQUE INDEX IF NOT EXISTS idx_databases_name_service ON databases (name, service_id);

-- ============================================================================
-- plugins
-- ============================================================================

-- Filter enabled plugins
CREATE INDEX IF NOT EXISTS idx_plugins_enabled
    ON plugins (is_enabled) WHERE is_enabled = 1;

-- Filter builtin vs user plugins
CREATE INDEX IF NOT EXISTS idx_plugins_builtin
    ON plugins (is_builtin);

-- ============================================================================
-- config_history
-- ============================================================================

-- Query by entity (most common: "show history for site X")
CREATE INDEX IF NOT EXISTS idx_config_history_entity
    ON config_history (entity_type, entity_id);

-- Query by entity name (human-readable lookups)
CREATE INDEX IF NOT EXISTS idx_config_history_entity_name
    ON config_history (entity_name) WHERE entity_name IS NOT NULL;

-- Time-based queries (recent changes)
CREATE INDEX IF NOT EXISTS idx_config_history_created_at
    ON config_history (created_at DESC);

-- Filter by operation type
CREATE INDEX IF NOT EXISTS idx_config_history_operation
    ON config_history (operation);

-- Combined: entity changes over time
CREATE INDEX IF NOT EXISTS idx_config_history_entity_time
    ON config_history (entity_type, entity_id, created_at DESC);

-- Filter by source
CREATE INDEX IF NOT EXISTS idx_config_history_source
    ON config_history (source);

-- ============================================================================
-- schema_migrations
-- ============================================================================

-- Version lookup
CREATE INDEX IF NOT EXISTS idx_schema_migrations_version
    ON schema_migrations (version);
