# NKS WebDev Console -- Revised Architecture Plan

**Version:** 2.0.0  
**Date:** 2026-04-09  
**Status:** Supersedes original SPEC.md architecture (Avalonia + gRPC)  
**Stack:** Electron + Vue 3 + Element Plus (frontend) | C# .NET 9 Worker Service (daemon) | REST + SSE (IPC)  
**Validated by:** `wdc-poc/` (20+ files, Electron spawn, REST API, SSE events, plugin schema rendering)

---

## 1. Architecture Overview

### Component Diagram

```
 Electron App (main process)
 ├── spawns C# Daemon as child process
 ├── manages system tray (icon, context menu)
 ├── preload.ts exposes daemonApi.getPort() via contextBridge
 └── loads Vue renderer

 Vue 3 Renderer (BrowserWindow)
 ├── Element Plus dark theme
 ├── Pinia stores (daemon, services, sites, plugins)
 ├── REST client (fetch wrapper → http://localhost:{port}/api/*)
 ├── SSE client (EventSource → /api/events, /api/logs/{id}/stream)
 ├── SchemaRenderer: resolves panel type → Vue component
 └── PluginRegistry: built-in + dynamic bundle loading

 C# Daemon (ASP.NET Core Minimal API, wdc-daemon.exe)
 ├── REST endpoints: /api/status, /api/services/*, /api/sites/*, /api/plugins/*
 ├── SSE hub: /api/events (broadcasts service state, progress, validation)
 ├── PluginLoader: AssemblyLoadContext per plugin DLL
 ├── ProcessManager: supervises child processes (Apache, MySQL, PHP-CGI, Redis)
 ├── ConfigEngine: Scriban templates → validate → atomic write
 ├── SQLite (state.db via Microsoft.Data.Sqlite + Dapper)
 └── Each plugin registers its own /api/{pluginId}/* routes + returns JSON UI schema

 CLI (wdc.exe, System.CommandLine)
 └── same REST client as frontend, outputs via Spectre.Console
```

### Process Model

Three OS processes at runtime:

| Process | Binary | RAM Target | Role |
|---------|--------|-----------|------|
| Electron | `nks-wdc-app.exe` | 150-200 MB | Window, tray, renderer |
| Daemon | `wdc-daemon.exe` | 30-50 MB | Service management, API, plugins |
| CLI | `wdc.exe` | 10 MB (transient) | Scripting, automation |

Electron spawns the daemon via `child_process.spawn`. In development: `dotnet run --project daemon/`. In production: `wdc-daemon.exe` bundled alongside the Electron app. The daemon writes a port file to `%TEMP%/nks-wdc-daemon.port`; the preload script reads it to configure the API base URL. Already validated in `wdc-poc/electron/main.ts` and `wdc-poc/electron/preload.ts`.

### Data Flow (User Action to UI Update)

```
User clicks "Start Apache"
  → Vue: servicesStore.start("apache")
    → fetch POST /api/services/apache/start
      → Daemon: routes to ApachePlugin.StartAsync()
        → ProcessManager.SpawnAsync("httpd.exe", args, jobObject)
          → HealthMonitor polls port 80 (TCP connect, 20x500ms)
            → State transitions: Starting → Running
              → SSE broadcast: { event: "service", data: { id: "apache", status: "running", pid: 1234 } }
                → daemonStore.onService() updates reactive ref
                  → ServiceCard re-renders with green badge
```

For config changes, the pipeline adds a validation step visible in the UI:

```
User edits vhost config → Save & Apply button
  → POST /api/sites/{domain}/config { content }
    → Daemon: render Scriban → write .tmp → httpd -t → atomic rename
      → SSE: { event: "validation", data: { phase: "validating" } }
      → SSE: { event: "validation", data: { phase: "passed" } }
        → ValidationBadge shows green checkmark for 2s
          → SSE: { event: "service", data: { id: "apache", status: "running" } } (after reload)
```

---

## 2. Project Structure

