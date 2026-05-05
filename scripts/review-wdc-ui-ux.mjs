import fs from 'node:fs/promises'
import path from 'node:path'
import playwright from '../src/frontend/node_modules/playwright/index.js'

const { chromium } = playwright

const root = path.resolve(new URL('..', import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1'))
const auditDir = process.argv[2] ? path.resolve(process.argv[2]) : path.join(root, 'output', 'playwright')
const reportPath = path.join(auditDir, 'report.json')
const source = JSON.parse(await fs.readFile(reportPath, 'utf8'))

const viewports = [
  { name: 'desktop', width: 1440, height: 960 },
  { name: 'tablet', width: 768, height: 900 },
  { name: 'mobile', width: 390, height: 844 },
]

const purposeByRoute = {
  '/sites': {
    purpose: 'Primary project inventory and daily site operations.',
    usefulness: 'High. This is the main entry point for local development work.',
    redesign: 'Prioritize scan density, clear per-site status badges, and a compact action menu with routine actions first.',
  },
  '/dashboard': {
    purpose: 'Operational health overview for services, readiness, metrics, and shortcuts.',
    usefulness: 'High. It should answer whether the local stack is usable right now.',
    redesign: 'Turn it into a readiness-first cockpit: service rows, blockers, recent issues, and update/backup status before secondary charts.',
  },
  '/settings': {
    purpose: 'Global configuration for preferences, services, paths, security, account, updates, and sync.',
    usefulness: 'High but currently broad. It carries both daily setup and advanced internals.',
    redesign: 'Continue extracting a shell plus task sections. Easy mode should expose only daily setup; advanced mode should group by task and risk.',
  },
  '/databases': {
    purpose: 'Database inventory and administration.',
    usefulness: 'High for backend projects, but risky because it can mutate data.',
    redesign: 'Add engine selector and separate inspect/query/export from create/drop/import. PostgreSQL should be a peer to MySQL/MariaDB.',
  },
  '/ssl': {
    purpose: 'Local certificate authority and per-site certificate management.',
    usefulness: 'Medium-high. Critical when HTTPS/local trust breaks.',
    redesign: 'Lead with trust status and repair action; move raw certificate details into expandable rows.',
  },
  '/php': {
    purpose: 'PHP runtime version and extension management.',
    usefulness: 'Medium-high for PHP projects.',
    redesign: 'Use a version matrix with installed/default/in-use indicators and hide install internals behind details.',
  },
  '/cloudflare': {
    purpose: 'Tunnel and DNS integration.',
    usefulness: 'Medium-high for public previews.',
    redesign: 'Separate account readiness, tunnel status, and per-site exposure. Keep DNS actions visually isolated.',
  },
  '/plugins/apache': {
    purpose: 'Apache service plugin status, config, logs, and diagnostics.',
    usefulness: 'Medium. Mostly useful when the web server fails.',
    redesign: 'Make overview status actionable, then put vhosts/debug/logs into secondary task tabs.',
  },
  '/plugins/php-custom': {
    purpose: 'PHP service plugin internals.',
    usefulness: 'Medium for debugging PHP-FPM/runtime issues.',
    redesign: 'Prefer service row plus version/extension tables; keep INI/log controls dense but less visually loud.',
  },
  '/plugins/mysql': {
    purpose: 'MySQL service status, databases, password management, tuning, and logs.',
    usefulness: 'High. It supports common local DB workflows.',
    redesign: 'Separate safe daily DB status from root-password reset and tuning. Password flows need explicit validation and non-destructive checks.',
  },
  '/plugins/mailpit': {
    purpose: 'Mailpit local mail catcher status and config.',
    usefulness: 'Medium. Important for app email testing.',
    redesign: 'Keep a compact status/open/log layout; config can be a small side panel.',
  },
  '/plugins/redis': {
    purpose: 'Redis service status, config, and logs.',
    usefulness: 'Medium for cache/queue projects.',
    redesign: 'Keep as a minimal service detail surface with connection details, memory, and logs.',
  },
  '/plugins': {
    purpose: 'Plugin inventory and marketplace.',
    usefulness: 'Medium. Mostly advanced extension management.',
    redesign: 'Group by service type and readiness; marketplace install paths need clear trust and compatibility metadata.',
  },
  '/binaries': {
    purpose: 'Runtime binary catalog and installed package management.',
    usefulness: 'High for platform support and reproducibility.',
    redesign: 'Show platform support and unsupported reasons first; add PostgreSQL parity and checksum/status clarity.',
  },
  '/composer': {
    purpose: 'Composer package manager discovery and site integration.',
    usefulness: 'Medium for PHP apps.',
    redesign: 'Lead with installed Composer status and sites using composer.json; move package operations into site context where possible.',
  },
  '/hosts': {
    purpose: 'Hosts file managed block inspection and repair.',
    usefulness: 'Medium-high when local domains fail.',
    redesign: 'Make it read-first: show managed entries, drift, backup state, and repair action separately from raw editing.',
  },
  '/backups': {
    purpose: 'Backup overview, snapshots, schedule, and content selection.',
    usefulness: 'High if it covers sites and database dumps reliably.',
    redesign: 'Make backup scope and next run explicit. Separate restore/destructive flows from routine snapshot browsing.',
  },
  '/mcp/activity': {
    purpose: 'MCP activity/audit hub.',
    usefulness: 'Advanced but important for agent safety and traceability.',
    redesign: 'Keep as audit-first: filters, risk badges, session drill-down, and quiet empty states.',
  },
  '/mcp/intents': {
    purpose: 'Pending and historical destructive operation intents.',
    usefulness: 'Advanced security control.',
    redesign: 'Focus on risk, state, expiration, and revoke/approve separation.',
  },
  '/mcp/grants': {
    purpose: 'Persistent MCP trust grants.',
    usefulness: 'Advanced security configuration.',
    redesign: 'Make matching rules explainable and previewable before saving.',
  },
  '/mcp/kinds': {
    purpose: 'Catalog of registered destructive operation types.',
    usefulness: 'Advanced diagnostics and policy setup.',
    redesign: 'Keep table dense, add owner/plugin provenance and policy status.',
  },
  '/help': {
    purpose: 'In-app help and workflow documentation.',
    usefulness: 'Medium. Useful if searchable and linked to current workflows.',
    redesign: 'Keep navigation compact and make help contextual from pages; avoid turning it into a large static manual.',
  },
}

function routeInfo(route) {
  return purposeByRoute[route] ?? {
    purpose: 'Feature-specific WDC surface.',
    usefulness: 'Depends on the active workflow.',
    redesign: 'Align with shared status/action/table primitives and keep destructive controls isolated.',
  }
}

function uniqueRoutes(routes) {
  const seen = new Set()
  const result = []
  for (const r of routes) {
    const key = `${r.mode}:${r.route}`
    if (seen.has(key)) continue
    seen.add(key)
    result.push(r)
  }
  return result
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
  await page.waitForTimeout(300)
}

async function closeTransientUi(page) {
  await page.keyboard.press('Escape').catch(() => {})
  await page.waitForTimeout(120)
}

async function setMode(page, mode) {
  await page.goto('http://127.0.0.1:5190/#/sites', { waitUntil: 'domcontentloaded', timeout: 5000 })
  await waitSettled(page)
  await page.evaluate((value) => localStorage.setItem('wdc-ui-mode', value), mode.includes('simple') ? 'simple' : 'advanced')
  await page.reload()
  await waitSettled(page)
}

function rel(file) {
  return path.relative(root, file).replaceAll('\\', '/')
}

function score(metrics) {
  let value = 100
  if (metrics.horizontalOverflow) value -= 20
  value -= Math.min(metrics.lowContrast.length * 3, 24)
  value -= Math.min(metrics.smallText.length * 2, 14)
  value -= Math.min(metrics.smallTargets.length * 2, 14)
  value -= Math.min(metrics.clipped.length * 2, 12)
  return Math.max(0, value)
}

function grade(value) {
  if (value >= 90) return 'A'
  if (value >= 80) return 'B'
  if (value >= 70) return 'C'
  if (value >= 60) return 'D'
  return 'F'
}

const browser = await chromium.connectOverCDP('http://127.0.0.1:9222')
const page = await currentMainPage(browser)
page.setDefaultTimeout(3000)
page.setDefaultNavigationTimeout(5000)
const screenReviews = []

try {
  for (const routeEntry of uniqueRoutes(source.routes)) {
    const checks = []
    for (const vp of viewports) {
      await page.setViewportSize({ width: vp.width, height: vp.height })
    await setMode(page, routeEntry.mode)
    await page.goto(`http://127.0.0.1:5190/#${routeEntry.route}`, { waitUntil: 'domcontentloaded', timeout: 5000 })
    await waitSettled(page)
    await closeTransientUi(page)
    const metrics = await page.evaluate(() => {
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
      const textNodes = [...document.querySelectorAll('h1,h2,h3,h4,p,label,button,a,td,th,li,span,.el-tag,.hint,.muted')]
        .filter(visible)
        .filter(el => (el.textContent || '').trim().length > 0)
        .slice(0, 450)
      const lowContrast = []
      const smallText = []
      for (const el of textNodes) {
        const cs = getComputedStyle(el)
        const fg = parseColor(cs.color)
        const bg = backgroundFor(el)
        const text = (el.textContent || '').trim().replace(/\s+/g, ' ').slice(0, 80)
        const fontSize = Number.parseFloat(cs.fontSize)
        if (fontSize < 11) smallText.push({ text, fontSize })
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
        const text = (el.textContent || el.getAttribute('aria-label') || el.getAttribute('title') || el.getAttribute('placeholder') || el.tagName).trim().replace(/\s+/g, ' ').slice(0, 80)
        if (rect.width < 32 || rect.height < 32) smallTargets.push({ text, width: Math.round(rect.width), height: Math.round(rect.height) })
      }
      const clipped = []
      for (const el of textNodes) {
        const rect = el.getBoundingClientRect()
        if (rect.right > window.innerWidth + 2 || rect.left < -2) {
          const text = (el.textContent || '').trim().replace(/\s+/g, ' ').slice(0, 80)
          clipped.push({ text, left: Math.round(rect.left), right: Math.round(rect.right) })
        }
      }
      return {
        url: location.href,
        title: document.title,
        viewport: { width: window.innerWidth, height: window.innerHeight },
        bodyTextLength: document.body.innerText.length,
        scrollHeight: document.documentElement.scrollHeight,
        horizontalOverflow: document.documentElement.scrollWidth > window.innerWidth + 2,
        lowContrast: lowContrast.slice(0, 12),
        smallText: smallText.slice(0, 12),
        smallTargets: smallTargets.slice(0, 12),
        clipped: clipped.slice(0, 12),
      }
    })
      checks.push({ viewport: vp.name, metrics, score: score(metrics), grade: grade(score(metrics)) })
    }
    const overall = Math.round(checks.reduce((n, x) => n + x.score, 0) / checks.length)
    screenReviews.push({
      mode: routeEntry.mode,
      route: routeEntry.route,
      title: routeEntry.title,
      h1: routeEntry.h1,
      screenshots: routeEntry.screenshots.map(rel),
      tabs: routeEntry.visibleTabs,
      info: routeInfo(routeEntry.route),
      checks,
      overall,
      grade: grade(overall),
    })
  }
} finally {
  await page.setViewportSize({ width: 1440, height: 960 }).catch(() => {})
  await setMode(page, 'advanced').catch(() => {})
  await page.goto('http://127.0.0.1:5190/#/sites', { waitUntil: 'domcontentloaded', timeout: 5000 }).catch(() => {})
  await browser.close()
}

function findingsFor(review) {
  const findings = []
  if (review.checks.some(c => c.metrics.horizontalOverflow)) findings.push('Horizontal overflow appears on at least one viewport.')
  if (review.checks.some(c => c.metrics.lowContrast.length > 0)) findings.push('Some text may not meet contrast targets.')
  if (review.checks.some(c => c.metrics.smallTargets.length > 0)) findings.push('Some interactive targets are under 32px in one or more viewports.')
  if (review.checks.some(c => c.metrics.smallText.length > 0)) findings.push('Some text renders below 11px.')
  if (review.checks.some(c => c.metrics.clipped.length > 0)) findings.push('Some visible text extends outside the viewport.')
  if (findings.length === 0) findings.push('No automated layout, contrast, or tap-target blocker found.')
  return findings
}

const lines = [
  '# WDC Screen-by-Screen UI/UX Review',
  '',
  `Source audit: \`${rel(reportPath)}\``,
  `Screens reviewed: ${screenReviews.length}`,
  '',
  '## Method',
  '',
  '- Uses the latest deep Playwright run with screenshots and safe physical clicks.',
  '- Re-checks every route at desktop 1440px, tablet 768px, and mobile 390px.',
  '- Measures horizontal overflow, text contrast, small text, small interactive targets, and clipped text.',
  '- Blocks destructive data operations such as delete, drop, reset, restore, wipe, uninstall, and file import/upload.',
  '',
  '## Summary Table',
  '',
  '| Screen | Mode | Grade | Purpose | Primary redesign direction |',
  '|---|---:|---:|---|---|',
  ...screenReviews.map(r => `| \`${r.route}\` | ${r.mode} | ${r.grade} (${r.overall}) | ${r.info.purpose} | ${r.info.redesign} |`),
  '',
  '## Screen Reviews',
  '',
]

for (const r of screenReviews) {
  lines.push(`### ${r.mode} ${r.route}`)
  lines.push('')
  lines.push(`- Title/H1: ${r.title || '(no title)'} / ${r.h1 || '(no h1)'}`)
  lines.push(`- Purpose: ${r.info.purpose}`)
  lines.push(`- Usefulness: ${r.info.usefulness}`)
  lines.push(`- Overall UI/accessibility score: ${r.grade} (${r.overall}/100)`)
  lines.push(`- Tabs/sections captured: ${r.tabs.length ? r.tabs.join(', ') : 'none detected'}`)
  lines.push(`- Main screenshot: \`${r.screenshots[0] || '(none)'}\``)
  lines.push(`- Screenshots captured for this screen: ${r.screenshots.length}`)
  lines.push('- Automated findings:')
  for (const f of findingsFor(r)) lines.push(`  - ${f}`)
  lines.push('- Viewport notes:')
  for (const c of r.checks) {
    const m = c.metrics
    lines.push(`  - ${c.viewport}: ${c.grade} (${c.score}/100), overflow=${m.horizontalOverflow ? 'yes' : 'no'}, lowContrast=${m.lowContrast.length}, smallTargets=${m.smallTargets.length}, smallText=${m.smallText.length}, clipped=${m.clipped.length}`)
    if (m.lowContrast[0]) lines.push(`    - Contrast sample: "${m.lowContrast[0].text}" ratio ${m.lowContrast[0].ratio}`)
    if (m.smallTargets[0]) lines.push(`    - Tap target sample: "${m.smallTargets[0].text}" ${m.smallTargets[0].width}x${m.smallTargets[0].height}`)
  }
  lines.push(`- UI/UX redesign/refactor recommendation: ${r.info.redesign}`)
  lines.push('')
}

const out = path.join(auditDir, 'screen-ui-ux-review.md')
await fs.writeFile(out, lines.join('\n'))
await fs.writeFile(path.join(auditDir, 'screen-ui-ux-review.json'), JSON.stringify(screenReviews, null, 2))
console.log(JSON.stringify({ out, screens: screenReviews.length }, null, 2))
