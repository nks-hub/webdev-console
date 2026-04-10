#!/usr/bin/env python3
"""
NKS WebDev Console Database Schema Test Suite

Creates a fresh SQLite database, runs all migrations, seeds data,
validates constraints, triggers, views, and common queries.

Usage:
    python schema_test.py
    python schema_test.py --verbose
    python schema_test.py --keep-db    # Don't delete test DB after run
"""

import sqlite3
import json
import os
import sys
import traceback
from pathlib import Path
from datetime import datetime, timedelta

# Configuration
SCRIPT_DIR = Path(__file__).parent
DB_PATH = SCRIPT_DIR / "test_wdc.db"
VERBOSE = "--verbose" in sys.argv or "-v" in sys.argv
KEEP_DB = "--keep-db" in sys.argv

# Test counters
passed = 0
failed = 0
errors = []


def log(msg):
    print(f"  {msg}")


def log_verbose(msg):
    if VERBOSE:
        print(f"    {msg}")


def test(name, fn):
    """Run a single test and track pass/fail."""
    global passed, failed
    try:
        fn()
        passed += 1
        print(f"  PASS  {name}")
    except AssertionError as e:
        failed += 1
        errors.append((name, str(e)))
        print(f"  FAIL  {name}: {e}")
    except Exception as e:
        failed += 1
        errors.append((name, f"{type(e).__name__}: {e}"))
        print(f"  ERROR {name}: {type(e).__name__}: {e}")
        if VERBOSE:
            traceback.print_exc()


def read_sql_file(filename):
    """Read a SQL file relative to the script directory."""
    path = SCRIPT_DIR / filename
    with open(path, "r", encoding="utf-8") as f:
        return f.read()


def execute_sql_file(conn, filename):
    """Execute a SQL file, handling multiple statements."""
    sql = read_sql_file(filename)
    conn.executescript(sql)


def create_fresh_database():
    """Create a fresh database and return a connection."""
    if DB_PATH.exists():
        os.remove(DB_PATH)

    conn = sqlite3.connect(str(DB_PATH))
    conn.row_factory = sqlite3.Row
    conn.execute("PRAGMA foreign_keys = ON")
    conn.execute("PRAGMA journal_mode = WAL")
    return conn


# ==========================================================================
# Phase 1: Schema Creation
# ==========================================================================

def test_migration(conn):
    print("\n[Phase 1] Schema Migration")

    def run_migration():
        execute_sql_file(conn, "migrations/001_initial.sql")
        # Verify migration was recorded
        row = conn.execute(
            "SELECT version, name FROM schema_migrations ORDER BY version DESC LIMIT 1"
        ).fetchone()
        assert row is not None, "No migration recorded"
        assert row["version"] == "001", f"Expected version '001', got '{row['version']}'"
        assert row["name"] == "initial", f"Expected name 'initial', got '{row['name']}'"

    test("Apply initial migration", run_migration)

    def verify_tables():
        expected_tables = [
            "schema_migrations", "settings", "php_versions", "services",
            "sites", "certificates", "databases", "plugins", "config_history"
        ]
        rows = conn.execute(
            "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'"
        ).fetchall()
        actual = {r["name"] for r in rows}
        for t in expected_tables:
            assert t in actual, f"Table '{t}' not found. Existing: {actual}"

    test("All expected tables exist", verify_tables)


# ==========================================================================
# Phase 2: Triggers, Views, Indexes
# ==========================================================================

