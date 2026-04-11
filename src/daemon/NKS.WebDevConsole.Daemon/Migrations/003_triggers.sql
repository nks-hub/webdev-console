-- ============================================================================
-- NKS WebDev Console Database — Triggers
-- Auto-update timestamps, audit trail, business rule enforcement
-- ============================================================================

-- ============================================================================
-- Auto-update updated_at timestamps
-- ============================================================================

CREATE TRIGGER IF NOT EXISTS trg_settings_updated_at
    AFTER UPDATE ON settings
    FOR EACH ROW
    WHEN OLD.updated_at = NEW.updated_at
BEGIN
    UPDATE settings
    SET updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    WHERE id = NEW.id;
END;

CREATE TRIGGER IF NOT EXISTS trg_php_versions_updated_at
    AFTER UPDATE ON php_versions
    FOR EACH ROW
    WHEN OLD.updated_at = NEW.updated_at
BEGIN
    UPDATE php_versions
    SET updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    WHERE id = NEW.id;
END;

CREATE TRIGGER IF NOT EXISTS trg_services_updated_at
    AFTER UPDATE ON services
    FOR EACH ROW
    WHEN OLD.updated_at = NEW.updated_at
BEGIN
    UPDATE services
    SET updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    WHERE id = NEW.id;
END;

CREATE TRIGGER IF NOT EXISTS trg_sites_updated_at
    AFTER UPDATE ON sites
    FOR EACH ROW
    WHEN OLD.updated_at = NEW.updated_at
BEGIN
    UPDATE sites
    SET updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    WHERE id = NEW.id;
END;

CREATE TRIGGER IF NOT EXISTS trg_certificates_updated_at
    AFTER UPDATE ON certificates
    FOR EACH ROW
    WHEN OLD.updated_at = NEW.updated_at
BEGIN
    UPDATE certificates
    SET updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    WHERE id = NEW.id;
END;

CREATE TRIGGER IF NOT EXISTS trg_databases_updated_at
    AFTER UPDATE ON databases
    FOR EACH ROW
    WHEN OLD.updated_at = NEW.updated_at
BEGIN
    UPDATE databases
    SET updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    WHERE id = NEW.id;
END;

CREATE TRIGGER IF NOT EXISTS trg_plugins_updated_at
    AFTER UPDATE ON plugins
    FOR EACH ROW
    WHEN OLD.updated_at = NEW.updated_at
BEGIN
    UPDATE plugins
    SET updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    WHERE id = NEW.id;
END;

-- ============================================================================
-- Audit Trail Triggers — Settings
-- ============================================================================

CREATE TRIGGER IF NOT EXISTS trg_settings_audit_insert
    AFTER INSERT ON settings
    FOR EACH ROW
BEGIN
    INSERT INTO config_history (entity_type, entity_id, entity_name, operation, new_values, source)
    VALUES (
        'setting',
        NEW.id,
        NEW.category || '.' || NEW.key,
        'INSERT',
        json_object(
            'category', NEW.category,
            'key', NEW.key,
            'value', NEW.value,
            'value_type', NEW.value_type
        ),
        'trigger'
    );
END;

CREATE TRIGGER IF NOT EXISTS trg_settings_audit_update
    AFTER UPDATE ON settings
    FOR EACH ROW
    WHEN OLD.value != NEW.value
        OR OLD.category != NEW.category
        OR OLD.key != NEW.key
BEGIN
    INSERT INTO config_history (entity_type, entity_id, entity_name, operation, old_values, new_values, changed_fields, source)
    VALUES (
        'setting',
        NEW.id,
        NEW.category || '.' || NEW.key,
        'UPDATE',
        json_object(
            'category', OLD.category,
            'key', OLD.key,
            'value', OLD.value,
            'value_type', OLD.value_type
        ),
        json_object(
            'category', NEW.category,
            'key', NEW.key,
            'value', NEW.value,
            'value_type', NEW.value_type
        ),
        json_array(
            CASE WHEN OLD.value != NEW.value THEN 'value' END,
            CASE WHEN OLD.category != NEW.category THEN 'category' END,
            CASE WHEN OLD.key != NEW.key THEN 'key' END
        ),
        'trigger'
    );
END;

CREATE TRIGGER IF NOT EXISTS trg_settings_audit_delete
    AFTER DELETE ON settings
    FOR EACH ROW