```
nks-wdc/
├── src/
│   ├── frontend/                          # Electron + Vue 3 app
│   │   ├── electron/
│   │   │   ├── main.ts                    # Electron main: spawn daemon, tray, window lifecycle
│   │   │   └── preload.ts                 # contextBridge: daemonApi.getPort()
│   │   ├── src/
│   │   │   ├── main.ts                    # Vue entry: Element Plus, Pinia, router, panel registration
│   │   │   ├── App.vue                    # Root layout: header + sidebar + router-view + status bar
│   │   │   ├── api/
│   │   │   │   ├── daemon.ts              # REST client (fetch wrapper) + SSE subscribeEvents()
│   │   │   │   └── types.ts              # TypeScript interfaces mirroring daemon models
│   │   │   ├── stores/
│   │   │   │   ├── daemon.ts              # Connection state, SSE listener, polling fallback
│   │   │   │   ├── services.ts            # start/stop/restart with busy tracking
│   │   │   │   ├── sites.ts               # CRUD for virtual hosts
│   │   │   │   └── plugins.ts             # Plugin manifests, UI definitions, sidebar categories
│   │   │   ├── plugins/
│   │   │   │   ├── PluginRegistry.ts      # Panel type → Vue component map + dynamic bundle loader
│   │   │   │   └── SchemaRenderer.vue     # Iterates definition.panels, resolves components, renders
│   │   │   ├── components/
│   │   │   │   ├── shared/
│   │   │   │   │   ├── ServiceCard.vue    # Status card: name, state badge, CPU/RAM, start/stop/restart
│   │   │   │   │   ├── VersionSwitcher.vue# Version grid with validation-before-switch
│   │   │   │   │   ├── ConfigEditor.vue   # Textarea (POC) / Monaco (prod), validate-then-apply
│   │   │   │   │   ├── ValidationBadge.vue# "Validating... Passed" UX pattern
│   │   │   │   │   ├── LogViewer.vue      # SSE-backed real-time log with level filter
│   │   │   │   │   └── MetricsChart.vue   # ECharts sparkline for CPU/RAM per service
│   │   │   │   ├── layout/
│   │   │   │   │   ├── AppHeader.vue      # Logo, service status dots, daemon connection tag
│   │   │   │   │   ├── AppSidebar.vue     # Fixed items + dynamic plugin categories from store
│   │   │   │   │   └── AppStatusBar.vue   # Daemon status, running count, version
│   │   │   │   └── pages/
│   │   │   │       ├── Dashboard.vue      # Service grid, quick actions, summary stats
│   │   │   │       ├── Sites.vue          # Table, detail drawer, create wizard dialog
│   │   │   │       ├── Settings.vue       # Ports, DNS, theme, startup, about tabs
│   │   │   │       ├── PluginManager.vue  # Plugin table with enable/disable toggles
│   │   │   │       └── PluginPage.vue     # Dynamic page: loads UI def, passes to SchemaRenderer
│   │   │   └── router/
│   │   │       └── index.ts              # Hash router: /dashboard, /sites, /settings, /plugin/:id
│   │   ├── package.json                   # electron 34, vue 3.5, element-plus 2.9, pinia, echarts
│   │   ├── electron.vite.config.ts        # electron-vite: main, preload, renderer builds
│   │   ├── electron-builder.json          # NSIS (win), DMG (mac), AppImage (linux)
│   │   └── tsconfig.json
│   │
│   ├── daemon/                            # C# .NET 9 solution
│   │   ├── NKS.WebDevConsole.Daemon/               # ASP.NET Core Minimal API host
│   │   │   ├── Program.cs                 # WebApplication builder, CORS, plugin route registration
│   │   │   ├── Api/
│   │   │   │   ├── StatusEndpoints.cs     # GET /api/status
│   │   │   │   ├── ServiceEndpoints.cs    # POST /api/services/{id}/start|stop|restart
│   │   │   │   ├── SiteEndpoints.cs       # CRUD /api/sites
│   │   │   │   ├── PluginEndpoints.cs     # GET /api/plugins, /api/plugins/{id}/ui
│   │   │   │   ├── SseHub.cs              # GET /api/events (SSE), GET /api/logs/{id}/stream
│   │   │   │   └── PhpEndpoints.cs        # /api/php/versions, /api/php/install
│   │   │   ├── Services/
│   │   │   │   ├── ProcessManager.cs      # ServiceUnit state machine, Job Objects, restart policy
│   │   │   │   ├── HealthMonitor.cs       # 5s interval: PID alive + TCP port + HTTP/mysqladmin ping
│   │   │   │   ├── MetricsCollector.cs    # CPU%, WorkingSet64, uptime per service
│   │   │   │   └── SseService.cs          # In-memory SSE broadcast: AddClient/RemoveClient/Broadcast
│   │   │   ├── Config/
│   │   │   │   ├── TemplateEngine.cs      # Scriban render from TOML model
│   │   │   │   ├── ConfigValidator.cs     # httpd -t, nginx -t via CliWrap
│   │   │   │   └── AtomicWriter.cs        # .tmp → validate → archive → rename
│   │   │   ├── Data/
│   │   │   │   ├── Database.cs            # SQLite init, PRAGMA, Dapper queries
│   │   │   │   └── MigrationRunner.cs     # Sequential SQL file execution from migrations/
│   │   │   └── Plugin/
│   │   │       ├── PluginLoader.cs        # AssemblyLoadContext per DLL, manifest parsing
│   │   │       └── PluginHost.cs          # DI registration, route mapping, lifecycle
│   │   │
│   │   ├── NKS.WebDevConsole.Core/                 # Shared library (no daemon dependencies)
│   │   │   ├── Interfaces/
│   │   │   │   ├── IPluginModule.cs       # Main plugin contract
│   │   │   │   ├── IServicePlugin.cs      # extends IPluginModule for managed services
│   │   │   │   └── IConfigProvider.cs     # Config read/write abstraction
│   │   │   ├── Models/
│   │   │   │   ├── ServiceState.cs        # Enum: Stopped, Starting, Running, Stopping, Crashed, Disabled
│   │   │   │   ├── ServiceStatus.cs       # Record: state, pid, cpu, memory, uptime
│   │   │   │   ├── PluginManifest.cs      # Matches plugin.json structure
│   │   │   │   ├── PluginUiDefinition.cs  # JSON UI schema: panels[], category, icon
│   │   │   │   ├── PanelDefinition.cs     # type + props dictionary
│   │   │   │   ├── ValidationResult.cs    # IsValid, Errors list
│   │   │   │   └── SiteConfig.cs          # TOML-mapped site model
│   │   │   └── Configuration/
│   │   │       └── AppPaths.cs            # %APPDATA%\NKS WebDev Console paths: bin/, sites/, ssl/, data/
│   │   │
│   │   ├── NKS.WebDevConsole.Plugin.SDK/           # NuGet package for third-party plugin authors
│   │   │   ├── NKS.WebDevConsole.Plugin.SDK.csproj
│   │   │   ├── PluginBase.cs              # Abstract base with default implementations
│   │   │   ├── UiSchemaBuilder.cs         # Fluent API: .AddPanel("service-status-card", props)
│   │   │   └── EndpointRegistration.cs    # Helper to register plugin-scoped routes
│   │   │
│   │   └── NKS.WebDevConsole.Cli/                  # CLI client
│   │       ├── Program.cs                 # System.CommandLine root, REST client setup
│   │       └── Commands/
│   │           ├── StatusCommand.cs
│   │           ├── ServiceCommands.cs
│   │           ├── SiteCommands.cs
│   │           ├── PhpCommands.cs
│   │           ├── SslCommands.cs
│   │           └── DbCommands.cs
│   │
│   └── plugins/                           # Built-in service plugins (each = separate .csproj → DLL)
│       ├── NKS.WebDevConsole.Plugin.Apache/
│       │   ├── ApachePlugin.cs            # IServicePlugin: start/stop/reload httpd
│       │   ├── ApacheUiSchema.cs          # Returns panels: service-card + config-editor + log-viewer
│       │   ├── Templates/                 # Scriban: apache-vhost.conf, httpd-main.conf
│       │   └── plugin.json                # Manifest: id, name, version, permissions
│       ├── NKS.WebDevConsole.Plugin.MySQL/
│       │   ├── MySqlPlugin.cs             # IServicePlugin: init datadir, start/stop mysqld
│       │   ├── MySqlEndpoints.cs          # Extra routes: /api/mysql/databases, /api/mysql/import
│       │   ├── MySqlUiSchema.cs           # Panels: service-card + db-manager (custom panel)
│       │   └── plugin.json
│       ├── NKS.WebDevConsole.Plugin.PHP/
│       │   ├── PhpPlugin.cs               # IPluginModule (not a service): version management
│       │   ├── PhpVersionManager.cs       # Download, extract, shim creation, php.ini generation
│       │   ├── PhpCgiManager.cs           # Windows: php-cgi.exe via mod_fcgid
│       │   ├── PhpUiSchema.cs             # Panels: version-switcher + config-editor
│       │   └── plugin.json
│       ├── NKS.WebDevConsole.Plugin.Hosts/
│       │   ├── HostsPlugin.cs             # Managed block in hosts file, elevation helper
│       │   └── plugin.json
│       ├── NKS.WebDevConsole.Plugin.SSL/
│       │   ├── SslPlugin.cs               # mkcert wrapper, CA install, per-site cert generation
│       │   └── plugin.json
│       ├── NKS.WebDevConsole.Plugin.Redis/
│       │   ├── RedisPlugin.cs             # IServicePlugin: start/stop redis-server
│       │   └── plugin.json
│       └── NKS.WebDevConsole.Plugin.Mailpit/
│           ├── MailpitPlugin.cs           # IServicePlugin: start/stop mailpit
│           └── plugin.json
│
├── WebDevConsole.sln                           # References all .csproj files
├── package.json                           # Workspace root (scripts: dev, build, package)
├── prototype/database/                    # Canonical SQLite DDL (use verbatim)
│   ├── migrations/001_initial.sql
│   ├── triggers.sql
│   ├── views.sql
│   ├── indexes.sql
│   └── seed.sql
└── docs/
```