def test_triggers_views_indexes(conn):
    print("\n[Phase 2] Triggers, Views, Indexes")

    def load_triggers():
        execute_sql_file(conn, "triggers.sql")
        rows = conn.execute(
            "SELECT name FROM sqlite_master WHERE type='trigger'"
        ).fetchall()
        names = [r["name"] for r in rows]
        assert len(names) >= 10, f"Expected at least 10 triggers, got {len(names)}"
        log_verbose(f"Loaded {len(names)} triggers: {', '.join(sorted(names))}")

    test("Load triggers", load_triggers)

    def load_views():
        execute_sql_file(conn, "views.sql")
        rows = conn.execute(
            "SELECT name FROM sqlite_master WHERE type='view'"
        ).fetchall()
        names = [r["name"] for r in rows]
        expected = ["v_active_sites", "v_service_dashboard", "v_expiring_certs",
                     "v_recent_changes", "v_site_summary"]
        for v in expected:
            assert v in names, f"View '{v}' not found"

    test("Load views", load_views)

    def load_indexes():
        execute_sql_file(conn, "indexes.sql")
        rows = conn.execute(
            "SELECT name FROM sqlite_master WHERE type='index' AND name LIKE 'idx_%'"
        ).fetchall()
        assert len(rows) >= 15, f"Expected at least 15 indexes, got {len(rows)}"
        log_verbose(f"Loaded {len(rows)} custom indexes")

    test("Load indexes", load_indexes)


# ==========================================================================
# Phase 3: Seed Data
# ==========================================================================

def test_seed(conn):
    print("\n[Phase 3] Seed Data")

    def run_seed():
        execute_sql_file(conn, "seed.sql")

    test("Insert seed data", run_seed)

    def verify_settings():
        count = conn.execute("SELECT COUNT(*) AS c FROM settings").fetchone()["c"]
        assert count >= 25, f"Expected at least 25 settings, got {count}"

        # Verify specific settings
        tld = conn.execute(
            "SELECT value FROM settings WHERE category='dns' AND key='tld'"
        ).fetchone()
        assert tld is not None, "DNS TLD setting not found"
        assert tld["value"] == "test", f"Expected TLD 'test', got '{tld['value']}'"

    test("Settings seeded correctly", verify_settings)

    def verify_services():
        count = conn.execute("SELECT COUNT(*) AS c FROM services").fetchone()["c"]
        assert count == 5, f"Expected 5 services, got {count}"

        apache = conn.execute(
            "SELECT port, ssl_port FROM services WHERE name='apache'"
        ).fetchone()
        assert apache["port"] == 80
        assert apache["ssl_port"] == 443

    test("Services seeded correctly", verify_services)

    def verify_php_versions():
        count = conn.execute("SELECT COUNT(*) AS c FROM php_versions").fetchone()["c"]
        assert count == 3, f"Expected 3 PHP versions, got {count}"

        default = conn.execute(
            "SELECT version FROM php_versions WHERE is_default=1"
        ).fetchone()
        assert default is not None, "No default PHP version found"
        assert default["version"] == "8.3", f"Expected default '8.3', got '{default['version']}'"

    test("PHP versions seeded correctly", verify_php_versions)


# ==========================================================================
# Phase 4: Constraint Validation
# ==========================================================================

