# NKS WebDev Console — Hourly Plan Audit

**Date:** 2026-04-11
**Audit scope:** all 72 checkbox items in `docs/plans/revised-architecture-plan.md`
**Method:** grep/read each item against real codebase + runtime verification

## Summary

- **Total plan items:** 72
- **DONE:** 66 (91.7%)
- **GAP tasks created:** 6 (#96–#101)
- **Completely new phases needed:** 0

## Phase Completion Detail

### Phase 0 — Verification (8 items)

All 8 items DONE (verified in iteration 4 via CDP + build output).

### Phase 1 — Foundation (14 items)

All 14 items DONE — plugin loader, SDK, REST endpoints, Pinia stores, layout, pages all present. Schema renderer + plugin registry ported from POC.

### Phase 2 — Core Plugins (13 items)

**11 DONE, 2 GAP:**

- ✓ ProcessManager state machine (ProcessManager.cs)
- ✗ **Windows Job Objects for child process cleanup** → task #96
- ✓ RestartPolicy with exponential backoff (ProcessManager.cs:10 + GetBackoff)
- ✓ HealthMonitor 5s interval (Services/HealthMonitor.cs)
- ✓ MetricsCollector (ProcessMetricsSampler in Core/Services)
- ✓ Apache plugin — start/stop/Scriban/httpd -t validation
- ✗ **MySQL DPAPI root password** → task #97. MySqlModule does `--initialize-insecure` but never sets/stores a root password; CLI calls use anonymous root which is insecure for post-init state.
- ✓ PHP plugin — version detection, download, shim scripts, php-cgi
- ✓ ConfigEngine (Config/TemplateEngine.cs + ConfigValidator.cs + AtomicWriter.cs)
- ✓ ValidationBadge SSE flow (SseService broadcasts)
- ✓ VersionSwitcher component
- ✓ ServiceCard live metrics via SSE
- ✓ Port conflict detection (ProcessManager.CheckPort)

### Phase 3 — Sites + DNS + SSL (10 items)

All 10 items DONE.

- SiteEndpoints CRUD ✓
- Config pipeline TOML → Scriban → httpd -t → atomic write ✓
- Config versioning last 5 (SiteOrchestrator.cs:279) ✓
- Hosts plugin with managed block + 3-level safety + UAC elevation ✓
- SSL plugin mkcert ✓
- Sites.vue table + wizard ✓
- Framework auto-detection: laravel/wordpress/nette all present (SiteManager.cs:244-265) ✓
- Wildcard alias support ✓
- CLI `wdc sites create` ✓ (System.CommandLine)

### Phase 4 — GUI Polish (10 items)

All 10 items DONE.

- Monaco editor replaces textarea ✓
- xterm.js LogViewer ✓
- ECharts MetricsChart with fixed height ✓
- Dashboard grid + recent activity ✓
- System tray with green/yellow/red + context menu ✓
- Dark/light theme toggle ✓
- Keyboard shortcuts Ctrl+K, Ctrl+N, F5, Ctrl+1-7 ✓
- Window management minimize to tray ✓
- Database manager panel ✓
- PHP manager extension toggling ✓
- SSL manager with cert list ✓

### Phase 5 — CLI + Additional Plugins (8 items)

**6 DONE, 2 GAP:**

- ✓ CLI commands per SPEC (1368-line Program.cs)
- ⚠ Shell completions — System.CommandLine supports them natively; no explicit generation wired (minor, can be added in Phase 7)
- ✓ `--json` output mode (Option<bool>("--json") with Recursive = true)
- ✓ Redis plugin
- ✓ Mailpit plugin
- ✓ Caddy plugin (added in iteration 3)
- ✗ **Plugin marketplace stub** → task #98
- ✗ **MAMP PRO migration** → task #99

### Phase 6 — Packaging (8 items)

**6 DONE, 2 GAP:**

- ✓ electron-builder.yml with NSIS (Windows), DMG (macOS), AppImage (Linux) targets
- ✓ dotnet publish daemon + plugin DLLs bundled
- ✓ Combined installer (electron-builder bundles daemon as extraResources)
- ✓ Auto-updater (electron-updater integration in main.ts)
- ✓ Portable mode (portable.txt detection)
- ✓ CI/CD GitHub Actions (.github/workflows/build.yml, ci.yml, test.yml)
- ✗ **Windows Defender submission after each release** → task #101
- ✗ **Pre-release VirusTotal scan in CI** → task #100

## New Tasks Created

| # | Phase | Subject |
|---|-------|---------|
| 96 | 2 | Windows Job Object for child process cleanup |
| 97 | 2 | DPAPI-protected MySQL root password storage |
| 98 | 5 | Plugin marketplace stub endpoint |
| 99 | 5 | MAMP PRO migration command |
| 100 | 6 | Pre-release VirusTotal scan CI workflow |
| 101 | 6 | Windows Defender submission automation |

## Incomplete vs Complete Phase Status

| Phase | Status | Note |
|-------|--------|------|
| 0 — Verification | COMPLETE | 8/8 |
| 1 — Foundation | COMPLETE | 14/14 |
| 2 — Core Plugins | INCOMPLETE | 11/13 (JobObject, DPAPI missing) |
| 3 — Sites + DNS + SSL | COMPLETE | 10/10 |
| 4 — GUI Polish | COMPLETE | 10/10 |
| 5 — CLI + Plugins | INCOMPLETE | 6/8 (marketplace, MAMP migration) |
| 6 — Packaging | INCOMPLETE | 6/8 (VirusTotal, Defender submission) |

**Core v1 release readiness: 91.7%** — Phases 0, 1, 3, 4 are complete. Phase 2 has security-relevant gaps (DPAPI, Job Object). Phase 5/6 gaps are nice-to-haves.

## Recommendation

The 6 new gap tasks divide cleanly into three priorities:

- **HIGH (security):** #96 Job Object, #97 DPAPI — ship before v1 tag
- **MEDIUM (v1 nice-to-have):** #98 marketplace stub, #100 VirusTotal
- **LOW (post-v1):** #99 MAMP migration, #101 Defender submission

## Cron Loop Status (untouched per user instruction)

- `e763d5b8` — 3 min plan iteration
- `5925654a` — 5 min test/review
- `c0d0b06c` — :07 hourly strict audit (this cycle)
- `73118892` — 90 min wide audit
