# WDC UI/UX Refactor & Redesign Plan

**Date:** 2026-05-04
**Scope:** Easy and Advanced UI across Settings, Dashboard/Services, Sites, Site Detail, Databases, Binaries, Plugins, SSL, and deploy surfaces.
**Goal:** make WDC intuitive, calm, and operationally complete without hiding power-user controls.

## 1. Product Direction

Easy and Advanced must feel like two densities of the same product, not two unrelated apps.

- **Easy mode:** daily operations with safe defaults, compact status, clear next actions, and no raw internals.
- **Advanced mode:** expert control grouped by task and risk, with dense but scannable tables/forms.
- **Shared language:** the same service status, readiness, database engine, catalog health, and backup semantics everywhere.

The UI should be operational, not marketing-like: no oversized hero cards, no nested cards, no decorative clutter, and no one-color theme drift. Prefer full-width sections, tables, rows, tabs only where the user is switching task families, and compact repeated cards for sites/services.

## 2. Current Problems

- `Settings.vue` still owns too many unrelated workflows and state branches.
- Easy Settings has grown from extracted slices but does not yet own all daily setup.
- Advanced tabs mirror historical implementation boundaries instead of user tasks.
- Dashboard, Sites, Site Detail, Binaries, and Settings use similar statuses with inconsistent visual treatment.
- Some advanced controls are visually louder than higher-frequency daily actions.

## 3. Unified Navigation Model

Keep top-level navigation simple:

- **Sites:** primary project work.
- **Services:** stack health and service control.
- **Databases:** advanced database administration; Easy only gets engine selection/status/backup.
- **Binaries:** installed/available runtimes and catalog health.
- **SSL:** certificate authority and trust management.
- **Plugins:** advanced extension internals.
- **Settings:** preferences, defaults, sync, backups, updates, raw configuration.

In Easy mode, hide advanced-only entries but surface their relevant summaries inside Easy Settings and Dashboard. Advanced mode shows the full nav, but each page should still start with a compact health summary and primary action row.

## 4. Easy Mode Redesign

Easy mode must support normal use without requiring Advanced:

- **Dashboard:** environment health, readiness checklist, running services, recent site activity, pending updates, and backup status.
- **Settings:** General, Projects, Services, Backups, Account & Sync, Updates.
- **Sites list:** domain, status, PHP/HTTPS/tunnel/database badges, last hit, error count, open/restart/menu actions.
- **Site detail:** one scrollable page with URLs, quick config, affected services, activity, and maintenance.

Easy mode rules:

- Show raw paths only as read-only summaries with "change in Advanced".
- Use section-level save state.
- Use explicit destructive confirmations.
- Keep labels short; move explanation into tooltips or secondary text.
- PostgreSQL appears wherever database engines are shown when selected, installed, required by a site, or available in catalog.

## 5. Advanced Mode Redesign

Advanced mode should be powerful without being visually noisy.

### Settings

Split Advanced Settings by task:

- **Network & Ports:** ports, hosts-file path summary, service binding addresses.
- **Paths & Binaries:** binary overrides, catalog cache, executable probes.
- **Databases:** admin credentials, data directories, default engine internals.
- **Catalog & Plugins:** catalog URL override, plugin catalog sync, plugin diagnostics.
- **Automation:** startup, service recovery, update policy, deploy backend cutover.
- **Security & Access:** MCP grants, destructive operation policies, certificates, tokens.
- **About & Diagnostics:** versions, logs, environment export, reset actions.

### Advanced Pages

- **Services:** compact table first; logs/config/details in side panels or secondary rows.
- **Databases:** engine selector plus CRUD/query/dump tools, with destructive actions visually separated.
- **Binaries:** platform/version grid with install/update/status; unsupported platform reason is first-class.
- **Plugins:** plugin inventory grouped by service type, not a long mixed list.
- **SSL:** CA status, installed certs, trust checks, repair actions.

Advanced mode rules:

- Keep dense controls aligned in grids/tables.
- Prefer progressive disclosure for risky or rare controls.
- Separate "inspect", "edit", and "destroy" actions visually.
- Keep warnings specific and actionable, not broad red panels.

## 6. Component Refactor

Create shared primitives before visual changes:

- `SettingsShell.vue` with section navigation and save-state slot.
- `SettingsSection.vue` for heading, description, status, and actions.
- `ServiceControlRow.vue` for status, port, uptime, autostart, start/stop/restart.
- `ReadinessChecklist.vue` for dashboard/settings readiness.
- `StatusBadge.vue` and `HealthStatusDot.vue` for shared service/site/catalog states.
- `UrlCopyChip.vue`, `SimpleMetricTile.vue`, `InlineErrorPreview.vue`.
- `AdvancedDataTable.vue` wrapper for dense tables with consistent empty/loading/error states.

Stores/API shape:

- `environmentHealth` aggregates services, sites, catalog, backups, updates.
- `settingsDraft` manages dirty state and section saves.
- `databaseEngines` normalizes MySQL, MariaDB, PostgreSQL.
- `backups` owns schedule, list, create, restore.

## 7. Implementation Order

1. Extract settings components without changing behavior.
2. Add shared status/service/readiness primitives.
3. Rebuild Easy Settings IA and Dashboard.
4. Refactor Sites list and Site Detail to shared primitives.
5. Reorganize Advanced Settings into task groups.
6. Apply advanced page polish to Services, Databases, Binaries, Plugins, SSL.
7. Add PostgreSQL/database-engine UI consistently.
8. Run visual and functional verification.

## 8. Testing & Review

- `cd src/frontend && npm run type-check`
- `cd src/frontend && npm run build`
- Playwright screenshots: Dashboard, Easy Settings, Advanced Settings, Sites list, Site detail, Binaries, Databases.
- Desktop and mobile viewport checks for no overlap, clipping, or unstable row heights.
- API contract tests for `/api/easy/summary`, `/api/easy/sites`, and `/api/databases/engines`.
- Do not run privileged local daemon/e2e paths as a normal local process; use CI or an approved elevated noninteractive path for admin-only checks.

## 9. Acceptance Criteria

- Easy mode covers daily operation without exposing raw internals.
- Advanced mode exposes all current capabilities with clearer grouping and lower visual noise.
- Shared components make service/site/catalog/database states visually consistent.
- Settings route is a shell; feature sections own their own UI.
- PostgreSQL is integrated as one database engine, not special-cased UI.
- Screenshot review confirms desktop/mobile readability and no clutter regressions.