def test_constraints(conn):
    print("\n[Phase 4] Constraint Validation")

    def reject_invalid_setting_value_type():
        try:
            conn.execute(
                "INSERT INTO settings (category, key, value, value_type) "
                "VALUES ('test', 'bad_type', 'x', 'invalid_type')"
            )
            conn.commit()
            assert False, "Should have rejected invalid value_type"
        except sqlite3.IntegrityError:
            conn.rollback()

    test("Reject invalid setting value_type", reject_invalid_setting_value_type)

    def reject_invalid_json_setting():
        try:
            conn.execute(
                "INSERT INTO settings (category, key, value, value_type) "
                "VALUES ('test', 'bad_json', 'not json', 'json')"
            )
            conn.commit()
            assert False, "Should have rejected invalid JSON value"
        except sqlite3.IntegrityError:
            conn.rollback()

    test("Reject invalid JSON in settings", reject_invalid_json_setting)

    def reject_invalid_service_type():
        try:
            conn.execute(
                "INSERT INTO services (name, display_name, service_type) "
                "VALUES ('bad', 'Bad Service', 'invalid_type')"
            )
            conn.commit()
            assert False, "Should have rejected invalid service_type"
        except sqlite3.IntegrityError:
            conn.rollback()

    test("Reject invalid service_type", reject_invalid_service_type)

    def reject_invalid_port():
        try:
            conn.execute(
                "INSERT INTO services (name, display_name, service_type, port) "
                "VALUES ('badport', 'Bad Port', 'webserver', 99999)"
            )
            conn.commit()
            assert False, "Should have rejected port > 65535"
        except sqlite3.IntegrityError:
            conn.rollback()

    test("Reject port > 65535", reject_invalid_port)

    def reject_same_port_and_ssl_port():
        try:
            conn.execute(
                "INSERT INTO services (name, display_name, service_type, port, ssl_port) "
                "VALUES ('sameport', 'Same Port', 'webserver', 9090, 9090)"
            )
            conn.commit()
            assert False, "Should have rejected same port and ssl_port"
        except sqlite3.IntegrityError:
            conn.rollback()

    test("Reject same port and ssl_port", reject_same_port_and_ssl_port)

    def reject_invalid_site_status():
        try:
            conn.execute(
                "INSERT INTO sites (domain, document_root, status) "
                "VALUES ('bad.test', '/var/www', 'invalid_status')"
            )
            conn.commit()
            assert False, "Should have rejected invalid site status"
        except sqlite3.IntegrityError:
            conn.rollback()

    test("Reject invalid site status", reject_invalid_site_status)

    def reject_ssl_without_cert():
        try:
            conn.execute(
                "INSERT INTO sites (domain, document_root, ssl_enabled, certificate_id) "
                "VALUES ('ssl-no-cert.test', '/var/www', 1, NULL)"
            )
            conn.commit()
            assert False, "Should have rejected SSL without certificate_id"
        except sqlite3.IntegrityError:
            conn.rollback()

    test("Reject SSL enabled without certificate", reject_ssl_without_cert)

    def reject_invalid_php_version_format():
        try:
            conn.execute(
                "INSERT INTO php_versions (version, install_path, binary_path) "
                "VALUES ('invalid', '/path', '/path/php')"
            )
            conn.commit()
            assert False, "Should have rejected invalid PHP version format"
        except sqlite3.IntegrityError:
            conn.rollback()

    test("Reject invalid PHP version format", reject_invalid_php_version_format)

    def reject_invalid_json_extensions():
        try:
            conn.execute(
                "INSERT INTO php_versions (version, install_path, binary_path, extensions) "
                "VALUES ('9.0', '/path', '/path/php', 'not json')"
            )
            conn.commit()
            assert False, "Should have rejected invalid JSON extensions"
        except sqlite3.IntegrityError:
            conn.rollback()

    test("Reject invalid JSON in php extensions", reject_invalid_json_extensions)

    def reject_invalid_cert_dates():
        try:
            conn.execute(
                "INSERT INTO certificates (domain, cert_path, key_path, valid_from, valid_until) "
                "VALUES ('bad.test', '/cert', '/key', '2025-12-31', '2025-01-01')"
            )
            conn.commit()
            assert False, "Should have rejected valid_from >= valid_until"
        except sqlite3.IntegrityError:
            conn.rollback()

    test("Reject certificate valid_from >= valid_until", reject_invalid_cert_dates)

    def reject_invalid_database_name():
        svc = conn.execute("SELECT id FROM services WHERE name='mysql'").fetchone()
        try:
            conn.execute(
                "INSERT INTO databases (name, service_id) VALUES ('bad name!', ?)",
                (svc["id"],)
            )
            conn.commit()
            assert False, "Should have rejected database name with special chars"
        except sqlite3.IntegrityError:
            conn.rollback()

    test("Reject database name with special characters", reject_invalid_database_name)

    def reject_invalid_plugin_slug():
        try:
            conn.execute(
                "INSERT INTO plugins (slug, name, version, install_path) "
                "VALUES ('BAD_SLUG!', 'Bad', '1.0', '/path')"
            )
            conn.commit()
            assert False, "Should have rejected invalid plugin slug"
        except sqlite3.IntegrityError:
            conn.rollback()

    test("Reject invalid plugin slug", reject_invalid_plugin_slug)

    def reject_invalid_history_entity_type():
        try:
            conn.execute(
                "INSERT INTO config_history (entity_type, entity_id, operation) "
                "VALUES ('invalid_type', 1, 'INSERT')"
            )
            conn.commit()
            assert False, "Should have rejected invalid entity_type"
        except sqlite3.IntegrityError:
            conn.rollback()

    test("Reject invalid config_history entity_type", reject_invalid_history_entity_type)

    def reject_invalid_history_operation():
        try:
            conn.execute(
                "INSERT INTO config_history (entity_type, entity_id, operation) "
                "VALUES ('site', 1, 'INVALID')"
            )
            conn.commit()
            assert False, "Should have rejected invalid operation"
        except sqlite3.IntegrityError:
            conn.rollback()

    test("Reject invalid config_history operation", reject_invalid_history_operation)

    def accept_valid_json_setting():
        conn.execute(
            "INSERT INTO settings (category, key, value, value_type) "
            "VALUES ('test', 'valid_json', '{\"a\":1}', 'json')"
        )
        conn.commit()
        row = conn.execute(
            "SELECT value FROM settings WHERE category='test' AND key='valid_json'"
        ).fetchone()
        assert row is not None
        assert json.loads(row["value"]) == {"a": 1}
        # Clean up
        conn.execute("DELETE FROM settings WHERE category='test' AND key='valid_json'")
        conn.commit()

    test("Accept valid JSON setting", accept_valid_json_setting)

    def foreign_key_cascade_delete():
        # Insert a service, then a database referencing it, then delete service
        conn.execute(
            "INSERT INTO services (name, display_name, service_type, port) "
            "VALUES ('temp_svc', 'Temp Service', 'database', 55555)"
        )
        svc_id = conn.execute("SELECT id FROM services WHERE name='temp_svc'").fetchone()["id"]
        conn.execute(
            "INSERT INTO databases (name, service_id) VALUES ('temp_db', ?)", (svc_id,)
        )
        conn.commit()

        # Verify database exists
        count = conn.execute(
            "SELECT COUNT(*) AS c FROM databases WHERE service_id=?", (svc_id,)
        ).fetchone()["c"]
        assert count == 1

        # Delete service — should cascade delete database
        conn.execute("DELETE FROM services WHERE id=?", (svc_id,))
        conn.commit()

        count = conn.execute(
            "SELECT COUNT(*) AS c FROM databases WHERE service_id=?", (svc_id,)
        ).fetchone()["c"]
        assert count == 0, "Database should have been cascade-deleted"

    test("Foreign key ON DELETE CASCADE (service->databases)", foreign_key_cascade_delete)


