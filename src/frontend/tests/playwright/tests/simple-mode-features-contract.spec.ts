import { test, expect } from './_fixtures'
import { readFileSync } from 'node:fs'
import { join, resolve } from 'node:path'

// Source-level contracts for the remaining simple-mode features this
// session added. Each one was a redesign that closed a UX gap reported
// by the operator:
//
//   - Dashboard recent-sites widget — was "prehled je prilis strohy"
//   - Settings Simple Network panel — read-only port snapshot
//   - Settings Simple Backup panel — one-click manual backup
//   - SiteDetailSimple document root edit — was "neni tam zadna editace"
//   - No-PHP empty state in Sites simple create dialog
//
// Without contracts here a future cleanup could silently drop the
// gap-closing UX after a refactor.

const srcRoot = resolve(process.cwd(), 'src')

test.describe('Simple-mode features contract', () => {
  test('Dashboard recent-sites widget wired in DashboardSimple', () => {
    const source = readFileSync(join(srcRoot, 'components', 'pages', 'DashboardSimple.vue'), 'utf-8')
    expect(source).toMatch(/recentSimpleSites/)
    expect(source).toMatch(/simple-recent-list/)
    expect(source).toMatch(/dashboard\.simple\.recent\.title/)
    expect(source).toMatch(/dashboard\.simple\.recent\.viewAll/)
    // The list should cap at 5 — anything more would make the dashboard
    // feel cluttered. Locked here so a future refactor doesn't show
    // the full sites table on the simple dashboard.
    expect(source).toMatch(/sitesStore\.sites\.slice\(0,\s*5\)/)
  })

  test('Settings simple Network panel renders port snapshot', () => {
    const source = readFileSync(join(srcRoot, 'components', 'pages', 'Settings.vue'), 'utf-8')
    expect(source).toMatch(/settings\.simple\.network\.title/)
    expect(source).toMatch(/settings\.simple\.network\.editInAdvanced/)
    // Network panel must show all three primary ports as read-only mono
    // values — Apache HTTP, Apache HTTPS, MySQL.
    expect(source).toMatch(/:\{\{ httpPort \}\}/)
    expect(source).toMatch(/:\{\{ httpsPort \}\}/)
    expect(source).toMatch(/:\{\{ mysqlPort \}\}/)
  })

  test('Settings simple Backup panel offers one-click manual backup', () => {
    const source = readFileSync(join(srcRoot, 'components', 'pages', 'Settings.vue'), 'utf-8')
    expect(source).toMatch(/settings\.simple\.backup\.title/)
    expect(source).toMatch(/settings\.simple\.backup\.create/)
    expect(source).toMatch(/createBackupFromSimple/)
    expect(source).toMatch(/settings\.simple\.backup\.openManager/)
  })

  test('SiteDetailSimple wires document root edit + folder picker', () => {
    const source = readFileSync(join(srcRoot, 'components', 'pages', 'SiteDetailSimple.vue'), 'utf-8')
    // The field was missing entirely before commit 59e1d29 — operator
    // had no way to edit docroot in simple mode. Lock both the input
    // ref and the picker handler.
    expect(source).toMatch(/v-model="documentRoot"/)
    expect(source).toMatch(/onDocRootChange/)
    expect(source).toMatch(/pickDocRoot/)
    expect(source).toMatch(/sites\.documentRoot/)
  })

  test('Sites simple create dialog surfaces no-PHP empty state', () => {
    const source = readFileSync(join(srcRoot, 'components', 'pages', 'Sites.vue'), 'utf-8')
    // The alert + Open-PHP-Manager link only fires when phpVersions is
    // empty — proves to the beginner that they can install PHP from
    // here instead of leaving the create dialog confused.
    expect(source).toMatch(/v-if="phpVersions\.length === 0"/)
    expect(source).toMatch(/sites\.simple\.noPhpInstalledTitle/)
    expect(source).toMatch(/sites\.simple\.noPhpInstalledCta/)
    expect(source).toMatch(/openPhpManagerFromSimple/)
  })

  test('i18n keys for all simple-mode redesigns exist cs+en', () => {
    const cs = JSON.parse(readFileSync(join(srcRoot, 'locales', 'cs.json'), 'utf-8')) as Record<string, unknown>
    const en = JSON.parse(readFileSync(join(srcRoot, 'locales', 'en.json'), 'utf-8')) as Record<string, unknown>

    const getPath = (obj: Record<string, unknown>, path: string): unknown =>
      path.split('.').reduce<unknown>((acc, k) => (acc && typeof acc === 'object'
        ? (acc as Record<string, unknown>)[k]
        : undefined), obj)

    const required = [
      'dashboard.simple.recent.title',
      'dashboard.simple.recent.viewAll',
      'settings.simple.network.title',
      'settings.simple.network.editInAdvanced',
      'settings.simple.backup.title',
      'settings.simple.backup.create',
      'settings.simple.backup.openManager',
      'sites.detail.simple.docRootHint',
      'sites.simple.noPhpInstalledTitle',
      'sites.simple.noPhpInstalledHint',
      'sites.simple.noPhpInstalledCta',
    ]
    for (const k of required) {
      expect(getPath(cs, k), `cs ${k}`).toBeTruthy()
      expect(getPath(en, k), `en ${k}`).toBeTruthy()
    }
  })
})
