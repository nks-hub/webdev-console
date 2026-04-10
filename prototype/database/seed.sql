-- ============================================================================
-- NKS WebDev Console Database — Seed Data
-- Default settings, services, and PHP versions
-- ============================================================================

-- ============================================================================
-- Default Settings
-- ============================================================================

-- Network
INSERT INTO settings (category, key, value, value_type, description)
VALUES
    ('network', 'http_port',  '80',    'integer', 'Default HTTP port for web servers'),
    ('network', 'https_port', '443',   'integer', 'Default HTTPS port for web servers'),
    ('network', 'mysql_port', '3306',  'integer', 'Default MySQL port'),
    ('network', 'mariadb_port', '3307', 'integer', 'Default MariaDB port'),
    ('network', 'dns_port',   '53',    'integer', 'DNS resolver port'),
    ('network', 'loopback_address', '127.0.0.1', 'string', 'Loopback address for local sites');

-- DNS / TLD
INSERT INTO settings (category, key, value, value_type, description)
VALUES
    ('dns', 'tld',            'test',  'string',  'Top-level domain for local sites (e.g. .test)'),
    ('dns', 'auto_resolve',   'true',  'boolean', 'Automatically resolve *.tld to loopback'),
    ('dns', 'upstream_dns',   '1.1.1.1', 'string', 'Upstream DNS server for non-local queries');

-- UI / Appearance
INSERT INTO settings (category, key, value, value_type, description)
VALUES
    ('ui', 'theme',           'system', 'string',  'UI theme: system, light, dark'),
    ('ui', 'language',        'en',     'string',  'UI language code'),
    ('ui', 'accent_color',    '#3B82F6', 'string', 'Accent color hex'),
    ('ui', 'sidebar_collapsed', 'false', 'boolean', 'Sidebar collapsed state'),
    ('ui', 'show_notifications', 'true', 'boolean', 'Show system notifications');

-- General
INSERT INTO settings (category, key, value, value_type, description)
VALUES
    ('general', 'app_name',         'NKS WebDev Console',     'string',  'Application display name'),
    ('general', 'data_dir',         '',             'path',    'Data directory path (empty = default)'),
    ('general', 'log_level',        'info',         'string',  'Log level: debug, info, warn, error'),
    ('general', 'log_retention_days', '30',         'integer', 'Days to retain log files'),
    ('general', 'start_on_boot',    'false',        'boolean', 'Start NKS WebDev Console on system boot'),
    ('general', 'check_updates',    'true',         'boolean', 'Automatically check for updates');

-- Updates
INSERT INTO settings (category, key, value, value_type, description)
VALUES
    ('updates', 'channel',          'stable',       'string',  'Update channel: stable, beta, nightly'),
    ('updates', 'auto_update',      'false',        'boolean', 'Automatically install updates'),
    ('updates', 'last_check',       '',             'string',  'Last update check timestamp'),
    ('updates', 'php_mirror',       'https://windows.php.net/downloads/releases/', 'string', 'PHP download mirror URL');

-- SSL
INSERT INTO settings (category, key, value, value_type, description)
VALUES
    ('ssl', 'auto_generate',        'true',         'boolean', 'Auto-generate self-signed certs for new sites'),
    ('ssl', 'default_issuer',       'mkcert',       'string',  'Default certificate issuer'),
    ('ssl', 'ca_cert_path',         '',             'path',    'Custom CA certificate path'),
    ('ssl', 'ca_key_path',          '',             'path',    'Custom CA key path'),
    ('ssl', 'cert_validity_days',   '825',          'integer', 'Certificate validity in days');

-- Backup
INSERT INTO settings (category, key, value, value_type, description)
VALUES
    ('backup', 'auto_backup',       'true',         'boolean', 'Enable automatic config backups'),
    ('backup', 'backup_dir',        '',             'path',    'Backup directory (empty = default)'),
    ('backup', 'max_backups',       '10',           'integer', 'Maximum number of backup files to keep'),
    ('backup', 'include_databases', 'false',        'boolean', 'Include database dumps in backups');

-- ============================================================================
-- Default Services
-- ============================================================================

INSERT INTO services (name, display_name, service_type, port, ssl_port, status, auto_start)
VALUES
    ('apache',   'Apache HTTP Server', 'webserver', 80,   443,  'stopped', 1),
    ('nginx',    'Nginx',              'webserver', 8080, 8443, 'stopped', 0),
    ('mysql',    'MySQL',              'database',  3306, NULL,  'stopped', 1),
    ('mariadb',  'MariaDB',            'database',  3307, NULL,  'stopped', 0),
    ('dnsmasq',  'dnsmasq',            'dns',       53,   NULL,  'stopped', 1);

-- ============================================================================
-- Default PHP Versions
-- ============================================================================

INSERT INTO php_versions (version, install_path, binary_path, fpm_binary_path, extensions, ini_overrides, is_default, is_active)
VALUES
    (
        '8.2',
        '{DATA_DIR}/php/8.2',
        '{DATA_DIR}/php/8.2/php.exe',
        '{DATA_DIR}/php/8.2/php-cgi.exe',
        '["curl","mbstring","openssl","pdo_mysql","pdo_sqlite","zip","gd","intl","xml","soap","bcmath","redis","xdebug"]',
        '{"memory_limit":"512M","max_execution_time":"300","upload_max_filesize":"128M","post_max_size":"128M","display_errors":"On","error_reporting":"E_ALL","xdebug.mode":"debug","xdebug.start_with_request":"trigger"}',
        0,
        1
    ),
    (
        '8.3',
        '{DATA_DIR}/php/8.3',
        '{DATA_DIR}/php/8.3/php.exe',
        '{DATA_DIR}/php/8.3/php-cgi.exe',
        '["curl","mbstring","openssl","pdo_mysql","pdo_sqlite","zip","gd","intl","xml","soap","bcmath","redis","xdebug"]',
        '{"memory_limit":"512M","max_execution_time":"300","upload_max_filesize":"128M","post_max_size":"128M","display_errors":"On","error_reporting":"E_ALL","xdebug.mode":"debug","xdebug.start_with_request":"trigger"}',
        1,
        1
    ),
    (
        '8.4',
        '{DATA_DIR}/php/8.4',
        '{DATA_DIR}/php/8.4/php.exe',
        '{DATA_DIR}/php/8.4/php-cgi.exe',
        '["curl","mbstring","openssl","pdo_mysql","pdo_sqlite","zip","gd","intl","xml","soap","bcmath","redis","xdebug"]',
        '{"memory_limit":"512M","max_execution_time":"300","upload_max_filesize":"128M","post_max_size":"128M","display_errors":"On","error_reporting":"E_ALL","xdebug.mode":"debug","xdebug.start_with_request":"trigger"}',
        0,
        1
    );