# ==========================================================================
# Phase 5: Trigger Validation
# ==========================================================================

def test_triggers(conn):
    print("\n[Phase 5] Trigger Validation")

    def test_updated_at_trigger():
        # Get the current updated_at for a setting
        row = conn.execute(
            "SELECT id, updated_at FROM settings WHERE category='dns' AND key='tld'"
        ).fetchone()
        old_ts = row["updated_at"]

        # Update the value
        conn.execute(
            "UPDATE settings SET value='loc' WHERE id=?", (row["id"],)
        )
        conn.commit()

        new_ts = conn.execute(
            "SELECT updated_at FROM settings WHERE id=?", (row["id"],)
        ).fetchone()["updated_at"]
        assert new_ts != old_ts, f"updated_at should have changed: old={old_ts}, new={new_ts}"

        # Restore original value
        conn.execute("UPDATE settings SET value='test' WHERE id=?", (row["id"],))
        conn.commit()

    test("Auto-update updated_at on settings change", test_updated_at_trigger)

    def test_audit_trail_on_settings_update():
        # The seed data inserts should have created audit records
        count = conn.execute(
            "SELECT COUNT(*) AS c FROM config_history WHERE entity_type='setting'"
        ).fetchone()["c"]
        assert count > 0, "No audit records for settings"

        # Update a setting and check audit
        conn.execute(
            "UPDATE settings SET value='debug' WHERE category='general' AND key='log_level'"
        )
        conn.commit()

        audit = conn.execute(
            "SELECT * FROM config_history "
            "WHERE entity_type='setting' AND operation='UPDATE' "
            "ORDER BY created_at DESC LIMIT 1"
        ).fetchone()
        assert audit is not None, "No UPDATE audit record found"
        old = json.loads(audit["old_values"])
        new = json.loads(audit["new_values"])
        assert old["value"] == "info", f"Expected old value 'info', got '{old['value']}'"
        assert new["value"] == "debug", f"Expected new value 'debug', got '{new['value']}'"

        # Restore
        conn.execute(
            "UPDATE settings SET value='info' WHERE category='general' AND key='log_level'"
        )
        conn.commit()

    test("Audit trail on settings update", test_audit_trail_on_settings_update)

    def test_audit_trail_on_service_insert():
        audit = conn.execute(
            "SELECT COUNT(*) AS c FROM config_history "
            "WHERE entity_type='service' AND operation='INSERT'"
        ).fetchone()["c"]
        assert audit >= 5, f"Expected at least 5 service INSERT audits, got {audit}"

    test("Audit trail on service insert (from seed)", test_audit_trail_on_service_insert)

    def test_prevent_delete_default_php():
        try:
            default = conn.execute(
                "SELECT id FROM php_versions WHERE is_default=1"
            ).fetchone()
            conn.execute("DELETE FROM php_versions WHERE id=?", (default["id"],))
            conn.commit()
            assert False, "Should have prevented deleting default PHP version"
        except sqlite3.IntegrityError:
            conn.rollback()

    test("Prevent deleting default PHP version", test_prevent_delete_default_php)

    def test_single_default_php():
        # Set PHP 8.2 as default (should auto-clear 8.3's default)
        php82 = conn.execute(
            "SELECT id FROM php_versions WHERE version='8.2'"
        ).fetchone()
        conn.execute(
            "UPDATE php_versions SET is_default=1 WHERE id=?", (php82["id"],)
        )
        conn.commit()

        defaults = conn.execute(
            "SELECT COUNT(*) AS c FROM php_versions WHERE is_default=1"
        ).fetchone()["c"]
        assert defaults == 1, f"Expected exactly 1 default PHP version, got {defaults}"

        current_default = conn.execute(
            "SELECT version FROM php_versions WHERE is_default=1"
        ).fetchone()
        assert current_default["version"] == "8.2", \
            f"Expected default '8.2', got '{current_default['version']}'"

        # Restore 8.3 as default
        php83 = conn.execute(
            "SELECT id FROM php_versions WHERE version='8.3'"
        ).fetchone()
        conn.execute(
            "UPDATE php_versions SET is_default=1 WHERE id=?", (php83["id"],)
        )
        conn.commit()

    test("Ensure only one default PHP version", test_single_default_php)

    def test_site_audit_trail():
        # Create a site
        php = conn.execute("SELECT id FROM php_versions WHERE is_default=1").fetchone()
        conn.execute(
            "INSERT INTO sites (domain, document_root, webserver, php_version_id, status) "
            "VALUES ('audit-test.test', '/var/www/audit-test', 'apache', ?, 'active')",
            (php["id"],)
        )
        conn.commit()

        # Check INSERT audit
        audit = conn.execute(
            "SELECT * FROM config_history "
            "WHERE entity_type='site' AND entity_name='audit-test.test' AND operation='INSERT'"
        ).fetchone()
        assert audit is not None, "No INSERT audit for site"

        # Update site
        site = conn.execute("SELECT id FROM sites WHERE domain='audit-test.test'").fetchone()
        conn.execute(
            "UPDATE sites SET status='inactive' WHERE id=?", (site["id"],)
        )
        conn.commit()

        # Check UPDATE audit
        audit = conn.execute(
            "SELECT * FROM config_history "
            "WHERE entity_type='site' AND entity_name='audit-test.test' AND operation='UPDATE'"
        ).fetchone()
        assert audit is not None, "No UPDATE audit for site"

        # Delete site
        conn.execute("DELETE FROM sites WHERE id=?", (site["id"],))
        conn.commit()

        # Check DELETE audit
        audit = conn.execute(
            "SELECT * FROM config_history "
            "WHERE entity_type='site' AND entity_name='audit-test.test' AND operation='DELETE'"
        ).fetchone()
        assert audit is not None, "No DELETE audit for site"

    test("Full audit trail for site CRUD", test_site_audit_trail)


