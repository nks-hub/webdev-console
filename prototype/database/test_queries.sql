-- ============================================================================
-- DevForge Database — Application Query Examples
-- These represent the actual queries the application will execute
-- ============================================================================

-- ============================================================================
-- 1. Site Management Queries
-- ============================================================================

-- Get all active sites with PHP version and SSL info (main dashboard)
SELECT
    s.id,
    s.domain,
    s.document_root,
    s.webserver,
    s.ssl_enabled,
    s.status,
    p.version AS php_version,
    c.issuer AS cert_issuer,
    c.valid_until AS cert_expires,
    CASE
        WHEN c.valid_until < strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
        THEN 'expired'
        WHEN c.valid_until IS NOT NULL
        THEN 'valid'
        ELSE NULL
    END AS cert_status
FROM sites s
LEFT JOIN php_versions p ON s.php_version_id = p.id
LEFT JOIN certificates c ON s.certificate_id = c.id
WHERE s.status = 'active'
ORDER BY s.domain;

-- Get a single site with full details
SELECT
    s.*,
    p.version AS php_version,
    p.binary_path AS php_binary,
    p.extensions AS php_extensions,
    c.cert_path,
    c.key_path,
    c.valid_until AS cert_expires
FROM sites s
LEFT JOIN php_versions p ON s.php_version_id = p.id
LEFT JOIN certificates c ON s.certificate_id = c.id
WHERE s.domain = :domain;

-- Find sites using a specific PHP version
SELECT s.domain, s.status, s.webserver
FROM sites s
WHERE s.php_version_id = :php_version_id
ORDER BY s.domain;

-- Count sites by status and webserver
SELECT * FROM v_site_summary;

-- ============================================================================
-- 2. Service Dashboard Queries
-- ============================================================================

-- Get full service status for dashboard
SELECT * FROM v_service_dashboard;

-- Get only running services
SELECT name, display_name, port, ssl_port, last_started_at
FROM services
WHERE status = 'running'
ORDER BY name;

-- Check if a port is in use by any service
SELECT name, display_name, service_type
FROM services
WHERE (port = :port OR ssl_port = :port)
  AND status != 'stopped';

-- Check port availability (including stopped services that claim the port)
SELECT
    :port AS requested_port,
    CASE
        WHEN EXISTS (
            SELECT 1 FROM services
            WHERE port = :port OR ssl_port = :port
        )
        THEN 'in_use'
        ELSE 'available'
    END AS availability,
    (SELECT GROUP_CONCAT(name, ', ')
     FROM services
     WHERE port = :port OR ssl_port = :port
    ) AS used_by;

-- ============================================================================
-- 3. PHP Version Queries
-- ============================================================================

-- Get all PHP versions with usage count
SELECT
    p.id,
    p.version,
    p.is_default,
    p.is_active,
    p.extensions,
    p.ini_overrides,
    COUNT(s.id) AS site_count
FROM php_versions p
LEFT JOIN sites s ON s.php_version_id = p.id AND s.status = 'active'
GROUP BY p.id
ORDER BY p.version DESC;

-- Get the default PHP version
SELECT * FROM php_versions WHERE is_default = 1;

-- ============================================================================
-- 4. Settings Queries
-- ============================================================================

-- Get all settings in a category
SELECT key, value, value_type, description, is_readonly
FROM settings
WHERE category = :category
ORDER BY key;

-- Get a single setting value
SELECT value, value_type
FROM settings
WHERE category = :category AND key = :key;

-- Get all settings as a flat config object (for app initialization)
SELECT
    category || '.' || key AS config_key,
    value,
    value_type
FROM settings
ORDER BY category, key;

-- ============================================================================
-- 5. Certificate Queries
-- ============================================================================

-- Get expiring certificates
SELECT * FROM v_expiring_certs;

-- Get certificate for a domain
SELECT *
FROM certificates
WHERE domain = :domain
  AND status = 'valid'
  AND valid_until > strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
ORDER BY valid_until DESC
LIMIT 1;

-- ============================================================================
-- 6. Database Management Queries
-- ============================================================================

-- List databases for a service
SELECT
    d.id,
    d.name,
    d.charset,
    d.collation,
    d.size_bytes,
    d.table_count,
    s.domain AS linked_site,
    d.created_at
FROM databases d
LEFT JOIN sites s ON d.site_id = s.id
WHERE d.service_id = :service_id
ORDER BY d.name;

-- ============================================================================
-- 7. Plugin Queries
-- ============================================================================

-- Get all enabled plugins
SELECT slug, name, version, description, author, config
FROM plugins
WHERE is_enabled = 1
ORDER BY name;

-- ============================================================================
-- 8. Audit / History Queries
-- ============================================================================

-- Get config history for a specific site
SELECT
    ch.operation,
    ch.old_values,
    ch.new_values,
    ch.changed_fields,
    ch.source,
    ch.created_at
FROM config_history ch
WHERE ch.entity_type = 'site'
  AND ch.entity_id = :site_id
ORDER BY ch.created_at DESC;

-- Get recent changes across all entities
SELECT * FROM v_recent_changes;

-- Get changes by a time range
SELECT *
FROM config_history
WHERE created_at BETWEEN :start_date AND :end_date
ORDER BY created_at DESC;

-- ============================================================================
-- 9. Startup / Initialization Queries
-- ============================================================================

-- Check which services should auto-start
SELECT id, name, display_name, service_type, port, ssl_port
FROM services
WHERE auto_start = 1
ORDER BY
    CASE service_type
        WHEN 'dns'       THEN 1
        WHEN 'database'  THEN 2
        WHEN 'php-fpm'   THEN 3
        WHEN 'webserver' THEN 4
        ELSE 5
    END;

-- Get current schema version
SELECT version, name, applied_at
FROM schema_migrations
ORDER BY version DESC
LIMIT 1;

-- ============================================================================
-- 10. Search Queries
-- ============================================================================

-- Search sites by domain pattern
SELECT id, domain, document_root, status
FROM sites
WHERE domain LIKE '%' || :search || '%'
ORDER BY domain;

-- Search settings by key or value
SELECT category, key, value, description
FROM settings
WHERE key LIKE '%' || :search || '%'
   OR value LIKE '%' || :search || '%'
   OR description LIKE '%' || :search || '%'
ORDER BY category, key;
