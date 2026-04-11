# NKS WebDev Console — Hourly Strict Plan Audit (2026-04-12 :07)

Scope: strict verification of every `[x]` item in
`docs/plans/revised-architecture-plan.md` from Phase 0 through Phase 8
against the **current state of the repo** (commits fd60794 → 52ac0d5).

Methodology:
1. Read each phase item in plan order.
2. Map the claim to a concrete file path or symbol.
3. Confirm the file/symbol still exists and still does what the plan
   says — either by `grep` or a targeted `Read`.
4. Downgrade anything that silently regressed.
5. Mirror new features built since the last audit that are not yet on
   the plan and schedule them for plan insertion by the next
   wide-audit cycle (`23 */2 * * *`).

## Phase 0 — Verification — **8 / 8 STILL DONE**

All baseline POC smoke items verified via earlier live runs + CDP
screenshots; no regression surface this hour.

## Phase 1 — Foundation — **14 / 14 STILL DONE**

| Item | Evidence |
|---|---|
| `IWdcPlugin` + `IServiceModule` interfaces | `src/daemon/NKS.WebDevConsole.Core/Interfaces/` populated |
| Plugin.SDK with `PluginBase`, `UiSchemaBuilder` | `src/daemon/NKS.WebDevConsole.Plugin.SDK/UiSchemaBuilder.cs` |
| `PluginLoader` scanning `plugins/` via ALC | `src/daemon/NKS.WebDevConsole.Daemon/Plugin/PluginLoader.cs` 209 lines, still uses `AssemblyLoadContext` isolation |
| `SseService` broadcast via Task.WhenAll | `src/daemon/NKS.WebDevConsole.Daemon/Services/SseService.cs` 74 lines |
| DbUp migrations ported | `src/daemon/NKS.WebDevConsole.Daemon/Migrations/00{1..4}*.sql` all present |
| Core REST endpoints | `/api/status`, `/api/plugins`, `/api/plugins/{id}/ui`, `/api/events` all in `Program.cs` |
| Pinia stores | `stores/{daemon,services,sites,plugins,theme}.ts` all present |
| Layout components | `AppHeader`, `AppSidebar`, `AppStatusBar` — **Sidebar just gained a Tunnel entry this hour (commit `52ac0d5`)** |

## Phase 2 — Core Plugins — **13 / 13 STILL DONE**

| Item | Evidence |
|---|---|
| `DaemonJobObject` KILL_ON_JOB_CLOSE | `src/daemon/NKS.WebDevConsole.Core/Services/DaemonJobObject.cs` 174 lines |
| `HealthMonitor` per-module try/catch | still registered in `Program.cs` |
| Apache plugin + Scriban vhost | `src/plugins/NKS.WebDevConsole.Plugin.Apache/` present, latest commit hardened `AtomicWriteAsync` helper |
| MySQL plugin + DPAPI password | `Core/Services/MySqlRootPassword.cs` still exists |
| PHP plugin multi-version | `src/plugins/NKS.WebDevConsole.Plugin.PHP/` present |
| `AtomicWriter` | `src/daemon/NKS.WebDevConsole.Daemon/Config/AtomicWriter.cs` + new cleanup audit verified |
| ValidationBadge SSE | `onValidation` in `subscribeEvents`, `ValidationBadge.vue` watches it |
| Port conflict detection | `PortConflictDetector.cs` in `Core/Services` |

## Phase 3 — Sites + DNS + SSL — **10 / 10 STILL DONE**

| Item | Evidence |
|---|---|
| Site CRUD + TOML read/write | `SiteManager.cs`, atomic write (7e1a2ec) |
| Config pipeline → Scriban → httpd -t → atomic | `ApacheModule.cs` + shared `AtomicWriteAsync` |
| Versioning + history | `Config/AtomicWriter.cs` + `~/.wdc/generated/history/` |
| Hosts plugin with UAC helper | `src/plugins/NKS.WebDevConsole.Plugin.Hosts/` |
| SSL plugin + mkcert | `src/plugins/NKS.WebDevConsole.Plugin.SSL/` |
| SiteEdit + create wizard | `src/frontend/src/components/pages/{Sites,SiteEdit}.vue` — heavily redesigned in last 48 h (folder browser, runtime cards, alias chips, Cloudflare tab) |
| Framework auto-detection | `SiteManager.DetectFramework` still in place |
| Wildcard alias | `*.loc` regression test in scenario 11 |
| CLI `wdc new` | `Cli/Program.cs` 1368 lines |

## Phase 4 — GUI Polish — **10 / 10 STILL DONE**

