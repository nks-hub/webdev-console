# `scripts/e2e/` — End-to-end scenario harness

Numbered scenarios under `scenarios/` exercise the daemon's HTTP surface as a
real WebDev Console client would. They are the primary regression net for
**any feature that crosses API → daemon → filesystem boundaries** (vhost
rendering, site lifecycle, deploy, snapshots, …).

## Running a single scenario

```bash
node scripts/e2e/run-scenario.mjs scripts/e2e/scenarios/19-bind-ip-vhost.mjs
```

The runner imports the scenario's default export, wires up a tiny `ctx`
shim (collects cleanup callbacks, runs them on success and failure), then
prints `[pass] <name> in NNms` or `[fail] <name>: <error>` and exits with
the matching status code.

## Prerequisites

- **Daemon running** on `127.0.0.1:17280` with the dev API token
  configured. Without it, scenarios fail with `Unauthorized`.
  The harness reads the token from `~/.wdc/dev-token.txt` (or whatever
  `harness.mjs#daemonAuthHeaders` is wired to).

- **Hosts file write**. Scenarios that POST `/api/sites` trigger a hosts
  file entry. On Windows this requires the daemon to have run elevated
  (or the helper to be in the operator's elevation cache). If you're
  iterating outside an elevated shell, export
  `NKS_WDC_SKIP_HOSTS_UAC=1` before starting the daemon — site creation
  then skips the hosts mutation. The `<VirtualHost ...>` assertions in
  scenarios 19–21 still pass because they read the rendered vhost file
  under `~/.wdc/generated/`, not the host's name resolution.

## Conventions

- **ID-prefixed filenames** keep alphabetical order = execution order
  when a future suite runner picks the directory wholesale.
- **No live external dependency** beyond the daemon. Scenarios use
  `tmpDir(...)` for document roots and register cleanups so a failed
  run doesn't leave orphaned `~/.wdc/sites/*.toml`.
- **Idempotent**: every scenario calls `DELETE /api/sites/{domain}` for
  its fixture both at the start (swallowing 404) and via `ctx.cleanup`.
- **`P0` / `P1` / `P2`** priority tag on `scenario(...)` is informational
  today; reserved for the canonical runner to filter smoke vs. extended
  runs.

## Adding a new scenario

1. Pick the next free numeric prefix.
2. `import { scenario, api, assert, tmpDir, rmTree, writeFile, wdcDataDir } from '../harness.mjs'`.
3. Default-export `scenario('<id>', '<name>', '<priority>', async (ctx) => { ... })`.
4. Register every cleanup via `ctx.cleanup(...)` before the assertion that
   would skip the rest of the function. Failures must not leak fixtures.
5. Smoke-load it: `node -e "import('./scripts/e2e/scenarios/NN-….mjs').then(m=>console.log(m.default?.id))"`.
6. Run it for real once the daemon is up.

## Current coverage

| # | Scenario | Surface exercised |
|---|---|---|
| 01 | WordPress stack | end-to-end WP site lifecycle |
| 02 | Laravel stack | framework auto-detect + composer hint |
| 03 | Static HTML | minimal docroot-only site |
| 04 | PHP switch | swap PHP version on existing site |
| 05 | Database round-trip | create db, list, drop |
| 06 | Config history | TOML history / rollback |
| 07 | SSL regen | mkcert dev cert regenerate |
| 08 | Plugin toggle | enable/disable plugin |
| 09 | Backup round-trip | full backup + restore |
| 10 | Caddy service | non-Apache server lifecycle |
| 11 | Wildcard alias | `*.local.loc` ServerAlias handling |
| 12 | Services list | `/api/services` shape |
| 13 | Crash recovery | daemon restart preserves state |
| 14 | Log endpoint | `/api/sites/{d}/access-log` streaming |
| 15 | Config validate | invalid input rejection paths |
| 16 | Stop stays stopped | restart-on-boot=false honored |
| 17 | Cloudflare config | tunnel pairing flow |
| 18 | Node proxy | reverse proxy to upstream port |
| 19 | Bind IP vhost | wildcard, 127.0.0.1, round-trip, options endpoint |
| 20 | IPv6 bind vhost | `::1` rendering with `[…]` wrap, bracket normalization |
| 21 | Multi-IP bind vhost | dual-stack loopback ordering + mixed-scope rejection |
| 22 | Bind NIC warning | `{site, warnings}` wrapper for bogus IP (RFC5737), surfaces "not assigned" message |
| 23 | Bind × SSL combo | HTTP + HTTPS VirtualHost pair per explicit bind + wildcard fall-through |
| 24 | Fresh-install first-site (P0) | minimum-viable POST without bindAddresses → defaults to `["*"]` + vhost present |
| 25 | Bind × SSL edge cases | multi-IP+SSL, flip-back to wildcard, bogus IP + SSL still wraps |
| 26 | Bind × custom port | wildcard / explicit + custom httpPort/httpsPort round-trip |
| 27 | Bind × ServerAlias | aliases land inside the IP-bound VirtualHost block, not wildcard |
| 28 | IPv6 link-local | `fe80::*` bracket wrap + round-trip |
| 29 | Localhost auto-loopback | `Domain="localhost" + bindAddresses=["127.0.0.1"]` → daemon auto-appends |
| 30 | Bind API rejection (P1) | mixed wildcard+specific / shell-injection / garbage / oversize → 4xx |
| 31 | Bind survives toggle | PATCH /enabled cycle preserves bindAddresses + vhost re-renders correctly |
| 32 | Bind persists across reload | TOML serializer uses array form; `POST /api/admin/reload-sites` round-trips |
| 33 | Bind delete-recreate cycle | DELETE prunes vhost; recreate with different bind has no stale blocks |

## Bind-IP coverage matrix (scenarios 19–33)

Every dimension of vhost generation across bind addresses is regression-locked. Total bind-IP related assertions across the test suite:

- **15 bash e2e scenarios** (this directory)
- **37 unit tests** in `tests/NKS.WebDevConsole.Daemon.Tests/BindAddressNormalizationTests.cs`
- **8 Playwright API specs** in `src/frontend/tests/playwright/tests/bind-ip-api.spec.ts` + `warnings-wrapper-contract.spec.ts`
- **8 source-level cross-stack contracts** in `src/frontend/tests/playwright/tests/warnings-wrapper-contract.spec.ts`

For a new bind-IP scenario, follow these patterns:
1. Use `readApacheVhost(domain)` to read live vhost from `sites-enabled/` (not `generated/` history)
2. Pick RFC test-net IPs for stable cross-host bogus-IP cases: `198.51.100.x`, `192.0.2.x`, `203.0.113.x`, `2001:db8::*`
3. Always `await api.delete(...)` in `ctx.cleanup` so a failed run doesn't leave fixtures behind
4. Use `api.patch(...)` for PATCH endpoints (added in commit 7c9a76d)
