# Playwright test infrastructure

API-level tests that hit the live daemon directly via the request fixture (no browser needed). Mirrors the bash `tools/e2e-mcp-deploy.sh` coverage in TypeScript.

## Quick start

```bash
cd src/frontend
npm test           # → playwright test, 38/38 pass against running daemon
npm run test:e2e   # → bash e2e suite (tools/e2e-mcp-deploy.sh)
```

The daemon must be running. Token is read from `%TEMP%/nks-wdc-daemon.port`.

## Critical invariants (do not undo)

### 1. Config lives at `src/frontend/playwright.config.ts` — NOT in this directory

Playwright auto-discovers `playwright.config.{ts,js}` by searching **upward** from the cwd, not into nested test dirs. A config at `tests/playwright/playwright.config.ts` is **silently ignored** when running `npx playwright test` from `src/frontend/`.

If you move the config or split it, verify the effective config first:
```bash
npx playwright test --reporter=list 2>&1 | head -1
# Expected: "Running 38 tests using 1 worker"
# If it says "10 workers" the config isn't being found.
```

This was the root cause of a 10-iter flake hunt — see `bugfix_playwright_config_misplaced.md` in operator memory.

### 2. `workers: 1` + `fullyParallel: false` is load-bearing

Tests share daemon state via SQLite. Some tests mutate global settings (`deploy.enabled`, `mcp.always_confirm_kinds`, `deploy.useLegacyHostHandlers`) for assertion. Concurrent execution causes state interleaving → stochastic test failures.

### 3. `globalSetup` resets settings baseline at suite start

`tests/playwright/global-setup.ts` runs once before all specs and resets 3 settings to default-safe values. This heals state pollution from a previous run that was killed mid-test (Ctrl-C, timeout, daemon restart).

If you add a setting that tests mutate, add it to `global-setup.ts`'s `baseline` object too.

### 4. Settings-mutating tests need 3-layer defense

Per the iter 33-39 hardening pattern:
- **Per-test `try/finally`** restores the captured `original` value (line of defense 1)
- **`test.afterEach`** writes the safe default regardless of how the test exited (defense 2)
- **`globalSetup`** runs at suite start (defense 3)

This pattern is mandatory for any test that touches settings. Example in `deploy-toggle.spec.ts`.

### 5. SSE stream tests use `fetch + AbortController`

Playwright's request fixture doesn't expose chunked body reading, so SSE assertions use the Web Fetch API directly with an `AbortController`. See `settings-sse.spec.ts` for the pattern. Pre-sleep 250ms before triggering the broadcast so the stream subscription is established first.

### 6. Daemon rebuild workflow

After C# changes land (server-side endpoint changes, etc.), use:
```bash
bash ../../tools/dev-daemon-rebuild.sh
```
Automates: shutdown → build → respawn → health check (~26s total). See `milestone_iter26_rebuild_helper.md` in operator memory.

## Layout

- `tests/` — spec files (each `.spec.ts` matches one feature surface)
- `_fixtures.ts` — shared `authedRequest` + `daemonAuth` fixtures
- `global-setup.ts` — startup baseline reset (iter 38)
- `results/` — JUnit reporter output (gitignored)

## Adding a new test

1. Create `tests/<feature>.spec.ts`
2. Import from `./_fixtures` (`authedRequest` is the bearer-authed request fixture)
3. If your test mutates settings, follow the 3-layer defense pattern above
4. Run `npm test` from `src/frontend/` to verify
