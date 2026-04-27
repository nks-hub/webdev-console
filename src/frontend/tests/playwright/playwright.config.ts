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
  testDir: './tests',
  // Iter 38 — global startup reset of settings that specs mutate so a
  // killed-mid-test run from a prior invocation can't poison this one.
  // Mirrors the bash startup reset pattern from iter 33-34.
  globalSetup: './global-setup.ts',
  timeout: 30_000,
  expect: { timeout: 5_000 },
  fullyParallel: false, // tests share daemon state; serial avoids cross-talk
  workers: 1,
  reporter: [['list'], ['junit', { outputFile: 'results/junit.xml' }]],
  use: {
    baseURL: 'http://localhost:17280',
    extraHTTPHeaders: {
      // Token resolved per-test via the auth fixture in tests/_fixtures.ts.
      // Configured here as fallback so plain `request.get(...)` calls also
      // work when a test forgets to consume the fixture.
    },
  },
})