BEGIN
    INSERT INTO config_history (entity_type, entity_id, entity_name, operation, old_values, source)
    VALUES (
        'setting',
        OLD.id,
        OLD.category || '.' || OLD.key,
        'DELETE',
        json_object(
            'category', OLD.category,
            'key', OLD.key,
            'value', OLD.value,
            'value_type', OLD.value_type
        ),
        'trigger'
    );
END;

-- ============================================================================
-- Audit Trail Triggers — Sites
-- ============================================================================

CREATE TRIGGER IF NOT EXISTS trg_sites_audit_insert
    AFTER INSERT ON sites
    FOR EACH ROW
BEGIN
    INSERT INTO config_history (entity_type, entity_id, entity_name, operation, new_values, source)
    VALUES (
        'site',
        NEW.id,
        NEW.domain,
        'INSERT',
        json_object(
            'domain', NEW.domain,
            'document_root', NEW.document_root,
            'webserver', NEW.webserver,
            'php_version_id', NEW.php_version_id,
            'ssl_enabled', NEW.ssl_enabled,
            'status', NEW.status
        ),
        'trigger'
    );
END;

CREATE TRIGGER IF NOT EXISTS trg_sites_audit_update
    AFTER UPDATE ON sites
    FOR EACH ROW
    WHEN OLD.domain != NEW.domain
        OR OLD.document_root != NEW.document_root
        OR OLD.webserver != NEW.webserver
        OR COALESCE(OLD.php_version_id, -1) != COALESCE(NEW.php_version_id, -1)
        OR OLD.ssl_enabled != NEW.ssl_enabled
        OR OLD.status != NEW.status
BEGIN
    INSERT INTO config_history (entity_type, entity_id, entity_name, operation, old_values, new_values, source)
    VALUES (
        'site',
        NEW.id,
        NEW.domain,
        'UPDATE',
        json_object(
            'domain', OLD.domain,
            'document_root', OLD.document_root,
            'webserver', OLD.webserver,
            'php_version_id', OLD.php_version_id,
            'ssl_enabled', OLD.ssl_enabled,
            'status', OLD.status
        ),
        json_object(
            'domain', NEW.domain,
            'document_root', NEW.document_root,
            'webserver', NEW.webserver,
            'php_version_id', NEW.php_version_id,
            'ssl_enabled', NEW.ssl_enabled,
            'status', NEW.status
        ),
        'trigger'
    );
END;

CREATE TRIGGER IF NOT EXISTS trg_sites_audit_delete
    AFTER DELETE ON sites
    FOR EACH ROW
BEGIN
    INSERT INTO config_history (entity_type, entity_id, entity_name, operation, old_values, source)
    VALUES (
        'site',
        OLD.id,
        OLD.domain,
        'DELETE',
        json_object(
            'domain', OLD.domain,
            'document_root', OLD.document_root,
            'webserver', OLD.webserver,
            'php_version_id', OLD.php_version_id,
            'ssl_enabled', OLD.ssl_enabled,
            'status', OLD.status
        ),
        'trigger'
    );
END;

-- ============================================================================
-- Audit Trail Triggers — Services
-- ============================================================================

CREATE TRIGGER IF NOT EXISTS trg_services_audit_insert
    AFTER INSERT ON services
    FOR EACH ROW
BEGIN
    INSERT INTO config_history (entity_type, entity_id, entity_name, operation, new_values, source)
    VALUES (
        'service',
        NEW.id,
        NEW.name,
        'INSERT',
        json_object(
            'name', NEW.name,
            'service_type', NEW.service_type,
            'port', NEW.port,
            'ssl_port', NEW.ssl_port,
            'status', NEW.status,
            'auto_start', NEW.auto_start
        ),
        'trigger'
    );
END;

CREATE TRIGGER IF NOT EXISTS trg_services_audit_update
    AFTER UPDATE ON services
    FOR EACH ROW
    WHEN OLD.status != NEW.status
        OR COALESCE(OLD.port, -1) != COALESCE(NEW.port, -1)
        OR COALESCE(OLD.ssl_port, -1) != COALESCE(NEW.ssl_port, -1)
        OR OLD.auto_start != NEW.auto_start
        OR OLD.config_overrides != NEW.config_overrides