### Key NuGet Packages (Daemon)

| Package | Version | Purpose |
|---------|---------|---------|
| Microsoft.Data.Sqlite | 9.0+ | SQLite access |
| Dapper | 2.1+ | Lightweight ORM |
| Scriban | 7.1+ | Config template rendering |
| CliWrap | 3.10+ | Subprocess execution (httpd -t, mkcert) |
| Tomlyn | 0.17+ | TOML parsing for site configs |
| Serilog + Serilog.Sinks.File | 4.3+ / 7.0+ | Structured logging |
| dbup-sqlite | 6.0+ | Schema migrations |
| System.CommandLine | 2.0.5+ | CLI parsing (NKS.WebDevConsole.Cli only) |
| Spectre.Console | 0.55+ | CLI output formatting only |

### Key npm Packages (Frontend)

| Package | Purpose |
|---------|---------|
| electron 34 | Desktop shell |
| electron-vite 3 | Build tooling for main/preload/renderer |
| vue 3.5 | UI framework |
| element-plus 2.9 | Component library (dark theme built-in) |
| pinia 2.2 | State management |
| vue-router 4 | Hash-based routing |
| echarts 5 + vue-echarts | Real-time metrics charts |
| xterm.js 5 | Production log viewer (replaces POC textarea) |
| @monaco-editor/loader | Production config editor |

