import { defineConfig } from 'playwright/test'

// Phase 7.7 — Playwright E2E for grants/deploy flow on blog.loc.
// Hits the live daemon directly via the request fixture (no browser
// needed for API flows). Mirrors the bash e2e-mcp-deploy.sh coverage
// in a structured TypeScript test format with proper assertions and
// a JUnit-compatible report.
//
// Daemon must be running at http://localhost:17280 with the Bearer
// token from C:\Users\LuRy\AppData\Local\Temp\nks-wdc-daemon.port.
// Tests read the token at setup time so a fresh daemon restart still
// works without code changes.
export default defineConfig({
  // Iter 40 fix — config moved from tests/playwright/playwright.config.ts
  // to src/frontend/playwright.config.ts so `npx playwright test` from
  // src/frontend/ auto-discovers it. Previously cd'ing to src/frontend
  // and running playwright meant the config was IGNORED — defaults
  // (10 workers, fullyParallel:true) silently applied, and every run
  // was at the mercy of test interleaving across 10 concurrent workers.
  // This explains the iter 33-39 chase: state-pollution defenses helped
  // but couldn't compensate for genuine parallelism that workers:1 was
  // supposed to prevent. With config now found, workers:1 actually means 1.
  testDir: './tests/playwright/tests',
  globalSetup: './tests/playwright/global-setup.ts',
  timeout: 30_000,
  expect: { timeout: 5_000 },
  fullyParallel: false, // tests share daemon state; serial avoids cross-talk
  workers: 1,
  reporter: [['list'], ['junit', { outputFile: 'tests/playwright/results/junit.xml' }]],
  use: {
    baseURL: 'http://localhost:17280',
    extraHTTPHeaders: {
      // Token resolved per-test via the auth fixture in tests/_fixtures.ts.
      // Configured here as fallback so plain `request.get(...)` calls also
      // work when a test forgets to consume the fixture.
    },
  },
})