BEGIN
    INSERT INTO config_history (entity_type, entity_id, entity_name, operation, old_values, new_values, source)
    VALUES (
        'service',
        NEW.id,
        NEW.name,
        'UPDATE',
        json_object(
            'status', OLD.status,
            'port', OLD.port,
            'ssl_port', OLD.ssl_port,
            'auto_start', OLD.auto_start
        ),
        json_object(
            'status', NEW.status,
            'port', NEW.port,
            'ssl_port', NEW.ssl_port,
            'auto_start', NEW.auto_start
        ),
        'trigger'
    );
END;

CREATE TRIGGER IF NOT EXISTS trg_services_audit_delete
    AFTER DELETE ON services
    FOR EACH ROW
BEGIN
    INSERT INTO config_history (entity_type, entity_id, entity_name, operation, old_values, source)
    VALUES (
        'service',
        OLD.id,
        OLD.name,
        'DELETE',
        json_object(
            'name', OLD.name,
            'service_type', OLD.service_type,
            'port', OLD.port,
            'status', OLD.status
        ),
        'trigger'
    );
END;

-- ============================================================================
-- Audit Trail Triggers — PHP Versions
-- ============================================================================

CREATE TRIGGER IF NOT EXISTS trg_php_versions_audit_insert
    AFTER INSERT ON php_versions
    FOR EACH ROW
BEGIN
    INSERT INTO config_history (entity_type, entity_id, entity_name, operation, new_values, source)
    VALUES (
        'php_version',
        NEW.id,
        'PHP ' || NEW.version,
        'INSERT',
        json_object(
            'version', NEW.version,
            'install_path', NEW.install_path,
            'is_default', NEW.is_default,
            'is_active', NEW.is_active
        ),
        'trigger'
    );
END;

CREATE TRIGGER IF NOT EXISTS trg_php_versions_audit_update
    AFTER UPDATE ON php_versions
    FOR EACH ROW
    WHEN OLD.is_default != NEW.is_default
        OR OLD.is_active != NEW.is_active
        OR OLD.extensions != NEW.extensions
        OR OLD.ini_overrides != NEW.ini_overrides
BEGIN
    INSERT INTO config_history (entity_type, entity_id, entity_name, operation, old_values, new_values, source)
    VALUES (
        'php_version',
        NEW.id,
        'PHP ' || NEW.version,
        'UPDATE',
        json_object(
            'version', OLD.version,
            'is_default', OLD.is_default,
            'is_active', OLD.is_active,
            'extensions', OLD.extensions,
            'ini_overrides', OLD.ini_overrides
        ),
        json_object(
            'version', NEW.version,
            'is_default', NEW.is_default,
            'is_active', NEW.is_active,
            'extensions', NEW.extensions,
            'ini_overrides', NEW.ini_overrides
        ),
        'trigger'
    );
END;

CREATE TRIGGER IF NOT EXISTS trg_php_versions_audit_delete
    AFTER DELETE ON php_versions
    FOR EACH ROW
BEGIN
    INSERT INTO config_history (entity_type, entity_id, entity_name, operation, old_values, source)
    VALUES (
        'php_version',
        OLD.id,
        'PHP ' || OLD.version,
        'DELETE',
        json_object(
            'version', OLD.version,
            'install_path', OLD.install_path,
            'is_default', OLD.is_default,
            'is_active', OLD.is_active
        ),
        'trigger'
    );
END;

-- ============================================================================
-- Business Rule: Prevent deleting the default PHP version
-- ============================================================================

CREATE TRIGGER IF NOT EXISTS trg_php_versions_prevent_delete_default
    BEFORE DELETE ON php_versions
    FOR EACH ROW
    WHEN OLD.is_default = 1
BEGIN
    SELECT RAISE(ABORT, 'Cannot delete the default PHP version. Set another version as default first.');
END;

-- ============================================================================
-- Business Rule: Ensure only one default PHP version
-- When a PHP version is set as default, clear previous default
-- ============================================================================

CREATE TRIGGER IF NOT EXISTS trg_php_versions_single_default_insert
    AFTER INSERT ON php_versions
    FOR EACH ROW
    WHEN NEW.is_default = 1
BEGIN
    UPDATE php_versions
    SET is_default = 0
    WHERE id != NEW.id AND is_default = 1;
END;

CREATE TRIGGER IF NOT EXISTS trg_php_versions_single_default_update
    AFTER UPDATE OF is_default ON php_versions
    FOR EACH ROW
    WHEN NEW.is_default = 1 AND OLD.is_default = 0
BEGIN
    UPDATE php_versions
    SET is_default = 0
    WHERE id != NEW.id AND is_default = 1;
END;
