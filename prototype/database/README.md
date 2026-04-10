# NKS WDC Database Schema

SQLite database for local dev server management. Stores all configuration, service state, and audit history.

## Requirements

- SQLite 3.38+ (for `json_valid()`, `json_object()`, `json_group_array()`)
- Python 3.8+ (for running tests)

## Quick Start

```bash
# Run the full test suite
python schema_test.py

# Verbose output
python schema_test.py --verbose

# Keep the test database for inspection
python schema_test.py --keep-db
```

## File Structure

```
database/
  migrations/
    001_initial.sql          -- DDL: all tables with constraints
    001_initial_down.sql     -- Rollback: drops all tables
  seed.sql                   -- Default settings, services, PHP versions
  triggers.sql               -- Timestamp auto-update, audit trail, business rules
  views.sql                  -- Dashboard views, cert expiry, summaries
  indexes.sql                -- Performance indexes
  test_queries.sql           -- Example application queries
  export_import.sql          -- JSON backup/restore helpers
  schema_test.py             -- Automated test suite
```

## Application Load Order

```
1. migrations/001_initial.sql   -- Create tables
2. triggers.sql                 -- Attach triggers
3. views.sql                    -- Create views
4. indexes.sql                  -- Create indexes
5. seed.sql                     -- Insert defaults (first run only)
```

## ER Diagram

```
+-------------------+       +-------------------+       +-------------------+
| settings          |       | php_versions      |       | certificates      |
+-------------------+       +-------------------+       +-------------------+
| id           PK   |       | id           PK   |<--+   | id           PK   |<--+
| category          |       | version           |   |   | domain            |   |
| key               |       | install_path      |   |   | cert_path         |   |
| value             |       | binary_path       |   |   | key_path          |   |
| value_type        |       | fpm_binary_path   |   |   | ca_bundle_path    |   |
| description       |       | extensions   JSON |   |   | issuer            |   |
| is_readonly       |       | ini_overrides JSON|   |   | valid_from        |   |
| created_at        |       | is_default        |   |   | valid_until       |   |
| updated_at        |       | is_active         |   |   | is_wildcard       |   |
+-------------------+       | installed_at      |   |   | auto_renew        |   |
                            | created_at        |   |   | status            |   |
                            | updated_at        |   |   | created_at        |   |
                            +-------------------+   |   | updated_at        |   |
                                                    |   +-------------------+   |
                                                    |                           |
+-------------------+       +-------------------+   |                           |
| services          |       | sites             |   |                           |
+-------------------+       +-------------------+   |                           |
| id           PK   |<--+  | id           PK   |   |                           |
| name              |   |  | domain       UQ   |   |                           |
| display_name      |   |  | aliases      JSON |   |                           |
| service_type      |   |  | document_root     |   |                           |
| binary_path       |   |  | webserver         |   |                           |
| config_path       |   |  | php_version_id FK-+---+                           |
| pid_file          |   |  | ssl_enabled       |                               |
| log_path          |   |  | certificate_id FK-+-------------------------------+
| port              |   |  | status            |
| ssl_port          |   |  | auto_created      |
| status            |   |  | nginx_config      |
| auto_start        |   |  | apache_config     |
| config_overrides  |   |  | env_variables JSON|
| last_started_at   |   |  | notes             |
| last_stopped_at   |   |  | created_at        |
| last_error        |   |  | updated_at        |
| created_at        |   |  +-------------------+
| updated_at        |   |          |
+-------------------+   |          | (optional)
        |               |          |
        |  +------------+----------+
        |  |            |
+-------------------+   |  +-------------------+
| databases         |   |  | config_history    |
+-------------------+   |  +-------------------+
| id           PK   |   |  | id           PK   |
| name              |   |  | entity_type       |
| service_id   FK---+---+  | entity_id         |
| charset           |      | entity_name       |
| collation         |      | operation         |
| size_bytes        |      | old_values   JSON |
| table_count       |      | new_values   JSON |
| site_id      FK--------+ | changed_fields    |
| username          |    | | source            |
| password_hash     |    | | ip_address        |
| notes             |    | | user_agent        |
| created_at        |    | | created_at        |
| updated_at        |    | +-------------------+
+-------------------+    |
                         |
+-------------------+    |
| plugins           |    |
+-------------------+    |
| id           PK   |    |
| slug         UQ   |    |
| name              |    |  +-------------------+
| version           |    |  | schema_migrations |
| description       |    |  +-------------------+
| author            |    |  | id           PK   |
| homepage_url      |    +--| version      UQ   |
| install_path      |       | name              |
| entry_point       |       | applied_at        |
| config       JSON |       | checksum          |
| permissions  JSON |       | execution_ms      |
| is_enabled        |       +-------------------+
| is_builtin        |
| installed_at      |
| updated_at        |
+-------------------+

FK = Foreign Key     PK = Primary Key     UQ = Unique
JSON = Column with json_valid() CHECK constraint
```

## Relationships

| Parent            | Child       | FK Column        | ON DELETE   |
|-------------------|-------------|------------------|-------------|
| php_versions      | sites       | php_version_id   | SET NULL    |
| certificates      | sites       | certificate_id   | SET NULL    |
| services          | databases   | service_id       | CASCADE     |
| sites             | databases   | site_id          | SET NULL    |

## Key Constraints

- **settings**: Unique `(category, key)`. JSON values validated with `json_valid()`.
- **php_versions**: Only one `is_default=1` (enforced by trigger). Cannot delete default version.
- **services**: Port range 1-65535. Port and ssl_port must differ. Status enum enforced.
- **sites**: SSL requires a certificate. Domain unique. Webserver restricted to apache/nginx.
- **certificates**: `valid_from < valid_until` enforced. Issuer enum.
- **databases**: Name restricted to `[a-zA-Z0-9_]`. Unique `(name, service_id)`.
- **plugins**: Slug restricted to `[a-z0-9-]`.
- **config_history**: Entity type and operation enums. JSON columns validated.

## Triggers

| Trigger | Table | Purpose |
|---------|-------|---------|
| `trg_*_updated_at` | all mutable | Auto-set `updated_at` on UPDATE |
| `trg_settings_audit_*` | settings | INSERT/UPDATE/DELETE audit |
| `trg_sites_audit_*` | sites | INSERT/UPDATE/DELETE audit |
| `trg_services_audit_*` | services | INSERT/UPDATE/DELETE audit |
| `trg_php_versions_audit_*` | php_versions | INSERT/UPDATE/DELETE audit |
| `trg_php_versions_prevent_delete_default` | php_versions | Block deleting default version |
| `trg_php_versions_single_default_*` | php_versions | Clear other defaults when setting new one |

## Views

| View | Purpose |
|------|---------|
| `v_active_sites` | Active sites with resolved PHP version and cert info |
| `v_service_dashboard` | Services with status colors, listen addresses, site counts |
| `v_expiring_certs` | Certificates expiring within 30 days with urgency level |
| `v_recent_changes` | Last 50 config changes with human-readable summary |
| `v_site_summary` | Site counts grouped by status and webserver |

## PRAGMA Settings

```sql
PRAGMA foreign_keys = ON;      -- Enforce FK constraints
PRAGMA journal_mode = WAL;     -- Write-Ahead Logging for concurrency
PRAGMA busy_timeout = 5000;    -- Wait 5s on lock contention
```
