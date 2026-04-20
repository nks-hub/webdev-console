#!/usr/bin/env node
/*
 * F91 end-to-end regression probe — verifies that disabling a plugin
 * removes its contribution from the /api/plugins/ui aggregator (and by
 * extension the sidebar Tools section) and re-enabling restores it.
 *
 * Prereqs:
 *   - WDC launched with --remote-debugging-port=9222
 *   - Playwright installed (reuses the same resolution as smoke-e2e.mjs)
 *
 * Exits 0 on pass, 1 on either "still present after disable" or
 * "missing after re-enable". Always attempts to re-enable the plugin
 * so running this probe does not leave the daemon in a disabled state.
 */
// Resolve playwright from src/frontend/node_modules (see smoke-e2e.mjs).
import { createRequire } from 'node:module'
import { fileURLToPath } from 'node:url'
import { dirname, resolve as pathResolve } from 'node:path'
const __dirname = dirname(fileURLToPath(import.meta.url))
const frontendPkg = pathResolve(__dirname, '..', 'src', 'frontend', 'package.json')
const require = createRequire(frontendPkg)
const { chromium } = require('playwright')

const CDP_URL = process.env.WDC_CDP_URL ?? 'http://127.0.0.1:9222'
const PLUGIN_ID = process.env.WDC_F91_PROBE_PLUGIN ?? 'nks.wdc.composer'

const browser = await chromium.connectOverCDP(CDP_URL)
let failed = false
try {
  const ctx = browser.contexts()[0]
  const page = ctx.pages()[0]

  const api = async (path, opts = {}) => page.evaluate(async ({ path, opts }) => {
    const { daemonApi } = /** @type {any} */ (window)
    const r = await fetch(`http://127.0.0.1:${daemonApi.getPort()}${path}`, {
      headers: { Authorization: `Bearer ${daemonApi.getToken()}`, 'content-type': 'application/json' },
      ...opts,
    })
    return {
      status: r.status,
      body: r.ok ? await r.json().catch(() => null) : null,
    }
  }, { path, opts })

  const before = await api('/api/plugins/ui')
  const present = before.body?.entries?.some(e => e.pluginId === PLUGIN_ID)
  console.log(`${PLUGIN_ID} pre-disable: ${present ? 'PRESENT' : 'ABSENT'}`)
  if (!present) {
    console.log('plugin not present to begin with — skipping probe')
    process.exit(0)
  }

  await api(`/api/plugins/${PLUGIN_ID}/disable`, { method: 'POST' })
  const after = await api('/api/plugins/ui')
  const stillThere = after.body?.entries?.some(e => e.pluginId === PLUGIN_ID)
  console.log(`${PLUGIN_ID} post-disable: ${stillThere ? '✗ STILL PRESENT' : '✓ REMOVED'}`)
  if (stillThere) failed = true

  await api(`/api/plugins/${PLUGIN_ID}/enable`, { method: 'POST' })
  const final = await api('/api/plugins/ui')
  const restored = final.body?.entries?.some(e => e.pluginId === PLUGIN_ID)
  console.log(`${PLUGIN_ID} post-reenable: ${restored ? '✓ RESTORED' : '✗ MISSING'}`)
  if (!restored) failed = true
} finally {
  await browser.close()
}
if (failed) process.exit(1)
