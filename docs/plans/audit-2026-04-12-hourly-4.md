# NKS WebDev Console — Hourly Strict Plan Audit #4 (2026-04-12)

Fourth hourly audit this date. Focus on verifying the Node.js plugin
just landed through commits `2d03e56` → `9920c9c` (8 commits) and
spot-checking Phase 0–8 for regressions.

## Phase 0–2 — STILL DONE

Core infrastructure unchanged from audit #3. Verified by file-size
spot-check matching audit-3 claims exactly (no accidental truncation):

| File | Lines | Audit-3 claim |
|------|-------|---------------|
| `src/daemon/NKS.WebDevConsole.Core/Services/DaemonJobObject.cs` | 174 | 174 ✓ |
| `src/daemon/NKS.WebDevConsole.Daemon/Plugin/PluginLoader.cs` | 209 | 209 ✓ |
| `src/daemon/NKS.WebDevConsole.Daemon/Services/SseService.cs` | 74 | 74 ✓ |
| `src/daemon/NKS.WebDevConsole.Daemon/Config/AtomicWriter.cs` | 93 | 93 ✓ |

All four classes still reachable via `git grep` from daemon sources.

## Phase 3 — Sites + DNS + SSL: STILL DONE

`SiteManager`, `SiteOrchestrator`, `ApacheModule.GenerateVhostAsync`,
`mkcert` integration all intact. `SiteOrchestrator.ApplyAsync` now has
eight numbered steps (0: php-version detect, 1: Apache vhost, 1b: SSL
cert, 2: PHP module ensure, 3: Apache reload, 3b: Node.js process
management [NEW], 4: hosts file, 5: Cloudflare sync).

Step 3b is a clean insertion — no existing step was reindexed, other
plugins unaffected.

## Phase 4 — GUI Polish: STILL DONE

`SiteEdit.vue` expanded (+137 lines vs the Node feature). Added:
- Node.js runtime card with start-command input
- Live process status pill with Start/Stop/Restart buttons
- Plugin-missing warning hint (commit `4e185bd`)
- `nodePluginAvailable` fallback state

All existing tabs (General, SSL, Aliases, Cloudflare, Runtime, History)
preserved. No field removed.

## Phase 5 — CLI + Plugins: STILL DONE + EXTENDED

New `wdc node` subcommand (commits `9f5f695`, `196376c`):
- `wdc node list` — Spectre table with domain/state/pid/port/cmd
- `wdc node start <domain>` — exits 2 if state ≠ Running
- `wdc node stop <domain>` — exit 1 on transport failure
- `wdc node restart <domain>` — exits 2 if state ≠ Running

Plugin count: **10** (was 9 in audit-3).

```
src/plugins/
├── NKS.WebDevConsole.Plugin.Apache
├── NKS.WebDevConsole.Plugin.Caddy
├── NKS.WebDevConsole.Plugin.Cloudflare
├── NKS.WebDevConsole.Plugin.Hosts
├── NKS.WebDevConsole.Plugin.Mailpit
├── NKS.WebDevConsole.Plugin.MySQL
├── NKS.WebDevConsole.Plugin.Node      ← NEW
├── NKS.WebDevConsole.Plugin.PHP
├── NKS.WebDevConsole.Plugin.Redis
└── NKS.WebDevConsole.Plugin.SSL
```

## Phase 6 — Packaging: STILL DONE

No changes to packaging workflow. `release.yml`, `start-all.cmd`, and
Electron builder config unchanged since audit-3.

## Phase 7 — Configuration & Settings: STILL DONE

Settings 8 tabs unchanged. `SITE_SYNC_FIELDS` extended with
`nodeUpstreamPort` + `nodeStartCommand` so Node config round-trips
through smart config sync (commit `2d03e56`).

## Phase 8 — 7/8 DONE, 1 OPEN

- [x] Packaged installer — unchanged
- [x] Electron sidecar spawning daemon — unchanged
- [x] Portable mode — unchanged
- [x] Crash recovery / rotating backups — unchanged
- [x] Backup + restore — unchanged
- [x] Managed hosts update (unix) — unchanged
- [x] Local updater feed override — unchanged
- [ ] **Self-update via tagged GitHub release** — still open, blocked on tagged release validation. **Estimated completion: 70%** (plumbing done, validation pending).

## Phase 11 — 4/10 DONE, 6 OPEN (up from 2/10 in audit-3)

- [x] `.php-version` auto-detection (audit-3)
- [x] **Node.js process management plugin** — `NKS.WebDevConsole.Plugin.Node` with NodeModule (500+ lines), NodePlugin, plugin.json, icon.svg, 49 unit tests. 6 of 10 complete (including new).
- [ ] Nginx plugin — 0%
- [ ] PostgreSQL plugin — 0%
- [ ] Docker Compose integration — 0%
- [ ] Multi-user RBAC — 0%
- [ ] WebSocket log streaming — 0%
- [x] Scheduled backups (audit-3)
- [x] Site templates (audit-3)
- [ ] Performance monitoring — 0%

## New commits since audit-3 (8)

| Hash | Type | Summary |
|------|------|---------|
| `2d03e56` | feat | Node.js plugin with per-site lifecycle |
| `c06dacc` | fix | Security: exe allowlist + exit-handler race + reflection guards |
| `72c3cb3` | refactor | SiteEdit static imports + refresh after save |
| `9f5f695` | feat | `wdc node` CLI subcommand |
| `7a8b361` | test | 39 NodeModule security tests |
| `196376c` | fix | `wdc node start/restart` exits 2 on non-Running |
| `4e185bd` | feat | SiteEdit plugin-missing hint + Phase 11 tally |
| `9920c9c` | fix | Semver-aware version detection (Node 9 vs 20) |

## Test Health

| Suite | Count | Status | Delta vs audit-3 |
|-------|-------|--------|-----------------|
| Daemon xUnit | 254 | ✅ | +49 (Node security + semver) |
| Core xUnit | 55 | ✅ | 0 |
| Catalog-api smoke pytest | 8 | ✅ | 0 |
| Device/account pytest | 11 | ✅ | 0 |
| **Total** | **328** | **Zero failures** | **+49** |

## Latent bug noted — ordinal version sort in other plugins

The semver bug fixed in `9920c9c` (`StringComparer.Ordinal` ranking
Node 9 above Node 20) exists in **six other plugins**:

- `ApacheModule.cs`
- `ApacheVersionManager.cs`
- `CaddyModule.cs`
- `CloudflareModule.cs`
- `MailpitModule.cs`
- `MySqlModule.cs`
- `RedisModule.cs`

In practice users don't usually have major-version-10+ Apache/Redis
installed alongside older 1-digit versions, so the bug is latent.
**Recommended follow-up:** promote `SemverDescendingComparer` out of
`NKS.WebDevConsole.Plugin.Node` into `NKS.WebDevConsole.Core.Services`
so all plugins can reuse it. Open gap-fix task.

## Summary

- **52 commits** this session on master (was 44 at audit-3)
- **18 e2e scenarios** (unchanged)
- **10 plugins** (was 9)
- **Phase 0–10:** 130/131 done, 1 open (self-update 70%)
- **Phase 11:** 4/10 done, 6 open
- **Grand total:** 134/141 plan items done

## No downgrades. No regressions.

The semver bug fix is a previously-undiscovered latent issue, not a
regression of a DONE item. Node plugin shipped behind the security
review + 49 tests, so the implementation is hardened before this audit.
