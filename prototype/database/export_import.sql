-- ============================================================================
-- NKS WebDev Console Database — Export / Import
-- SQL helpers for JSON-based config backup and restore
-- ============================================================================

-- ============================================================================
-- EXPORT: Generate JSON backup of all configuration
-- ============================================================================

-- Export all settings as JSON array
SELECT json_group_array(
    json_object(
        'category', category,
        'key', key,
        'value', value,
        'value_type', value_type,
        'description', description,
        'is_readonly', is_readonly
    )
) AS settings_json
FROM settings;

-- Export all services as JSON array
SELECT json_group_array(
    json_object(
        'name', name,
        'display_name', display_name,
        'service_type', service_type,
        'binary_path', binary_path,
        'config_path', config_path,
        'pid_file', pid_file,
        'log_path', log_path,
        'port', port,
        'ssl_port', ssl_port,
        'auto_start', auto_start,
        'config_overrides', json(config_overrides)
    )
) AS services_json
FROM services;

-- Export all PHP versions as JSON array
SELECT json_group_array(
    json_object(
        'version', version,
        'install_path', install_path,
        'binary_path', binary_path,
        'fpm_binary_path', fpm_binary_path,
        'extensions', json(extensions),
        'ini_overrides', json(ini_overrides),
        'is_default', is_default,
        'is_active', is_active
    )
) AS php_versions_json
FROM php_versions;

-- Export all sites as JSON array
SELECT json_group_array(
    json_object(
        'domain', domain,
        'aliases', json(aliases),
        'document_root', document_root,
        'webserver', webserver,
        'php_version', (SELECT version FROM php_versions WHERE id = sites.php_version_id),
        'ssl_enabled', ssl_enabled,
        'status', status,
        'auto_created', auto_created,
        'env_variables', json(env_variables),
        'notes', notes
    )
) AS sites_json
FROM sites;

-- Export certificates as JSON array
SELECT json_group_array(
    json_object(
        'domain', domain,
        'cert_path', cert_path,
        'key_path', key_path,
        'ca_bundle_path', ca_bundle_path,
        'issuer', issuer,
        'serial_number', serial_number,
        'fingerprint', fingerprint,
        'valid_from', valid_from,
        'valid_until', valid_until,
        'is_wildcard', is_wildcard,
        'auto_renew', auto_renew,
        'status', status
    )
) AS certificates_json
FROM certificates;

-- Export databases as JSON array
SELECT json_group_array(
    json_object(
        'name', d.name,
        'service_name', (SELECT name FROM services WHERE id = d.service_id),
        'charset', d.charset,
        'collation', d.collation,
        'site_domain', (SELECT domain FROM sites WHERE id = d.site_id),
        'username', d.username,
        'notes', d.notes
    )
) AS databases_json
FROM databases d;

-- Export plugins as JSON array
SELECT json_group_array(
    json_object(
        'slug', slug,
        'name', name,
        'version', version,
        'description', description,
        'author', author,
        'homepage_url', homepage_url,
        'install_path', install_path,
        'entry_point', entry_point,
        'config', json(config),
        'permissions', json(permissions),
        'is_enabled', is_enabled,
        'is_builtin', is_builtin
    )
) AS plugins_json
FROM plugins;

-- Full backup as single JSON document
SELECT json_object(
    'backup_version', '1.0',
    'created_at', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'),
    'schema_version', (SELECT version FROM schema_migrations ORDER BY version DESC LIMIT 1),
    'settings',     (SELECT json_group_array(json_object(
                        'category', category, 'key', key, 'value', value,
                        'value_type', value_type, 'description', description,
                        'is_readonly', is_readonly
                    )) FROM settings),
    'services',     (SELECT json_group_array(json_object(
                        'name', name, 'display_name', display_name,
                        'service_type', service_type, 'binary_path', binary_path,
                        'config_path', config_path, 'port', port, 'ssl_port', ssl_port,
                        'auto_start', auto_start, 'config_overrides', json(config_overrides)
                    )) FROM services),
    'php_versions', (SELECT json_group_array(json_object(
                        'version', version, 'install_path', install_path,
                        'binary_path', binary_path, 'fpm_binary_path', fpm_binary_path,
                        'extensions', json(extensions), 'ini_overrides', json(ini_overrides),
                        'is_default', is_default, 'is_active', is_active
                    )) FROM php_versions),
    'sites',        (SELECT json_group_array(json_object(
                        'domain', domain, 'aliases', json(aliases),
                        'document_root', document_root, 'webserver', webserver,
                        'php_version', (SELECT version FROM php_versions WHERE id = sites.php_version_id),
                        'ssl_enabled', ssl_enabled, 'status', status,
                        'env_variables', json(env_variables), 'notes', notes
                    )) FROM sites),
    'certificates', (SELECT json_group_array(json_object(
                        'domain', domain, 'cert_path', cert_path, 'key_path', key_path,
                        'issuer', issuer, 'valid_from', valid_from, 'valid_until', valid_until,
                        'is_wildcard', is_wildcard, 'auto_renew', auto_renew, 'status', status
                    )) FROM certificates),
    'plugins',      (SELECT json_group_array(json_object(
                        'slug', slug, 'name', name, 'version', version,
                        'is_enabled', is_enabled, 'config', json(config)
                    )) FROM plugins)
) AS full_backup;