# ==========================================================================
# Phase 6: View Validation
# ==========================================================================

def test_views(conn):
    print("\n[Phase 6] View Validation")

    # First, insert test data that views need
    php = conn.execute("SELECT id FROM php_versions WHERE is_default=1").fetchone()

    # Insert a certificate
    conn.execute(
        "INSERT INTO certificates (domain, cert_path, key_path, issuer, valid_from, valid_until, status) "
        "VALUES ('myapp.test', '/certs/myapp.pem', '/certs/myapp-key.pem', 'mkcert', "
        "'2025-01-01T00:00:00.000Z', '2027-01-01T00:00:00.000Z', 'valid')"
    )
    cert_id = conn.execute(
        "SELECT id FROM certificates WHERE domain='myapp.test'"
    ).fetchone()["id"]

    # Insert test sites
    conn.execute(
        "INSERT INTO sites (domain, document_root, webserver, php_version_id, ssl_enabled, certificate_id, status) "
        "VALUES ('myapp.test', '/var/www/myapp', 'apache', ?, 1, ?, 'active')",
        (php["id"], cert_id)
    )
    conn.execute(
        "INSERT INTO sites (domain, document_root, webserver, php_version_id, status) "
        "VALUES ('api.test', '/var/www/api', 'nginx', ?, 'active')",
        (php["id"],)
    )
    conn.execute(
        "INSERT INTO sites (domain, document_root, status) "
        "VALUES ('disabled.test', '/var/www/disabled', 'inactive')"
    )
    conn.commit()

    def test_v_active_sites():
        rows = conn.execute("SELECT * FROM v_active_sites").fetchall()
        assert len(rows) >= 2, f"Expected at least 2 active sites, got {len(rows)}"

        domains = [r["domain"] for r in rows]
        assert "myapp.test" in domains
        assert "api.test" in domains
        assert "disabled.test" not in domains, "Inactive site should not be in v_active_sites"

        myapp = [r for r in rows if r["domain"] == "myapp.test"][0]
        assert myapp["php_version"] is not None, "PHP version should be resolved"
        assert myapp["cert_issuer"] == "mkcert"
        assert myapp["cert_expired"] == 0

    test("v_active_sites returns correct data", test_v_active_sites)

    def test_v_service_dashboard():
        rows = conn.execute("SELECT * FROM v_service_dashboard").fetchall()
        assert len(rows) >= 5, f"Expected at least 5 services, got {len(rows)}"

        apache = [r for r in rows if r["name"] == "apache"][0]
        assert apache["listen_address"] == "0.0.0.0:80"
        assert apache["ssl_listen_address"] == "0.0.0.0:443"
        assert apache["status_color"] == "gray"  # stopped
        assert apache["service_group"] == "Web Servers"

    test("v_service_dashboard returns correct data", test_v_service_dashboard)

    def test_v_expiring_certs():
        # Insert an expiring certificate
        conn.execute(
            "INSERT INTO certificates (domain, cert_path, key_path, issuer, "
            "valid_from, valid_until, status) "
            "VALUES ('expiring.test', '/certs/exp.pem', '/certs/exp-key.pem', 'mkcert', "
            "'2025-01-01T00:00:00.000Z', "
            "strftime('%Y-%m-%dT%H:%M:%fZ', 'now', '+10 days'), 'valid')"
        )
        conn.commit()

        rows = conn.execute("SELECT * FROM v_expiring_certs").fetchall()
        domains = [r["domain"] for r in rows]
        assert "expiring.test" in domains, "Expiring cert should appear in view"

        exp = [r for r in rows if r["domain"] == "expiring.test"][0]
        assert exp["days_until_expiry"] <= 30
        assert exp["urgency"] in ("critical", "warning", "notice")

    test("v_expiring_certs detects expiring certificates", test_v_expiring_certs)

    def test_v_recent_changes():
        rows = conn.execute("SELECT * FROM v_recent_changes").fetchall()
        assert len(rows) > 0, "Should have recent changes from seed and test inserts"
        assert len(rows) <= 50, "Should be limited to 50 rows"

        # Check summary field
        first = rows[0]
        assert first["summary"] is not None
        assert first["time_ago"] is not None

    test("v_recent_changes returns formatted data", test_v_recent_changes)

    def test_v_site_summary():
        rows = conn.execute("SELECT * FROM v_site_summary").fetchall()
        assert len(rows) > 0, "Should have summary rows"

        # Find the ALL/ALL total row
        total = [r for r in rows if r["webserver"] == "ALL" and r["status"] == "ALL"]
        assert len(total) == 1, "Should have exactly one total row"
        assert total[0]["site_count"] >= 3, \
            f"Expected at least 3 total sites, got {total[0]['site_count']}"

    test("v_site_summary returns aggregated data", test_v_site_summary)