---

## 3. Phase-by-Phase Implementation

### Phase 0: Verification (1 day) — DONE 2026-04-11

Already largely completed by `wdc-poc/`. Remaining verification:

- [x] Confirm Electron 34 + Vue 3.5 + Element Plus 2.9 dark theme renders correctly (done in POC)
- [x] Confirm C# daemon spawned from Electron main process (done in POC: `wdc-poc/electron/main.ts`)
- [x] Confirm REST API round-trip < 50ms (done in POC: `/api/status`)
- [x] Confirm SSE streaming works for service events (done in POC: `subscribeEvents()`)
- [x] Test ECharts renders a sparkline inside an Element Plus card
- [x] Test xterm.js renders ANSI-colored log output
- [x] Test `dotnet publish --self-contained -r win-x64` daemon binary is not flagged by Defender
- [x] Test Electron-builder produces installable NSIS package

### Phase 1: Foundation (2 weeks) — DONE 2026-04-11

**Goal:** Pluggable daemon with SDK, Electron shell with dynamic sidebar.

- [x] Create `NKS.WebDevConsole.Core` with `IPluginModule` and `IServicePlugin` interfaces
- [x] Create `NKS.WebDevConsole.Plugin.SDK` with `PluginBase`, `UiSchemaBuilder`, `EndpointRegistration`
- [x] Implement `PluginLoader` in daemon: scan `plugins/` directory, load DLLs via `AssemblyLoadContext`
- [x] Implement `PluginHost`: register plugin routes under `/api/{pluginId}/*`, collect UI schemas
- [x] Implement `SseService`: thread-safe client list, `Broadcast(eventType, data)` method (parallel via Task.WhenAll)
- [x] Port `prototype/database/` schema to `MigrationRunner` using dbup-sqlite
- [x] Implement daemon `Program.cs`: WebApplication with CORS, port file, plugin discovery
- [x] Implement core REST endpoints: `/api/status`, `/api/plugins`, `/api/plugins/{id}/ui`, `/api/events`
- [x] Scaffold Electron app from POC: promote `wdc-poc/` structure to `src/frontend/`
- [x] Implement Pinia stores: `daemon`, `services`, `sites`, `plugins` (port from POC)
- [x] Implement `SchemaRenderer` + `PluginRegistry` (port from POC)
- [x] Implement layout: `AppHeader`, `AppSidebar` (dynamic categories), `AppStatusBar`
- [x] Implement pages: `Dashboard`, `PluginPage`, `PluginManager`, `Settings`
- [x] Wire sidebar navigation: fixed items (Dashboard, Sites, Settings, Plugins) + dynamic plugin entries

