-- ============================================================================
-- NKS WebDev Console Database Schema - Rollback Migration 001
-- Drops all views, triggers, indexes, and tables in reverse dependency order
-- ============================================================================

PRAGMA foreign_keys = OFF;

-- Drop views
DROP VIEW IF EXISTS v_active_sites;
DROP VIEW IF EXISTS v_service_dashboard;
DROP VIEW IF EXISTS v_expiring_certs;
DROP VIEW IF EXISTS v_recent_changes;
DROP VIEW IF EXISTS v_site_summary;

-- Drop triggers (updated_at)
DROP TRIGGER IF EXISTS trg_settings_updated_at;
DROP TRIGGER IF EXISTS trg_php_versions_updated_at;
DROP TRIGGER IF EXISTS trg_services_updated_at;
DROP TRIGGER IF EXISTS trg_sites_updated_at;
DROP TRIGGER IF EXISTS trg_certificates_updated_at;
DROP TRIGGER IF EXISTS trg_databases_updated_at;
DROP TRIGGER IF EXISTS trg_plugins_updated_at;

-- Drop triggers (audit trail — settings)
DROP TRIGGER IF EXISTS trg_settings_audit_insert;
DROP TRIGGER IF EXISTS trg_settings_audit_update;
DROP TRIGGER IF EXISTS trg_settings_audit_delete;

-- Drop triggers (audit trail — sites)
DROP TRIGGER IF EXISTS trg_sites_audit_insert;
DROP TRIGGER IF EXISTS trg_sites_audit_update;
DROP TRIGGER IF EXISTS trg_sites_audit_delete;

-- Drop triggers (audit trail — services)
DROP TRIGGER IF EXISTS trg_services_audit_insert;
DROP TRIGGER IF EXISTS trg_services_audit_update;
DROP TRIGGER IF EXISTS trg_services_audit_delete;

-- Drop triggers (audit trail — php_versions)
DROP TRIGGER IF EXISTS trg_php_versions_audit_insert;
DROP TRIGGER IF EXISTS trg_php_versions_audit_update;
DROP TRIGGER IF EXISTS trg_php_versions_audit_delete;

-- Drop triggers (business rules)
DROP TRIGGER IF EXISTS trg_php_versions_prevent_delete_default;
DROP TRIGGER IF EXISTS trg_php_versions_single_default_insert;
DROP TRIGGER IF EXISTS trg_php_versions_single_default_update;

-- Drop tables (dependent tables first)
DROP TABLE IF EXISTS config_history;
DROP TABLE IF EXISTS plugins;
DROP TABLE IF EXISTS databases;
DROP TABLE IF EXISTS sites;
DROP TABLE IF EXISTS certificates;
DROP TABLE IF EXISTS services;
DROP TABLE IF EXISTS php_versions;
DROP TABLE IF EXISTS settings;

-- Migration tracking last
DROP TABLE IF EXISTS schema_migrations;

PRAGMA foreign_keys = ON;
