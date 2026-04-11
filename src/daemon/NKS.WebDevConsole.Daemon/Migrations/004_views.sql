-- ============================================================================
-- NKS WebDev Console Database — Views
-- ============================================================================

-- ============================================================================
-- v_active_sites — sites with resolved PHP version and certificate info
-- ============================================================================

CREATE VIEW IF NOT EXISTS v_active_sites AS
SELECT
    s.id,
    s.domain,
    s.aliases,
    s.document_root,
    s.webserver,
    s.status,
    s.ssl_enabled,
    s.auto_created,
    s.env_variables,
    s.notes,
    s.created_at,
    s.updated_at,
    -- PHP version details
    p.id          AS php_version_id,
    p.version     AS php_version,
    p.binary_path AS php_binary_path,
    p.is_active   AS php_is_active,
    -- Certificate details
    c.id          AS certificate_id,
    c.issuer      AS cert_issuer,
    c.valid_from  AS cert_valid_from,
    c.valid_until AS cert_valid_until,
    c.status      AS cert_status,
    c.is_wildcard AS cert_is_wildcard,
    -- Computed fields
    CASE
        WHEN c.valid_until IS NOT NULL
            AND c.valid_until < strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
        THEN 1
        ELSE 0
    END AS cert_expired,
    CASE
        WHEN c.valid_until IS NOT NULL
            AND c.valid_until < strftime('%Y-%m-%dT%H:%M:%fZ', 'now', '+30 days')
            AND c.valid_until >= strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
        THEN 1
        ELSE 0
    END AS cert_expiring_soon
FROM sites s
LEFT JOIN php_versions p ON s.php_version_id = p.id
LEFT JOIN certificates c ON s.certificate_id = c.id
WHERE s.status = 'active';

-- ============================================================================
-- v_service_dashboard — service overview with parsed ports
-- ============================================================================

CREATE VIEW IF NOT EXISTS v_service_dashboard AS
SELECT
    sv.id,
    sv.name,
    sv.display_name,
    sv.service_type,
    sv.status,
    sv.auto_start,
    sv.port,
    sv.ssl_port,
    sv.binary_path,
    sv.config_path,
    sv.last_started_at,
    sv.last_stopped_at,
    sv.last_error,
    sv.updated_at,
    -- Formatted listen addresses
    CASE
        WHEN sv.port IS NOT NULL THEN '0.0.0.0:' || sv.port
        ELSE NULL
    END AS listen_address,
    CASE
        WHEN sv.ssl_port IS NOT NULL THEN '0.0.0.0:' || sv.ssl_port
        ELSE NULL
    END AS ssl_listen_address,
    -- Status indicator
    CASE sv.status
        WHEN 'running'  THEN 'green'
        WHEN 'stopped'  THEN 'gray'
        WHEN 'starting' THEN 'yellow'
        WHEN 'stopping' THEN 'yellow'
        WHEN 'error'    THEN 'red'
        ELSE 'gray'
    END AS status_color,
    -- Service group label
    CASE sv.service_type
        WHEN 'webserver' THEN 'Web Servers'
        WHEN 'database'  THEN 'Databases'
        WHEN 'php-fpm'   THEN 'PHP'
        WHEN 'dns'       THEN 'DNS'
        WHEN 'cache'     THEN 'Cache'
        WHEN 'mail'      THEN 'Mail'
        ELSE 'Other'
    END AS service_group,
    -- Count of sites using this webserver
    CASE
        WHEN sv.service_type = 'webserver'
        THEN (SELECT COUNT(*) FROM sites WHERE webserver = sv.name AND status = 'active')
        ELSE NULL
    END AS active_site_count
FROM services sv
ORDER BY
    CASE sv.service_type
        WHEN 'webserver' THEN 1
        WHEN 'database'  THEN 2
        WHEN 'php-fpm'   THEN 3
        WHEN 'dns'       THEN 4
        ELSE 5
    END,
    sv.name;