**Acceptance:** Daemon starts, loads a stub plugin DLL, exposes its UI schema via REST. Electron connects, sidebar shows the plugin, clicking it renders SchemaRenderer with a service-status-card panel.

### Phase 2: Core Plugins (3 weeks) — DONE 2026-04-11

**Goal:** Apache, MySQL, and PHP fully managed with config validation.

- [x] Implement `ProcessManager`: `ServiceUnit` state machine (Stopped/Starting/Running/Stopping/Crashed/Disabled)
- [x] Implement Windows Job Objects for child process cleanup (`DaemonJobObject` with KILL_ON_JOB_CLOSE)
- [x] Implement `RestartPolicy`: max 5 restarts in 60s, exponential backoff 2-30s
- [x] Implement `HealthMonitor`: 5s interval, PID alive + TCP port probe (per-module try/catch)
- [x] Implement `MetricsCollector`: CPU% via `Process.TotalProcessorTime`, `WorkingSet64` (centralized `ProcessMetricsSampler` with delta-based CPU)
- [x] **NKS.WebDevConsole.Plugin.Apache**: start/stop httpd, Scriban vhost templates, `httpd -t` validation
- [x] **NKS.WebDevConsole.Plugin.MySQL**: `mysqld --initialize-insecure`, start/stop, root password to DPAPI (`MySqlRootPassword` static class)
- [x] **NKS.WebDevConsole.Plugin.PHP**: version detection, download/extract, shim scripts, php.ini generation, php-cgi.exe management on Windows
- [x] Implement `ConfigEngine`: `TemplateEngine` (Scriban), `ConfigValidator` (CliWrap), `AtomicWriter` (.tmp → validate → archive → rename)
- [x] Implement `ValidationBadge` SSE flow: daemon emits validation phase events, frontend shows Validating/Passed/Failed
- [x] Implement `VersionSwitcher` component connected to PHP plugin's version list endpoint
- [x] Implement `ServiceCard` with live CPU/RAM from `MetricsCollector` via SSE `/api/events`
- [x] Port conflict detection: before binding, check port, identify owner process, suggest alternative

**Acceptance:** Can start Apache + MySQL + assign PHP version to a site via GUI. Config validation blocks invalid configs. Service crash triggers auto-restart within 5s. ECharts sparkline shows CPU usage.

### Phase 3: Sites + DNS + SSL (2 weeks) — DONE 2026-04-11

**Goal:** End-to-end site creation with SSL and DNS.

- [x] Implement `SiteEndpoints`: CRUD `/api/sites`, TOML read/write, SQLite sync
- [x] Implement config pipeline: TOML model → Scriban template → httpd -t → atomic write → graceful reload
- [x] Config versioning: keep last 5 in `generated/history/`, rollback endpoint (timestamp validated, path containment)
- [x] **NKS.WebDevConsole.Plugin.Hosts**: managed block in hosts file, Windows elevation helper (UAC `Verb=runas`), DNS flush; 3-layer safety with pre-check skip + sanity verification
- [x] **NKS.WebDevConsole.Plugin.SSL**: mkcert binary management, CA install, per-site cert generation, certificate tracking
- [x] Implement `Sites.vue` page: table with domain/PHP/SSL/status, full-view edit route, create wizard dialog
- [x] Create Site Wizard: domain input, docroot picker, framework auto-detection, PHP version selector, SSL toggle, database creation option
- [x] Framework auto-detection: scan for `artisan` (Laravel), `wp-config.php` (WordPress), `nette/application` in composer.json (Nette)
- [x] Wildcard alias support: `*.myapp.loc` (Apache ServerAlias + mkcert native; hosts file skips with DNS hint)
- [x] CLI: `wdc new myapp.loc --php=8.2 --ssl --nette`

