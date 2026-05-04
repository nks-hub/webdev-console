# PostgreSQL Platform Support Plan

**Date:** 2026-05-04
**Scope:** PostgreSQL as a first-class WDC database engine across binaries, catalog, plugin runtime, daemon APIs, Easy UI, Advanced UI, backups, and CI.
**Current status:** binaries, catalog, production catalog import, plugin runtime, and basic app catalog integration have shipped for required platforms.

## 1. Ownership Decision

PostgreSQL runtime lifecycle is plugin-owned in `webdev-console-plugins`. The `nks-ws` app/daemon owns catalog visibility, settings, ports, firewall-managed port metadata, generic database-engine APIs, Easy/Advanced UI integration, and site/backup workflows. `webdev-console-binaries` owns runtime archives. `wdc-catalog-api` owns metadata validation and release generation.

## 2. Completed Baseline

- `webdev-console-binaries`: PostgreSQL 18.3 packages exist for `windows-x64`, `linux-x64`, and `macos-arm64`; CI smoke passed.
- `wdc-catalog-api`: PostgreSQL catalog generator and production catalog entry exist.
- `webdev-console-plugins`: `NKS.WebDevConsole.Plugin.PostgreSQL` initializes, starts, stops, checks readiness, and reports logs/status.
- `nks-ws`: catalog client, CLI summary, `ports.postgresql`, managed firewall port metadata, service icon, and i18n labels are wired.

## 3. Required App Runtime Work

- Add generic database engine model for MySQL, MariaDB, and PostgreSQL.
- Add `GET /api/databases/engines`.
- Add install/init/start/stop/restart/status operations through plugin/service abstractions.
- Add backup/restore operations using `pg_dump`/`psql` for PostgreSQL and equivalent tools for MySQL/MariaDB.
- Include engine health in `/api/easy/summary` and site summaries.
- Make per-site database engine selection explicit: None, MySQL, MariaDB, PostgreSQL.

## 4. UI Requirements

- Easy Settings / Projects: default database engine selector.
- Easy Settings / Services: PostgreSQL row with status, port, autostart, and restart.
- Dashboard: readiness item only when PostgreSQL is installed, selected, required by a site, or catalog-available.
- Site Detail: database badge and maintenance backup action.
- Advanced Databases: engine selector, data-dir visibility, CRUD/dump/restore, and destructive actions isolated from routine actions.
- Binaries page: show PostgreSQL platform availability and unsupported-platform reasons.

## 5. Platform Matrix

Required supported platforms:

- `windows-x64`
- `linux-x64`
- `macos-arm64`

Extended target platforms:

- `windows-arm64`
- `linux-arm64`
- `macos-x64`

Any missing extended platform must have an explicit catalog reason and tracked follow-up. Required platforms must not silently fall back to a different architecture.

## 6. Verification Commands

Local non-admin checks:

- `dotnet test WebDevConsole.sln --filter CatalogClientTests`
- `dotnet test WebDevConsole.sln --filter BinaryCatalogTests`
- `cd src/frontend && npm run type-check`
- `cd src/frontend && npm run build`

Cross-repo checks:

- `webdev-console-plugins`: build all `NKS.WebDevConsole.Plugin.*.csproj`.
- `wdc-catalog-api`: run generator/catalog validation tests.
- `webdev-console-binaries`: inspect latest PostgreSQL workflow and release assets.

Admin-only lifecycle/e2e checks must not be run as a normal local process. Use GitHub Actions or an approved elevated noninteractive path for hosts, firewall, certificate, or daemon lifecycle slices.

## 7. Remaining Tasks

- [ ] Implement generic database engine API in `nks-ws`.
- [ ] Wire PostgreSQL into Easy and Advanced UI through `databaseEngines`.
- [ ] Add database backup/restore support per engine.
- [ ] Add screenshot coverage for PostgreSQL in Easy Services, Dashboard, Site Detail, Binaries, and Advanced Databases.
- [ ] Add catalog unsupported-platform reason display.
- [ ] Track extended platform support gaps.
