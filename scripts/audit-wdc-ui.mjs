import fs from 'node:fs/promises'
import path from 'node:path'
import playwright from '../src/frontend/node_modules/playwright/index.js'

const { chromium } = playwright

const root = path.resolve(new URL('..', import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1'))
const stamp = new Date().toISOString().replace(/[:.]/g, '-')
const outDir = path.join(root, 'output', 'playwright', `wdc-ui-audit-${stamp}`)
await fs.mkdir(outDir, { recursive: true })

const advancedRoutes = [
  '/sites',
  '/dashboard',
  '/settings',
  '/databases',
  '/ssl',
  '/php',
  '/cloudflare',
  '/plugins/apache',
  '/plugins/php-custom',
  '/plugins/mysql',
  '/plugins/mailpit',
  '/plugins/redis',
  '/plugins',
  '/binaries',
  '/composer',
  '/hosts',
  '/backups',
  '/mcp/activity',
  '/mcp/intents',
  '/mcp/grants',
  '/mcp/kinds',
  '/help',
]

const simpleRoutes = [
  '/sites',
  '/dashboard',
  '/settings',
  '/databases',
  '/ssl',
  '/php',
  '/cloudflare',
  '/plugins/mysql',
  '/binaries',
  '/backups',
  '/help',
]

const report = {
  startedAt: new Date().toISOString(),
  outDir,
  routes: [],
  interactions: [],
  skippedInteractions: [],
  console: [],
  pageErrors: [],
  badResponses: [],
  safeApiChecks: [],
}

async function flushReport() {
  await fs.writeFile(path.join(outDir, 'report.json'), JSON.stringify(report, null, 2))
  await fs.writeFile(path.join(outDir, 'summary.md'), [
    '# WDC UI Audit',
    '',
    `Started: ${report.startedAt}`,
    `Finished: ${report.finishedAt ?? '(running or interrupted)'}`,
    `Routes audited: ${report.routes.length}`,
    `Safe interactions clicked: ${report.interactions.length}`,
    `Interactions skipped: ${report.skippedInteractions.length}`,
    `Console warnings/errors: ${report.console.length}`,
    `Page errors: ${report.pageErrors.length}`,
    `HTTP >=400 responses: ${report.badResponses.length}`,
    '',
    '## Safe API Checks',
    ...report.safeApiChecks.map(x => `- ${x.name}: ${x.ok ? 'OK' : 'FAIL'} status=${x.status ?? 'n/a'}`),
    '',
    '## Route Issues',
    ...report.routes
      .filter(r => r.issueTexts.length > 0 || (r.finalUrl && !r.finalUrl.includes(`#${r.route}`) && r.mode !== 'simple' && r.mode !== 'mobile-simple'))
      .map(r => `- ${r.mode} ${r.route} -> ${r.finalUrl}: ${r.issueTexts.join(', ') || 'redirect'}`),
    '',
    '## Safe Interactions',
    ...report.interactions.map(x => `- ${x.mode} ${x.route}: ${x.kind} "${x.name}"`),
    '',
    '## Skipped Interactions',
    ...report.skippedInteractions.slice(0, 350).map(x => `- ${x.mode} ${x.route}: ${x.kind} "${x.name}" (${x.reason})`),
    '',
  ].join('\n'))
}

function safeName(input) {
  return input.replace(/^\/+/, '').replace(/[^\w.-]+/g, '_') || 'root'
}

const blockedText = /\b(delete|drop|remove|reset|restore|factory|uninstall|wipe|revoke|clear|import|upload|download|install|browse|choose file|file|smazat|odstranit|reset|obnovit ze|továr|odinstal|vymazat|odvolat|import|nahrát|stáhnout|instal|vybrat soubor|soubor)\b/i

async function normalizedText(locator) {
  return (await locator.innerText().catch(() => '') || '')
    .replace(/\s+/g, ' ')
    .trim()
}

async function closeTransientUi(page) {
  await page.keyboard.press('Escape').catch(() => {})
  await page.waitForTimeout(180)
  const dropdownItems = page.locator('.el-dropdown-menu__item, .el-select-dropdown__item')
  const dropdownCount = await dropdownItems.count().catch(() => 0)
  if (dropdownCount > 0) {
    await page.keyboard.press('Escape').catch(() => {})
    await page.waitForTimeout(120)
  }
  const closeButtons = page.locator('.el-dialog__headerbtn, .el-drawer__close-btn, .el-message-box__headerbtn')
  const count = Math.min(await closeButtons.count().catch(() => 0), 4)
  for (let i = 0; i < count; i += 1) {
    const button = closeButtons.nth(i)
    if (await button.isVisible().catch(() => false)) {
      await button.click().catch(() => {})
      await page.waitForTimeout(180)
    }
  }
}

async function auditSafeInteractions(page, mode, route, entry) {
  const candidates = [
    { kind: 'button', locator: page.locator('button, [role="button"]') },
    { kind: 'collapse', locator: page.locator('.el-collapse-item__header') },
    { kind: 'help-nav', locator: page.locator('.help-nav-item, .help-group-header') },
  ]
  let clicked = 0
  const maxClicks = Number.parseInt(process.env.WDC_AUDIT_MAX_CLICKS ?? '8', 10)
  const seen = new Set()

  for (const group of candidates) {
    const count = Math.min(await group.locator.count().catch(() => 0), 60)
    for (let i = 0; i < count && clicked < maxClicks; i += 1) {
      const item = group.locator.nth(i)
      if (!(await item.isVisible().catch(() => false))) continue
      if (!(await item.isEnabled().catch(() => true))) continue
      const label = await normalizedText(item)
      const title = (await item.getAttribute('title').catch(() => '') || '').trim()
      const aria = (await item.getAttribute('aria-label').catch(() => '') || '').trim()
      const name = label || title || aria || `${group.kind}-${i}`
      const key = `${group.kind}:${name}`
      if (seen.has(key)) continue
      seen.add(key)

      if (blockedText.test(name)) {
        report.skippedInteractions.push({ mode, route, kind: group.kind, name: name.slice(0, 160), reason: 'blocked-text' })
        continue
      }

      const beforeUrl = page.url()
      try {
        await item.click({ timeout: 1500, noWaitAfter: true })
        await page.waitForTimeout(300)
        await closeTransientUi(page)
        const afterUrl = page.url()
        if (afterUrl !== beforeUrl && !afterUrl.includes(`#${route}`)) {
          await page.goto(beforeUrl, { waitUntil: 'domcontentloaded', timeout: 3000 }).catch(() => {})
          await waitSettled(page)
        }
        const shot = await screenshot(page, `${mode}-${safeName(route)}-click-${clicked}-${safeName(name).slice(0, 42)}`)
        report.interactions.push({ mode, route, kind: group.kind, name: name.slice(0, 160), screenshot: shot })
        entry.screenshots.push(shot)
        clicked += 1
      } catch (err) {
        report.skippedInteractions.push({ mode, route, kind: group.kind, name: name.slice(0, 160), reason: `click-failed: ${err.message}` })
      }
    }
  }
}

async function currentMainPage(browser) {
  for (const context of browser.contexts()) {
    for (const page of context.pages()) {
      if (page.url().includes('127.0.0.1:5190') || page.url().includes('localhost:5190')) return page
    }
  }
  const context = browser.contexts()[0] ?? await browser.newContext()
  return await context.newPage()
}

async function waitSettled(page) {
  await page.waitForLoadState('domcontentloaded').catch(() => {})
  await page.waitForTimeout(450)
}

async function screenshot(page, name) {
  const file = path.join(outDir, `${name}.png`)
  try {
    await page.screenshot({ path: file, fullPage: true, timeout: 15000 })
  } catch (err) {
    report.skippedInteractions.push({
      mode: 'screenshot',
      route: name,
      kind: 'screenshot',
      name,
      reason: `full-page-fallback: ${err.message}`,
    })
    await page.screenshot({ path: file, fullPage: false, timeout: 10000 })
  }
  return file
}

async function setMode(page, mode) {
  await page.goto('http://127.0.0.1:5190/#/sites', { waitUntil: 'domcontentloaded', timeout: 5000 })
  await waitSettled(page)
  await page.evaluate((value) => localStorage.setItem('wdc-ui-mode', value), mode)
  await page.reload()
  await waitSettled(page)
}

async function auditRoute(page, mode, route) {
  const entry = {
    mode,
    route,
    finalUrl: null,
    title: null,
    h1: null,
    visibleTabs: [],
    screenshots: [],
    issueTexts: [],
  }

  await page.goto(`http://127.0.0.1:5190/#${route}`, { waitUntil: 'domcontentloaded', timeout: 5000 })
  await waitSettled(page)
  entry.finalUrl = page.url()
  entry.title = await page.title().catch(() => null)
  entry.h1 = await page.locator('h1').first().textContent({ timeout: 1000 }).catch(() => null)

  const bodyText = await page.locator('body').innerText({ timeout: 2000 }).catch(() => '')
  for (const needle of ['Failed to fetch', 'Unauthorized', 'Error', 'Exception', 'NaN', 'undefined']) {
    if (bodyText.includes(needle)) entry.issueTexts.push(needle)
  }

  entry.screenshots.push(await screenshot(page, `${mode}-${safeName(route)}-page`))

  const tabs = await page.locator('[role="tab"]').all()
  for (let i = 0; i < tabs.length; i += 1) {
    const tab = tabs[i]
    if (!(await tab.isVisible().catch(() => false))) continue
    const label = (await tab.innerText().catch(() => `tab-${i}`)).trim().replace(/\s+/g, ' ')
    entry.visibleTabs.push(label)
    await tab.click().catch(() => {})
    await page.waitForTimeout(250)
    entry.screenshots.push(await screenshot(page, `${mode}-${safeName(route)}-tab-${i}-${safeName(label).slice(0, 40)}`))
  }

  await auditSafeInteractions(page, mode, route, entry)

  report.routes.push(entry)
  await flushReport()
}

async function readDaemonInfo(page) {
  return await page.evaluate(async () => {
    const port = window.daemonApi?.getPort?.()
    const token = window.daemonApi?.getToken?.()
    return { port, token }
  })
}

async function safeApiChecks(page) {
  const info = await readDaemonInfo(page)
  if (!info.port || !info.token) {
    report.safeApiChecks.push({ name: 'daemonInfo', ok: false, error: 'missing preload port/token' })
    return
  }

  const checks = [
    {
      name: 'mysql-reset-newPassword-alias-validation',
      path: '/api/plugins/mysql/reset-password',
      body: { newPassword: 'short' },
      expect: 'newPwd must be at least 8 characters',
    },
    {
      name: 'mysql-change-newPassword-alias-validation',
      path: '/api/plugins/mysql/change-password',
      body: { currentPassword: 'definitely-wrong', newPassword: 'short' },
      expect: 'newPwd must be at least 8 characters',
    },
  ]

  for (const check of checks) {
    const result = await page.evaluate(async ({ port, token, check }) => {
      const response = await fetch(`http://127.0.0.1:${port}${check.path}`, {
        method: 'POST',
        headers: {
          Authorization: `Bearer ${token}`,
          'Content-Type': 'application/json',
        },
        body: JSON.stringify(check.body),
      })
      const text = await response.text()
      return { status: response.status, text }
    }, { port: info.port, token: info.token, check })
    report.safeApiChecks.push({
      name: check.name,
      status: result.status,
      ok: result.text.includes(check.expect),
      response: result.text.slice(0, 500),
    })
  }
}

const browser = await chromium.connectOverCDP('http://127.0.0.1:9222')
const page = await currentMainPage(browser)

try {
  page.on('console', (msg) => {
    const type = msg.type()
    if (['error', 'warning'].includes(type)) {
      report.console.push({ type, text: msg.text(), url: page.url() })
    }
  })
  page.on('pageerror', (err) => report.pageErrors.push({ message: err.message, stack: err.stack, url: page.url() }))
  page.on('response', (res) => {
    const status = res.status()
    if (status >= 400) report.badResponses.push({ status, url: res.url(), page: page.url() })
  })

  await page.setViewportSize({ width: 1440, height: 960 })
  await setMode(page, 'advanced')
  for (const route of advancedRoutes) await auditRoute(page, 'advanced', route)

  await setMode(page, 'simple')
  for (const route of simpleRoutes) await auditRoute(page, 'simple', route)

  await page.setViewportSize({ width: 390, height: 844 })
  for (const route of ['/sites', '/settings', '/plugins/mysql']) {
    await auditRoute(page, 'mobile-simple', route)
  }

  await safeApiChecks(page)
  report.finishedAt = new Date().toISOString()
} finally {
  await page.setViewportSize({ width: 1440, height: 960 }).catch(() => {})
  await setMode(page, 'advanced').catch(() => {})
  await page.goto('http://127.0.0.1:5190/#/sites', { waitUntil: 'domcontentloaded', timeout: 5000 }).catch(() => {})
  await flushReport()
  console.log(JSON.stringify({ outDir, routes: report.routes.length, interactions: report.interactions.length, skippedInteractions: report.skippedInteractions.length, console: report.console.length, pageErrors: report.pageErrors.length, badResponses: report.badResponses.length }, null, 2))
  await browser.close()
}
