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
//   - Site settings document root edit — was "neni tam zadna editace"
//   - No-PHP empty state in the create site dialog
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

  test('Site settings wire document root edit + folder picker', () => {
    const source = readFileSync(join(srcRoot, 'components', 'pages', 'SiteEdit.vue'), 'utf-8')
    // Operators had no way to edit docroot in simple mode before commit 59e1d29.
    // It now lives in the one Settings tab, next to the folder browser.
    expect(source).toMatch(/v-model="site\.documentRoot"/)
    expect(source).toMatch(/<FolderBrowser/)
    expect(source).toMatch(/siteEdit\.folderHint/)
  })

  test('Create site dialog surfaces a no-PHP empty state with an install button', () => {
    const source = readFileSync(join(srcRoot, 'components', 'sites', 'CreateSiteDialog.vue'), 'utf-8')
    // The callout only shows when no PHP version is installed — it tells
    // the beginner they can install PHP from here instead of leaving the
    // dialog confused.
    expect(source).toMatch(/siteState\.phpVersions === null \|\| siteState\.phpVersions\.length/)
    expect(source).toMatch(/sites\.new\.noPhp/)
    expect(source).toMatch(/siteState\.install\('php'\)/)
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
      'siteEdit.folderHint',
      'sites.new.noPhp',
      'sites.new.installPhp',
    ]
    for (const k of required) {
      expect(getPath(cs, k), `cs ${k}`).toBeTruthy()
      expect(getPath(en, k), `en ${k}`).toBeTruthy()
    }
  })
})
