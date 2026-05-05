import fs from 'node:fs/promises'
import path from 'node:path'
import playwright from '../src/frontend/node_modules/playwright/index.js'

const { chromium } = playwright

const root = path.resolve(new URL('..', import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1'))
const stamp = new Date().toISOString().replace(/[:.]/g, '-')
const outDir = path.join(root, 'output', 'playwright', `simple-easy-audit-${stamp}`)
await fs.mkdir(outDir, { recursive: true })

const routes = (process.env.AUDIT_ROUTES ?? '/sites,/settings')
  .split(',')
  .map(x => x.trim())
  .filter(Boolean)
const viewportsAll = [
  { name: 'desktop', width: 1440, height: 960 },
  { name: 'tablet', width: 768, height: 900 },
  { name: 'mobile', width: 390, height: 844 },
]
const viewportFilter = new Set((process.env.AUDIT_VIEWPORTS ?? '')
  .split(',')
  .map(x => x.trim())
  .filter(Boolean))
const viewports = viewportFilter.size > 0
  ? viewportsAll.filter(v => viewportFilter.has(v.name))
  : viewportsAll
const themes = (process.env.AUDIT_THEMES ?? 'dark,light')
  .split(',')
  .map(x => x.trim())
  .filter(Boolean)

const report = {
  startedAt: new Date().toISOString(),
  outDir,
  screens: [],
  console: [],
  pageErrors: [],
  badResponses: [],
}

function safeName(input) {
  return input.replace(/^\/+/, '').replace(/[^\w.-]+/g, '_') || 'root'
}

async function currentMainPage(browser) {
  const contexts = browser.contexts()
  for (const context of contexts) {
    const pages = context.pages()
    const appPage = pages.find(p => p.url().includes('127.0.0.1:5190') || p.url().includes('localhost:5190'))
    if (appPage) return appPage
    if (pages[0]) return pages[0]
  }
  throw new Error('No Electron page available over CDP')
}

async function waitSettled(page) {
  await page.waitForLoadState('domcontentloaded').catch(() => {})
  await page.locator('.sites-simple, .settings-page').first().waitFor({ state: 'visible', timeout: 6000 }).catch(() => {})
  await page.locator('.splash-overlay, .splash-screen, .app-loading').first().waitFor({ state: 'hidden', timeout: 6000 }).catch(() => {})
  await page.waitForTimeout(500)
}

async function setSimpleMode(page) {
  const mode = process.env.AUDIT_UI_MODE ?? 'simple'
  await page.evaluate((mode) => localStorage.setItem('wdc-ui-mode', mode), mode)
}

async function setTheme(page, theme) {
  await page.evaluate((theme) => {
    localStorage.setItem('nks-wdc-theme', theme)
    document.documentElement.classList.toggle('dark', theme === 'dark')
  }, theme)
}

async function screenshot(page, name) {
  const file = path.join(outDir, `${name}.png`)
  await page.screenshot({ path: file, fullPage: true, timeout: 15000 })
  return path.relative(root, file).replaceAll('\\', '/')
}

async function metrics(page) {
  return await page.evaluate(() => {
    const parseColor = (raw) => {
      const match = String(raw).match(/rgba?\(([^)]+)\)/)
      if (!match) return null
      const parts = match[1].split(',').map(x => Number.parseFloat(x.trim()))
      return { r: parts[0], g: parts[1], b: parts[2], a: parts[3] ?? 1 }
    }
    const luminance = (c) => {
      const vals = [c.r, c.g, c.b].map(v => {
        const s = v / 255
        return s <= 0.03928 ? s / 12.92 : Math.pow((s + 0.055) / 1.055, 2.4)
      })
      return 0.2126 * vals[0] + 0.7152 * vals[1] + 0.0722 * vals[2]
    }
    const ratio = (fg, bg) => {
      const a = luminance(fg)
      const b = luminance(bg)
      return (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05)
    }
    const backgroundFor = (el) => {
      let cur = el
      while (cur && cur !== document.documentElement) {
        const bg = parseColor(getComputedStyle(cur).backgroundColor)
        if (bg && bg.a > 0.05) return bg
        cur = cur.parentElement
      }
      return parseColor(getComputedStyle(document.body).backgroundColor) || { r: 255, g: 255, b: 255, a: 1 }
    }
    const visible = (el) => {
      const rect = el.getBoundingClientRect()
      const cs = getComputedStyle(el)
      return rect.width > 0 && rect.height > 0 && cs.visibility !== 'hidden' && cs.display !== 'none'
    }
    const textNodes = [...document.querySelectorAll('h1,h2,h3,h4,p,label,button,a,td,th,li,span,.el-tag,.hint,.form-hint,.tab-desc')]
      .filter(visible)
      .filter(el => (el.textContent || '').trim().length > 0)
      .slice(0, 500)
    const lowContrast = []
    const smallText = []
    const clipped = []
    for (const el of textNodes) {
      const rect = el.getBoundingClientRect()
      const cs = getComputedStyle(el)
      const fg = parseColor(cs.color)
      const bg = backgroundFor(el)
      const text = (el.textContent || '').trim().replace(/\s+/g, ' ').slice(0, 90)
      const fontSize = Number.parseFloat(cs.fontSize)
      if (fontSize < 11) smallText.push({ text, fontSize: Number(fontSize.toFixed(1)) })
      if (rect.right > window.innerWidth + 2 || rect.left < -2) clipped.push({ text, left: Math.round(rect.left), right: Math.round(rect.right) })
      if (fg && bg) {
        const r = ratio(fg, bg)
        const required = fontSize >= 18 || (fontSize >= 14 && Number.parseInt(cs.fontWeight, 10) >= 600) ? 3 : 4.5
        if (r < required) lowContrast.push({ text, ratio: Number(r.toFixed(2)), required })
      }
    }
    const targets = [...document.querySelectorAll('button,a,[role="button"],input,select,textarea,.el-switch,.el-checkbox,.el-radio')]
      .filter(visible)
      .slice(0, 350)
    const smallTargets = []
    for (const el of targets) {
      const rect = el.getBoundingClientRect()
      const text = (el.textContent || el.getAttribute('aria-label') || el.getAttribute('title') || el.getAttribute('placeholder') || el.tagName).trim().replace(/\s+/g, ' ').slice(0, 90)
      if (rect.width < 32 || rect.height < 32) smallTargets.push({ text, width: Math.round(rect.width), height: Math.round(rect.height) })
    }
    return {
      url: location.href,
      bodyTextLength: document.body.innerText.length,
      horizontalOverflow: document.documentElement.scrollWidth > window.innerWidth + 2,
      scrollWidth: document.documentElement.scrollWidth,
      innerWidth: window.innerWidth,
      lowContrast: lowContrast.slice(0, 40),
      smallText: smallText.slice(0, 40),
      smallTargets: smallTargets.slice(0, 40),
      clipped: clipped.slice(0, 40),
    }
  })
}

const browser = await chromium.connectOverCDP('http://127.0.0.1:9222')
const page = await currentMainPage(browser)
page.setDefaultTimeout(4000)
page.on('console', (msg) => {
  if (['error', 'warning'].includes(msg.type())) report.console.push({ type: msg.type(), text: msg.text(), url: page.url() })
})
page.on('pageerror', (err) => report.pageErrors.push({ message: err.message, stack: err.stack, url: page.url() }))
page.on('response', (res) => {
  if (res.status() >= 400) report.badResponses.push({ status: res.status(), url: res.url(), page: page.url() })
})

try {
  await setSimpleMode(page)
  for (const theme of themes) {
    await setTheme(page, theme)
    for (const vp of viewports) {
      await page.setViewportSize({ width: vp.width, height: vp.height })
      for (const route of routes) {
        await page.goto(`http://127.0.0.1:5190/?audit=${Date.now()}#${route}`, { waitUntil: 'domcontentloaded', timeout: 6000 })
        await waitSettled(page)
        const shot = await screenshot(page, `${theme}-${vp.name}-${safeName(route)}`)
        report.screens.push({ theme, viewport: vp, route, screenshot: shot, metrics: await metrics(page) })
      }
    }
  }
  const siteLinks = await page.locator('a,button,.site-card,.site-mobile-main').count().catch(() => 0)
  report.siteClickTargets = siteLinks
  report.finishedAt = new Date().toISOString()
} finally {
  await fs.writeFile(path.join(outDir, 'report.json'), JSON.stringify(report, null, 2))
  const lines = [
    '# Simple/Easy UI Audit',
    '',
    `Started: ${report.startedAt}`,
    `Finished: ${report.finishedAt ?? '(interrupted)'}`,
    `Screens: ${report.screens.length}`,
    `Console warnings/errors: ${report.console.length}`,
    `Page errors: ${report.pageErrors.length}`,
    `HTTP >=400 responses: ${report.badResponses.length}`,
    '',
    '## Screens',
    ...report.screens.map(s => `- ${s.theme} ${s.viewport.name} ${s.route}: overflow=${s.metrics.horizontalOverflow} lowContrast=${s.metrics.lowContrast.length} clipped=${s.metrics.clipped.length} smallTargets=${s.metrics.smallTargets.length} screenshot=\`${s.screenshot}\``),
  ]
  await fs.writeFile(path.join(outDir, 'summary.md'), lines.join('\n'))
  console.log(JSON.stringify({ outDir, screens: report.screens.length, console: report.console.length, pageErrors: report.pageErrors.length, badResponses: report.badResponses.length }, null, 2))
  await page.setViewportSize({ width: 1440, height: 960 }).catch(() => {})
  await setTheme(page, 'dark').catch(() => {})
  await page.goto('http://127.0.0.1:5190/#/sites', { waitUntil: 'domcontentloaded', timeout: 5000 }).catch(() => {})
  await browser.close().catch(() => {})
}