-- ============================================================================
-- IMPORT: Restore from JSON backup
-- These are template statements. The application reads the JSON file
-- and executes parameterized inserts from parsed data.
-- ============================================================================

-- Import settings from JSON
-- Application parses the JSON array, then for each item:
--   INSERT OR REPLACE INTO settings (category, key, value, value_type, description, is_readonly)
--   VALUES (:category, :key, :value, :value_type, :description, :is_readonly);

-- Import services from JSON
-- Application parses the JSON array, then for each item:
--   INSERT OR REPLACE INTO services (name, display_name, service_type, binary_path,
--       config_path, port, ssl_port, auto_start, config_overrides)
--   VALUES (:name, :display_name, :service_type, :binary_path,
--       :config_path, :port, :ssl_port, :auto_start, :config_overrides);

-- Import PHP versions from JSON
-- Application parses the JSON array, then for each item:
--   INSERT OR REPLACE INTO php_versions (version, install_path, binary_path,
--       fpm_binary_path, extensions, ini_overrides, is_default, is_active)
--   VALUES (:version, :install_path, :binary_path,
--       :fpm_binary_path, :extensions, :ini_overrides, :is_default, :is_active);

-- ============================================================================
-- SQLite-native JSON import (for use within SQLite directly)
-- Requires the JSON to be loaded into a parameter or CTE
-- ============================================================================

-- Example: Import settings from a JSON string stored in a temp table
-- This pattern works when the app loads the backup JSON into a temp table

-- CREATE TEMP TABLE _import_buffer (data TEXT);
-- INSERT INTO _import_buffer VALUES (readfile('backup.json'));
--
-- INSERT OR REPLACE INTO settings (category, key, value, value_type, description, is_readonly)
-- SELECT
--     json_extract(item.value, '$.category'),
--     json_extract(item.value, '$.key'),
--     json_extract(item.value, '$.value'),
--     json_extract(item.value, '$.value_type'),
--     json_extract(item.value, '$.description'),
--     json_extract(item.value, '$.is_readonly')
-- FROM _import_buffer,
--      json_each(json_extract(_import_buffer.data, '$.settings')) AS item;
--
-- INSERT OR REPLACE INTO services (name, display_name, service_type, binary_path,
--     config_path, port, ssl_port, auto_start, config_overrides)
-- SELECT
--     json_extract(item.value, '$.name'),
--     json_extract(item.value, '$.display_name'),
--     json_extract(item.value, '$.service_type'),
--     json_extract(item.value, '$.binary_path'),
--     json_extract(item.value, '$.config_path'),
--     json_extract(item.value, '$.port'),
--     json_extract(item.value, '$.ssl_port'),
--     json_extract(item.value, '$.auto_start'),
--     json_extract(item.value, '$.config_overrides')
-- FROM _import_buffer,
--      json_each(json_extract(_import_buffer.data, '$.services')) AS item;
--
-- INSERT OR REPLACE INTO php_versions (version, install_path, binary_path,
--     fpm_binary_path, extensions, ini_overrides, is_default, is_active)
-- SELECT
--     json_extract(item.value, '$.version'),
--     json_extract(item.value, '$.install_path'),
--     json_extract(item.value, '$.binary_path'),
--     json_extract(item.value, '$.fpm_binary_path'),
--     json_extract(item.value, '$.extensions'),
--     json_extract(item.value, '$.ini_overrides'),
--     json_extract(item.value, '$.is_default'),
--     json_extract(item.value, '$.is_active')
-- FROM _import_buffer,
--      json_each(json_extract(_import_buffer.data, '$.php_versions')) AS item;
--
-- DROP TABLE _import_buffer;