# ==========================================================================
# Phase 7: Application Query Tests
# ==========================================================================

def test_queries(conn):
    print("\n[Phase 7] Application Queries")

    def test_active_sites_query():
        rows = conn.execute("""
            SELECT s.id, s.domain, s.document_root, s.webserver, s.ssl_enabled,
                   p.version AS php_version, c.issuer AS cert_issuer
            FROM sites s
            LEFT JOIN php_versions p ON s.php_version_id = p.id
            LEFT JOIN certificates c ON s.certificate_id = c.id
            WHERE s.status = 'active'
            ORDER BY s.domain
        """).fetchall()
        assert len(rows) >= 2

    test("Get active sites with PHP and SSL info", test_active_sites_query)

    def test_service_status_query():
        rows = conn.execute("""
            SELECT name, display_name, status, port, ssl_port, auto_start
            FROM services
            ORDER BY name
        """).fetchall()
        assert len(rows) >= 5

    test("Get service status for dashboard", test_service_status_query)

    def test_sites_by_php_version():
        php = conn.execute("SELECT id FROM php_versions WHERE is_default=1").fetchone()
        rows = conn.execute(
            "SELECT domain FROM sites WHERE php_version_id=?",
            (php["id"],)
        ).fetchall()
        assert len(rows) >= 2

    test("Find sites using a specific PHP version", test_sites_by_php_version)

    def test_config_history_query():
        site = conn.execute("SELECT id FROM sites WHERE domain='myapp.test'").fetchone()
        rows = conn.execute(
            "SELECT * FROM config_history WHERE entity_type='site' AND entity_id=? "
            "ORDER BY created_at DESC",
            (site["id"],)
        ).fetchall()
        assert len(rows) >= 1

    test("Get config history for a site", test_config_history_query)

    def test_port_availability():
        row = conn.execute("""
            SELECT
                80 AS requested_port,
                CASE
                    WHEN EXISTS (SELECT 1 FROM services WHERE port = 80 OR ssl_port = 80)
                    THEN 'in_use'
                    ELSE 'available'
                END AS availability
        """).fetchone()
        assert row["availability"] == "in_use", "Port 80 should be in use by Apache"

        row = conn.execute("""
            SELECT
                12345 AS requested_port,
                CASE
                    WHEN EXISTS (SELECT 1 FROM services WHERE port = 12345 OR ssl_port = 12345)
                    THEN 'in_use'
                    ELSE 'available'
                END AS availability
        """).fetchone()
        assert row["availability"] == "available", "Port 12345 should be available"

    test("Check port availability", test_port_availability)

    def test_auto_start_services():
        rows = conn.execute("""
            SELECT name, service_type FROM services
            WHERE auto_start = 1
            ORDER BY CASE service_type
                WHEN 'dns' THEN 1
                WHEN 'database' THEN 2
                WHEN 'php-fpm' THEN 3
                WHEN 'webserver' THEN 4
                ELSE 5
            END
        """).fetchall()
        assert len(rows) >= 1
        # DNS should come first if it's auto-start
        types = [r["service_type"] for r in rows]
        if "dns" in types and "webserver" in types:
            assert types.index("dns") < types.index("webserver"), \
                "DNS should start before webserver"

    test("Get auto-start services in correct order", test_auto_start_services)

    def test_php_versions_with_usage():
        rows = conn.execute("""
            SELECT p.version, p.is_default, COUNT(s.id) AS site_count
            FROM php_versions p
            LEFT JOIN sites s ON s.php_version_id = p.id AND s.status = 'active'
            GROUP BY p.id
            ORDER BY p.version DESC
        """).fetchall()
        assert len(rows) == 3

    test("Get PHP versions with usage count", test_php_versions_with_usage)

    def test_settings_by_category():
        rows = conn.execute(
            "SELECT key, value, value_type FROM settings WHERE category='network' ORDER BY key"
        ).fetchall()
        assert len(rows) >= 4

    test("Get settings by category", test_settings_by_category)

    def test_search_sites():
        rows = conn.execute(
            "SELECT domain FROM sites WHERE domain LIKE '%' || 'app' || '%'"
        ).fetchall()
        assert len(rows) >= 1

    test("Search sites by domain pattern", test_search_sites)