**Acceptance:** `wdc new myapp.loc --php=8.2 --ssl --nette` creates a working site accessible at `https://myapp.loc` with no browser certificate warning. GUI wizard produces the same result.

### Phase 4: GUI Polish (2 weeks) — DONE 2026-04-11

**Goal:** Production-quality UI with real-time features.

- [x] Replace POC textarea with Monaco Editor in `ConfigEditor.vue` (monaco-editor 0.53.0, custom wdc-dark theme)
- [x] Replace POC log div with xterm.js in `LogViewer.vue` (with SearchAddon)
- [x] Implement `MetricsChart.vue` with ECharts: CPU/RAM sparklines, 60s rolling window (fixed height, no autoresize)
- [x] Dashboard: aggregate service cards, recent activity timeline from `config_history`
- [x] System tray: green/yellow/red icon state, right-click context menu with service list and quick actions
- [x] Dark/light theme toggle (Element Plus CSS vars + design tokens)
- [x] Keyboard shortcuts: Ctrl+K command palette, Ctrl+N new site, F5 refresh, Ctrl+1-7 page navigation
- [x] Window management: minimize to tray on close, restore on tray click, remember size/position
- [x] Database manager panel in MySQL plugin: create/drop DB, import (file upload), export
- [x] PHP manager: extension toggling, php.ini override editor, CLI alias status display
- [x] SSL manager: CA trust status, certificate list, bulk regeneration

**Acceptance:** All screens render correctly in dark and light mode. Log viewer handles 10k+ lines without lag. Metrics charts update in real-time. Tray icon reflects service states.

### Phase 5: CLI + Additional Plugins (2 weeks) — DONE 2026-04-11

**Goal:** Complete CLI and optional plugins.

- [x] Implement all CLI commands per SPEC.md section 15 (1368-line `Cli/Program.cs`)
- [x] Shell completions: System.CommandLine native support (`completionCmd` registered)
- [x] `--json` output mode for all commands (`Recursive = true` global option)
- [x] **NKS.WebDevConsole.Plugin.Redis**: start/stop redis-server, config template, health check via PING
- [x] **NKS.WebDevConsole.Plugin.Mailpit**: start/stop mailpit, SMTP port 1025, UI port 8025
- [x] **NKS.WebDevConsole.Plugin.Caddy**: alternative web server, Caddyfile generation, admin API health check
- [x] Plugin marketplace stub: `/api/plugins/marketplace` fetches remote manifest with graceful fallback + UI tabs
- [x] MAMP PRO migration: vhost parser + `wdc migrate --from=mamp` command + REST endpoints

**Acceptance:** All CLI commands pass tests. `wdc status --json` returns valid JSON. Redis and Mailpit plugins load, start services, appear in sidebar, show schema-driven UI.

### Phase 6: Packaging + Distribution (1 week) — DONE 2026-04-11

**Goal:** Installable product.

- [x] Electron-builder config: NSIS (Windows), DMG (macOS), AppImage (Linux)
- [x] `dotnet publish` daemon + all plugin DLLs → bundled inside Electron resources
- [x] Combined installer: Electron app + daemon binary + plugin DLLs + bundled .NET runtime
- [x] Auto-updater: electron-updater checking GitHub releases
- [x] Portable mode: `.zip` extract, no install needed (detect by presence of `portable.txt`)
- [x] CI/CD: GitHub Actions matrix (windows-2022, macos-14, ubuntu-24.04)
- [x] Windows Defender submission after each release build (`scripts/submit-defender.ps1` + `defender-submit.yml`)
- [x] Pre-release VirusTotal scan in CI pipeline (`release-scan.yml` with VT_API_KEY secret)

**Acceptance:** NSIS installer runs cleanly on Windows 10/11. App starts, daemon spawns, all services manageable. Portable zip works without installation.

---