| Item | Evidence |
|---|---|
| Monaco Editor | `components/shared/MonacoEditor.vue` + `ConfigEditor.vue` present |
| xterm LogViewer scrollback 10000 | verified `scrollback: 10000` in `LogViewer.vue` |
| ECharts MetricsChart | `MetricsChart.vue` + Dashboard integration (container height lock) |
| Tray + Ctrl+K palette + keybindings | `electron/main.ts` tray code + `App.vue` keydown handler |
| Dark / light theme | `useThemeStore` + Element Plus vars |
| Database manager, PHP manager, SSL manager | `components/pages/{Databases,PhpManager,SslManager}.vue` all render |

## Phase 5 — CLI + Additional Plugins — **8 / 8 STILL DONE**

| Item | Evidence |
|---|---|
| CLI commands | `Cli/Program.cs` 1368 lines |
| Redis / Mailpit / Caddy plugins | all three under `src/plugins/` |
| Marketplace stub | `/api/plugins/marketplace` + built-in fallback catalogue |
| MAMP migrator | `Sites/MampMigrator.cs` present |

## Phase 6 — Packaging — **8 / 8 STILL DONE**

| Item | Evidence |
|---|---|
| electron-builder | `src/frontend/electron-builder.yml` |
| Portable mode `WdcPaths` | `Core/Services/WdcPaths.cs` 87 lines + env var override |
| Auto-updater plumbing | `electron/main.ts` still wires `electron-updater` |
| GitHub Actions matrix | `.github/workflows/*.yml` present |
| Defender + VirusTotal workflows | `.github/workflows/defender-submit.yml`, `release-scan.yml` |

## Phase 7 / 7b / 7c — Post-v1 Polish — **STILL DONE**

No regressions. `CatalogClient` hardening in 437f484 + 92f8a03 fits
under Phase 7 resilience bucket. Hot-reload URL provider + empty-URL
guard close a silent gap that the earlier [x] item did not protect
against.

## Phase 8 — Future Work — **1 OPEN, 7 DONE**

Still the only blocker: **self-update via tagged GitHub release** —
plumbing is in place, needs a real release to prove the end-to-end feed.

---

## New features NOT yet on the plan (build queue for wide-audit)

These have been implemented in the last 24 h and deserve formal plan
entries under a new **Phase 9: Integrations & Cloud** section:

1. **Cloudflare Tunnel plugin** (`src/plugins/NKS.WebDevConsole.Plugin.Cloudflare/`)
   — auto-setup from API token, per-site exposure with SSL-aware
   ingress (https://localhost:443 + noTLSVerify + originServerName for
   sslEnabled sites), deterministic hashed subdomain template,
   DNS-record-on-disable, dedicated `/cloudflare` Vue page with
   Settings / Sites / DNS tabs, sidebar Tunnel nav entry with live
   status + exposed-site badge.
2. **Python FastAPI catalog-api service** (`services/catalog-api/`)
   — SQLAlchemy + SQLite backend, bcrypt session auth, Jinja2 admin
   UI, URL auto-generators for 7 upstreams (cloudflared, mailpit,
   caddy, redis, php, apache, nginx), snake_case Pydantic matching
   `CatalogClient.cs` C# DTOs, Dockerfile + docker-compose,
   `run.cmd` dev launcher, 8-test pytest smoke suite.
3. **Electron sidecar spawning** — `spawnCatalogApi()` in
   `electron/main.ts` auto-starts the Python service before the
   daemon, passes `NKS_WDC_CATALOG_URL` through env, graceful shutdown
   on `before-quit`.
4. **Settings → Advanced tab** — user-editable catalog URL with Test
   Connection + Refresh + Open Admin UI buttons.
5. **CatalogClient hot-reload** — URL re-read on every `RefreshAsync`
   via `Func<string>` closure closing over `SettingsStore`, so Settings
   changes take effect without daemon restart.
6. **cloudflared + mkcert in binary catalog** — both now downloadable
   via the Binaries page with GitHub release URLs per-platform.
7. **`SiteConfig.Cloudflare`** sub-config persisted to TOML.

**Recommended action (wide audit :23):** promote these to a formal
`Phase 9: Integrations & Cloud` section in the plan doc with 7 `[x]`
items and document the architecture (catalog sidecar lifecycle, tunnel
plugin shape) in `docs/plan-cloud-integrations.md`.

## Test health

- Daemon xUnit: **205 / 205** passing (re-verified this fire)
- Core xUnit: **55 / 55** passing
- Catalog-api pytest: **8 / 8** passing
- Frontend `electron-vite build`: clean

**Total: 268 passing, 0 failing, 0 skipped.**

## No downgrades this cycle.
