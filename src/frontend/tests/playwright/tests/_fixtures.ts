import { test as base, expect } from 'playwright/test'
import { readFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

// Phase 7.7 — Auth fixture. Reads the Bearer token the daemon writes
// to its port file on startup (fresh value after every restart). Tests
// consume this fixture to get an APIRequestContext pre-loaded with the
// Authorization header — equivalent to the `api` helper in the bash
// e2e but with TypeScript types.
function readDaemonToken(): { port: number; token: string } {
  const portFile = join(tmpdir(), 'nks-wdc-daemon.port')
  const raw = readFileSync(portFile, 'utf8').trim()
  const lines = raw.split(/\r?\n/)
  if (lines.length < 2) {
    throw new Error(`port file ${portFile} malformed: expected port + token on two lines`)
  }
  const port = parseInt(lines[0], 10)
  const token = lines[1].trim()
  if (!port || !token) throw new Error('port or token empty')
  return { port, token }
}

export const test = base.extend<{
  daemonAuth: { port: number; token: string }
  authedRequest: import('playwright/test').APIRequestContext
}>({
  daemonAuth: async ({}, use) => {
    await use(readDaemonToken())
  },
  authedRequest: async ({ playwright, daemonAuth }, use) => {
    const ctx = await playwright.request.newContext({
      baseURL: `http://localhost:${daemonAuth.port}`,
      extraHTTPHeaders: { Authorization: `Bearer ${daemonAuth.token}` },
    })
    await use(ctx)
    await ctx.dispose()
  },
})

export { expect }
