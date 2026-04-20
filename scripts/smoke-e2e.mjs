#!/usr/bin/env node
/*
 * NKS WDC end-to-end smoke test.
 *
 * Connects to a running WDC instance via Chrome DevTools Protocol and
 * walks the UI surfaces most likely to regress across versions: the
 * Settings → About tab (F83 SSO sign-in button + displayed version),
 * the Advanced tab (F95 plugin auto-sync toggle + Sync-now action),
 * and the sidebar (F91 plugin-contributed nav entries).
 *
 * Prereqs:
 *   - Install playwright once: `npm i -D playwright` in src/frontend
 *     (or reuse the @playwright/test dependency from package.json).
 *   - Launch WDC with the remote-debugging port open:
 *         "NKS WebDev Console.exe" --remote-debugging-port=9222
 *     (dev builds already bind 9222 unconditionally; packaged builds
 *     require the explicit flag because production strips the switch.)
 *   - Token + port are read from window.daemonApi via preload — no
 *     extra env wiring needed.
 *
 * Exit code: 0 when every probe passes, 1 when at least one fails.
 * Individual rows are printed so a CI log shows exactly which surface
 * broke.
 */
// Resolve `playwright` from src/frontend/node_modules even when this file
// lives at repo-root — npm run smoke:e2e runs with cwd=src/frontend so a
// bare `import 'playwright'` would walk up from scripts/ and never find
// the module (node_modules/ is only installed in src/frontend).
// createRequire bypasses ESM's module-location-based resolver.
import { createRequire } from 'node:module'
import { fileURLToPath } from 'node:url'
import { dirname, resolve as pathResolve } from 'node:path'
const __dirname = dirname(fileURLToPath(import.meta.url))
const frontendPkg = pathResolve(__dirname, '..', 'src', 'frontend', 'package.json')
const require = createRequire(frontendPkg)
const { chromium } = require('playwright')

const CDP_URL = process.env.WDC_CDP_URL ?? 'http://127.0.0.1:9222'
const results = []

function record(name, ok, detail) {
  results.push({ name, ok, detail })
  console.log(`${ok ? '✓' : '✗'} ${name}${detail ? ' — ' + detail : ''}`)
}

const browser = await chromium.connectOverCDP(CDP_URL)
try {
  const ctx = browser.contexts()[0]
  const page = ctx.pages()[0]
  const base = page.url().split('#')[0]

  // F83 — About tab carries the app version + the SSO sign-in entry point.
  await page.goto(base + '#/settings')
  await page.waitForTimeout(1200)
  await page.locator('#tab-about, [role="tab"]:has-text("About")').first().click()
  await page.waitForTimeout(400)

  const versionText = (await page.locator('.about-version').first().textContent() || '').trim()
  record('F83: about version is set', /^v\d+\.\d+\.\d+$/.test(versionText), versionText)

  // The SSO button is conditional on useAuthStore.isAuthenticated → the
  // signed-out variant exposes "Sign in with SSO"; the signed-in variant
  // exposes "Sign out". Either is a valid pass.
  const signedIn = await page.locator('button:has-text("Sign out")').count()
  const signedOut = await page.locator('button:has-text("Sign in with SSO")').count()
  record('F83: SSO control present in About', signedIn + signedOut > 0,
    `signedIn=${signedIn}, signedOut=${signedOut}`)

  // F95 — Advanced tab owns the plugin auto-sync surface.
  await page.locator('#tab-advanced, [role="tab"]:has-text("Advanced")').first().click().catch(() => null)
  await page.waitForTimeout(400)
  const autoSyncLabel = await page.getByText(/Plugin auto-sync/i).count()
  const syncNowBtn = await page.locator('button:has-text("Sync now")').count()
  record('F95: auto-sync toggle visible', autoSyncLabel > 0)
  record('F95: Sync now button visible', syncNowBtn > 0)

  // F91 — sidebar Tools section pulled from pluginsStore.toolsNavEntries.
  await page.goto(base + '#/dashboard')
  await page.waitForTimeout(800)
  const navTexts = (await page.locator('.sidebar .nav-item .nav-label').allTextContents())
    .map(s => s.trim().toLowerCase())
  const expectTools = ['composer', 'hosts', 'ssl', 'cloudflare']
  const missingTools = expectTools.filter(t => !navTexts.some(n => n.includes(t)))
  record('F91: sidebar has plugin-contributed tools', missingTools.length === 0,
    missingTools.length ? `missing: ${missingTools.join(',')}` : `nav=${navTexts.join(',')}`)

  // REST surface check — reuse the renderer auth token so we do not need
  // to parse the port file from disk.
  const rest = await page.evaluate(async () => {
    const { daemonApi } = /** @type {any} */ (window)
    const p = daemonApi?.getPort?.()
    const t = daemonApi?.getToken?.()
    if (!p || !t) return { err: 'no daemonApi' }
    const h = { 'Authorization': `Bearer ${t}` }
    const catalog = await fetch(`http://127.0.0.1:${p}/api/plugins/catalog`, { headers: h })
    const ui      = await fetch(`http://127.0.0.1:${p}/api/plugins/ui`, { headers: h })
    return {
      catalog: catalog.ok ? await catalog.json() : { status: catalog.status },
      ui:      ui.ok      ? await ui.json()      : { status: ui.status },
    }
  })
  record('F95: /api/plugins/catalog reachable', Array.isArray(rest.catalog?.plugins),
    `count=${rest.catalog?.count}`)
  record('F91: /api/plugins/ui aggregator', Array.isArray(rest.ui?.entries),
    `entries=${rest.ui?.entries?.length}`)
} finally {
  await browser.close()
}

const failed = results.filter(r => !r.ok)
console.log(`\nResults: ${results.length - failed.length}/${results.length} passed`)
if (failed.length > 0) {
  console.log('Failed:')
  for (const f of failed) console.log(' -', f.name, f.detail || '')
  process.exit(1)
}