# ==========================================================================
# Phase 8: Export Validation
# ==========================================================================

def test_export(conn):
    print("\n[Phase 8] Export Validation")

    def test_full_backup_json():
        row = conn.execute("""
            SELECT json_object(
                'backup_version', '1.0',
                'created_at', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'),
                'settings', (SELECT json_group_array(json_object(
                    'category', category, 'key', key, 'value', value
                )) FROM settings),
                'services', (SELECT json_group_array(json_object(
                    'name', name, 'port', port
                )) FROM services)
            ) AS backup
        """).fetchone()

        backup = json.loads(row["backup"])
        assert backup["backup_version"] == "1.0"
        assert len(backup["settings"]) >= 25
        assert len(backup["services"]) >= 5

    test("Full backup exports valid JSON", test_full_backup_json)

    def test_individual_table_export():
        row = conn.execute("""
            SELECT json_group_array(json_object(
                'version', version, 'is_default', is_default, 'is_active', is_active
            )) AS data FROM php_versions
        """).fetchone()

        data = json.loads(row["data"])
        assert len(data) == 3
        defaults = [d for d in data if d["is_default"] == 1]
        assert len(defaults) == 1

    test("Individual table export to JSON", test_individual_table_export)


# ==========================================================================
# Phase 9: Rollback Migration
# ==========================================================================

