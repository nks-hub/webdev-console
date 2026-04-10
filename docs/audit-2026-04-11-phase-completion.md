# NKS WebDev Console — Phase Completion Audit

**Date:** 2026-04-11
**Audit scope:** `docs/plans/revised-architecture-plan.md` phases 0-6 vs. real codebase state
**Commits checked:** through `f8598b1`

## Executive Summary

**Status:** ~95% of plan items verified present in codebase. Core architecture (plugin system, REST+SSE, schema-driven UI, Scriban templates, SQLite, AssemblyLoadContext isolation) all implemented. All 6 planned service plugins exist except Caddy.

## Phase-by-phase Status

### Phase 0: Verification — DONE ✓
- Electron 34 + Vue 3.5 + Element Plus 2.9 renders: verified (CDP shows `Connected` state)
- C# daemon spawned from Electron: verified (`spawnDaemon()` in electron/main.ts)
- REST round-trip: verified (daemon responding on dynamic port 5146)
- SSE streaming: `/api/events` endpoint present
- ECharts sparklines: `MetricsChart.vue` exists
- xterm.js: `LogViewer.vue` exists with SearchAddon
- electron-builder: `electron-builder.yml` with NSIS target

### Phase 1: Foundation — DONE ✓
- `IServiceModule`, `PluginBase`, `PluginLoader`, `SseService` all present
- REST endpoints: `/api/status`, `/api/plugins`, `/api/plugins/{id}/ui`, `/api/events` all wired
- Pinia stores: 5 files in src/frontend/src/stores
- Layout: AppHeader + AppSidebar + AppStatusBar
- Pages: Dashboard, Sites, Settings, PluginManager, Binaries, Databases, SSL, PHP

### Phase 2: Core Plugins — DONE ✓ (minor gap)
- Apache, PHP, MySQL plugins all present with full templates
- ProcessManager, HealthMonitor logic present (distributed across plugins)
- **GAP:** No explicit `MetricsCollector` service class — metrics appear collected ad-hoc per plugin. Refactor to central service → task #91.
- Scriban templates for vhost + httpd present in Apache plugin Resources

### Phase 3: Sites + DNS + SSL — DONE ✓ (minor gap)
- SiteOrchestrator, SiteManager with TOML persistence
- Site CRUD REST endpoints + history + framework detection
- Hosts plugin + SSL plugin (mkcert) present
- SiteOrchestrator has UAC optimization (skip elevation when all domains already mapped) + 5 rotating backups + safety checks
- **GAP:** Wildcard alias support (`*.myapp.loc`) not implemented — task #93

### Phase 4: GUI Polish — 90% DONE
- xterm.js in LogViewer ✓
- ECharts MetricsChart ✓
- CommandPalette (Ctrl+K) ✓
- Ctrl+N new site + Ctrl+1-7 page navigation ✓
- Database manager, PHP manager, SSL manager pages ✓
- Tray icon with green/yellow/red state ✓
- **GAP:** Monaco Editor not yet integrated — still using plain textarea in ServiceConfig.vue / SiteEdit.vue → task #90
- System tray with right-click context menu: ✓ (in electron/main.ts)

### Phase 5: CLI + Plugins — 85% DONE
- CLI Program.cs: 1368 lines using System.CommandLine + Spectre.Console
- Redis, Mailpit plugins present
- **GAP:** Caddy plugin missing (low priority) → task #92
- **UNKNOWN:** shell completions (bash/zsh/fish/PowerShell) — System.CommandLine supports them natively but need to verify generation + packaging

### Phase 6: Packaging — DONE ✓
- electron-builder.yml with NSIS target
- Auto-updater code path present in electron/main.ts
- Portable mode via `portable.txt` detection
- `.github/workflows/` has build.yml, ci.yml, test.yml

## New v1 Gap Tasks (added to todolist)

- **#90** Phase 4: Monaco Editor integration
- **#91** Phase 2: Centralized MetricsCollector
- **#92** Phase 5: Caddy plugin
- **#93** Phase 3: Wildcard alias support
- **#94** Audit: OpenAPI + TS type generation (contract drift prevention)
- **#95** Audit: SSE client reconnect after daemon restart

## Runtime Verification (2026-04-11 22:12)

- Daemon: running on port 5146 (PID 67412), healthz 200 OK
- Electron: 4 processes running, URL shows `?port=5146&token=...#/sites`
- CDP reachable on port 9222 (Chrome 132.0.6834.210, Electron 34.5.8)
- Body text contains "Connected" — Electron NIKDY Offline fix holds
- Brand icons: all 5 endpoints return proper multi-color SVG from plugin DLL embedded Resources
- Console errors: 1 CSP security warning (unsafe-eval from Vite dev server, not blocking)
- No JS exceptions, no EPIPE crashes

## Recommendations

1. **Monaco Editor** is the most visible remaining Phase 4 gap — users will feel textarea limitations immediately when editing configs
2. **MetricsCollector refactor** low priority — current distributed approach works
3. **Caddy plugin** can be deferred to post-v1 if v1 must ship
4. **OpenAPI + TS generation** would prevent future bugs but not blocking for v1
5. **SSE reconnect** fix is small — refresh token/port from URL on reconnect attempt