## v1 Status — 2026-04-11

**ALL PHASES 0-6 COMPLETE.** 72/72 plan items + 28 user-mandated security/UX fixes shipped across 32 commits in a single 2-day session. Runtime verified end-to-end via CDP introspection: Electron remains Connected across daemon restarts thanks to live-refresh preload + fast-retry cascade in the daemon store.

### Phase 7: Post-v1 Polish (in progress, partially started 2026-04-11)

Items discovered during the v1 audit cycle that are valuable but not blocking the v1 tag:

- [ ] Integration test harness running `docs/e2e-scenarios.md` against the real daemon (15 scenarios)
- [ ] Performance baselines: `/api/status` p99 latency, SSE broadcast throughput, Electron cold-start
- [ ] Plugin SDK reference doc + sample "hello-plugin" template
- [x] OpenAPI → TS type regeneration wired into CI (`scripts/generate-api-types.mjs` + `.github/workflows/api-type-check.yml`)
- [ ] Crash-recovery smoke test: kill daemon mid-install, verify partial state cleanup
- [ ] i18n framework bootstrap (vue-i18n + cs/en string extraction)
- [x] Backup/restore CLI command (`wdc backup`, `wdc restore --from file.zip`) — `BackupManager` + REST endpoints + CLI
- [ ] First-run onboarding wizard (binary install nudge, mkcert CA install, hosts permission grant)
- [ ] Sentry/telemetry opt-in with explicit consent
- [ ] Marketplace UI: download + install button (currently only "Copy URL")

---

## 4. Total Timeline

| Phase | Duration | Cumulative |
|-------|----------|-----------|
| Phase 0: Verification | 1 day | 1 day |
| Phase 1: Foundation | 2 weeks | 2.5 weeks |
| Phase 2: Core Plugins | 3 weeks | 5.5 weeks |
| Phase 3: Sites + DNS + SSL | 2 weeks | 7.5 weeks |
| Phase 4: GUI Polish | 2 weeks | 9.5 weeks |
| Phase 5: CLI + Plugins | 2 weeks | 11.5 weeks |
| Phase 6: Packaging | 1 week | 12.5 weeks |

**Total: approximately 12 weeks solo development.** Phase 0 is already 80% complete via the POC.

---

## 5. Risk Analysis for Modular Architecture

### R1: Plugin DLL Version Conflicts (HIGH)

Plugin DLLs may depend on different versions of shared NuGet packages (e.g., Newtonsoft.Json 12 vs 13). **Mitigation:** Each plugin loads in its own `AssemblyLoadContext`, isolating dependencies. The SDK package (`NKS.WebDevConsole.Plugin.SDK`) pins the shared contract types and is loaded in the default context. Plugins reference SDK interfaces, not daemon internals.

### R2: SSE Connection Limits (MEDIUM)

Browsers limit concurrent SSE connections to ~6 per domain. NKS WebDev Console uses 2 SSE streams (events + logs), leaving headroom. **Mitigation:** Multiplex all event types through a single `/api/events` endpoint with named events (`service`, `progress`, `validation`, `metrics`). Log streaming uses a separate endpoint only when the log viewer is open.

### R3: Electron Memory Overhead (MEDIUM)

Electron adds 150-200 MB baseline RAM, which the original SPEC highlighted as a FlyEnv weakness. **Mitigation:** This is the trade-off for proven cross-platform UI, the same stack FlyEnv uses successfully. The C# daemon is lean (30-50 MB vs FlyEnv's all-in-Electron approach). Combined footprint (180-250 MB) is competitive with FlyEnv (250-400 MB) because service management logic runs in the efficient .NET process, not in Node.js.

### R4: REST API Contract Drift (MEDIUM)

Without proto files, TypeScript types and C# models can diverge. **Mitigation:** Generate TypeScript types from C# models using NSwag or a build-time script that reads the daemon's OpenAPI spec (add `Microsoft.AspNetCore.OpenApi` to daemon). Run type-check in CI.

### R5: Plugin UI Schema Expressiveness (LOW-MEDIUM)

JSON schema panels may be too limiting for complex plugin UIs. **Mitigation:** Three-tier approach already validated in POC: (A) built-in panel types cover 80% of cases, (B) plugins can provide a JS bundle URL for custom Vue components, (C) hybrid of both. The `PluginRegistry.loadPluginBundle()` handles dynamic import.