def test_rollback(conn):
    print("\n[Phase 9] Rollback Migration")

    def run_rollback():
        execute_sql_file(conn, "migrations/001_initial_down.sql")
        rows = conn.execute(
            "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'"
        ).fetchall()
        assert len(rows) == 0, f"Expected 0 tables after rollback, got {len(rows)}: {[r['name'] for r in rows]}"

    test("Rollback migration drops all tables", run_rollback)

    def re_apply_migration():
        """Re-apply everything to confirm schema is repeatable."""
        execute_sql_file(conn, "migrations/001_initial.sql")
        execute_sql_file(conn, "triggers.sql")
        execute_sql_file(conn, "views.sql")
        execute_sql_file(conn, "indexes.sql")
        execute_sql_file(conn, "seed.sql")

        count = conn.execute("SELECT COUNT(*) AS c FROM settings").fetchone()["c"]
        assert count >= 25, "Schema should be fully functional after re-apply"

    test("Re-apply all migrations successfully", re_apply_migration)


# ==========================================================================
# Main
# ==========================================================================

def main():
    global passed, failed

    print("=" * 70)
    print("NKS WebDev Console Database Schema Test Suite")
    print("=" * 70)
    print(f"Database: {DB_PATH}")
    print(f"Verbose:  {VERBOSE}")
    print(f"Keep DB:  {KEEP_DB}")

    conn = None
    try:
        conn = create_fresh_database()

        test_migration(conn)
        test_triggers_views_indexes(conn)
        test_seed(conn)
        test_constraints(conn)
        test_triggers(conn)
        test_views(conn)
        test_queries(conn)
        test_export(conn)
        test_rollback(conn)

    except Exception as e:
        print(f"\nFATAL ERROR: {e}")
        traceback.print_exc()
        failed += 1
    finally:
        if conn:
            conn.close()
        if not KEEP_DB and DB_PATH.exists():
            os.remove(DB_PATH)

    # Summary
    total = passed + failed
    print("\n" + "=" * 70)
    print(f"Results: {passed}/{total} passed, {failed} failed")
    if errors:
        print("\nFailures:")
        for name, err in errors:
            print(f"  - {name}: {err}")
    print("=" * 70)

    return 0 if failed == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