-- ============================================================================
-- v_expiring_certs — certificates expiring within 30 days
-- ============================================================================

CREATE VIEW IF NOT EXISTS v_expiring_certs AS
SELECT
    c.id,
    c.domain,
    c.issuer,
    c.valid_from,
    c.valid_until,
    c.status,
    c.auto_renew,
    c.is_wildcard,
    c.cert_path,
    -- Days until expiry (can be negative if already expired)
    CAST(
        julianday(c.valid_until) - julianday('now')
    AS INTEGER) AS days_until_expiry,
    -- Urgency level
    CASE
        WHEN c.valid_until < strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
        THEN 'expired'
        WHEN julianday(c.valid_until) - julianday('now') <= 7
        THEN 'critical'
        WHEN julianday(c.valid_until) - julianday('now') <= 14
        THEN 'warning'
        ELSE 'notice'
    END AS urgency,
    -- Sites using this certificate
    (SELECT GROUP_CONCAT(s.domain, ', ')
     FROM sites s
     WHERE s.certificate_id = c.id
       AND s.status = 'active'
    ) AS affected_sites
FROM certificates c
WHERE c.valid_until < strftime('%Y-%m-%dT%H:%M:%fZ', 'now', '+30 days')
  AND c.status != 'revoked'
ORDER BY c.valid_until ASC;

-- ============================================================================
-- v_recent_changes — last 50 configuration changes
-- ============================================================================

CREATE VIEW IF NOT EXISTS v_recent_changes AS
SELECT
    ch.id,
    ch.entity_type,
    ch.entity_id,
    ch.entity_name,
    ch.operation,
    ch.old_values,
    ch.new_values,
    ch.changed_fields,
    ch.source,
    ch.created_at,
    -- Human-readable summary
    ch.operation || ' ' || ch.entity_type ||
        CASE
            WHEN ch.entity_name IS NOT NULL THEN ' "' || ch.entity_name || '"'
            ELSE ' #' || ch.entity_id
        END AS summary,
    -- Time ago (approximate)
    CASE
        WHEN julianday('now') - julianday(ch.created_at) < 1.0/24
        THEN CAST((julianday('now') - julianday(ch.created_at)) * 24 * 60 AS INTEGER) || ' min ago'
        WHEN julianday('now') - julianday(ch.created_at) < 1
        THEN CAST((julianday('now') - julianday(ch.created_at)) * 24 AS INTEGER) || ' hours ago'
        WHEN julianday('now') - julianday(ch.created_at) < 30
        THEN CAST(julianday('now') - julianday(ch.created_at) AS INTEGER) || ' days ago'
        ELSE 'over a month ago'
    END AS time_ago
FROM config_history ch
ORDER BY ch.created_at DESC
LIMIT 50;

-- ============================================================================
-- v_site_summary — count per status, per webserver type
-- ============================================================================

CREATE VIEW IF NOT EXISTS v_site_summary AS
SELECT
    webserver,
    status,
    COUNT(*)                                                AS site_count,
    SUM(ssl_enabled)                                        AS ssl_count,
    SUM(CASE WHEN php_version_id IS NOT NULL THEN 1 ELSE 0 END) AS with_php_count
FROM sites
GROUP BY webserver, status

UNION ALL

SELECT
    'ALL'   AS webserver,
    status,
    COUNT(*) AS site_count,
    SUM(ssl_enabled) AS ssl_count,
    SUM(CASE WHEN php_version_id IS NOT NULL THEN 1 ELSE 0 END) AS with_php_count
FROM sites
GROUP BY status

UNION ALL

SELECT
    'ALL'    AS webserver,
    'ALL'    AS status,
    COUNT(*) AS site_count,
    SUM(ssl_enabled) AS ssl_count,
    SUM(CASE WHEN php_version_id IS NOT NULL THEN 1 ELSE 0 END) AS with_php_count
FROM sites;