### R6: Windows Antivirus False Positives (LOW)

The .NET daemon uses Framework-Dependent Execution which is not flagged by Defender. The Electron shell is also safe (signed Electron binary). **Mitigation:** Do not use `PublishTrimmed` or `PublishSingleFile` for the daemon. Submit to Microsoft Defender portal after each release.

### R7: Hot Reload During Development (LOW)

Electron + Vite provides HMR for the Vue renderer. The C# daemon requires restart on code changes. **Mitigation:** Use `dotnet watch run` for the daemon during development. The Electron main process detects daemon restart via port file polling and auto-reconnects SSE.

---

## 6. Key Decisions and Trade-offs

### D1: Electron + Vue 3 over Avalonia UI

**Chose:** Electron + Vue 3 + Element Plus.  
**Over:** Avalonia UI 12.x (C# XAML).  
**Rationale:** FlyEnv proves this exact stack works for a dev server manager with 2.7k stars. Element Plus provides 70+ ready-made components (tables, forms, drawers, dialogs, notifications) that would need custom implementation in Avalonia. The POC validates dark theme, service cards, and plugin rendering in ~500 lines of Vue vs estimated 2000+ lines of AXAML. Trade-off: higher baseline RAM (150 MB vs 40 MB for Avalonia), but total system RAM is competitive because the daemon is leaner than FlyEnv's all-Electron approach.

### D2: REST + SSE over gRPC

**Chose:** REST (Minimal API) + SSE (EventSource).  
**Over:** gRPC over named pipes.  
**Rationale:** REST is debuggable with curl/browser DevTools. SSE is natively supported by `EventSource` in Chromium (no client library needed). No proto compilation step. The Electron renderer can call REST directly without IPC bridging. gRPC's bidirectional streaming is unnecessary -- all NKS WebDev Console flows are request/response or server-push. Trade-off: no typed contracts at compile time (mitigate with OpenAPI + generated TypeScript types).

### D3: Each Service as a Separate Plugin DLL

**Chose:** One .csproj per service (Apache, MySQL, PHP, Redis, Mailpit, Hosts, SSL).  
**Over:** Monolithic `Modules/` directory inside the daemon.  
**Rationale:** Enables third-party plugins (e.g., community PostgreSQL or Nginx plugins) without forking the daemon. Each plugin is loaded in its own AssemblyLoadContext for dependency isolation. Plugin DLLs can be added/removed without recompiling the daemon. Trade-off: more .csproj files (9 vs 1), slightly more complex build. Justified by the user's top priority: extensibility ("platforma ne jen tool").

### D4: JSON UI Schema with Panel Registry

**Chose:** Plugins declare UI as JSON (`{ panels: [{ type: "service-status-card", props: {...} }] }`). Frontend resolves panel types to Vue components via `PluginRegistry`.  
**Over:** Plugins shipping their own Vue components (pure bundle approach).  
**Rationale:** 80% of plugins need the same 4-5 panel types (service card, version switcher, config editor, log viewer, metrics chart). JSON schema avoids shipping redundant JS per plugin. For the 20% needing custom UI, the bundle escape hatch exists (`bundleUrl` in manifest). Already validated in `wdc-poc/src/plugins/SchemaRenderer.vue`.

### D5: TOML Site Configs (unchanged from SPEC)

Source of truth for site configuration remains per-site TOML files at `%APPDATA%\NKS WebDev Console\sites\{domain}.toml`. SQLite stores runtime state only. TOML wins on conflict. This decision from the original SPEC is correct and unchanged -- human-editable, diffable, git-friendly configs are a key differentiator over FlyEnv's opaque electron-store JSON.

### D6: Electron Spawns Daemon (not the reverse)

Electron main process spawns the C# daemon as a child process. When the user quits the Electron app, the daemon is killed. This matches FlyEnv's model and avoids the complexity of a Windows Service or launchd agent. For headless/CLI usage, `wdc daemon start` can spawn the daemon independently.

---

*End of revised architecture plan. Supersedes Avalonia + gRPC architecture from SPEC.md sections 2, 4, 16.*  
*Domain logic (config pipeline, service management, site creation, SSL, DNS, PHP versioning, CLI commands, security model, database schema) remains as specified in SPEC.md sections 5-15, 17-20.*