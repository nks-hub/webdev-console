import { test, expect } from './_fixtures'
import { readFileSync } from 'node:fs'
import { join, resolve } from 'node:path'

// Source-level contracts for behaviour of the sites pages that a refactor could
// drop without a build error. Each one names the screen it protects.

const srcRoot = resolve(process.cwd(), 'src')
const read = (...parts: string[]) => readFileSync(join(srcRoot, ...parts), 'utf-8')

test.describe('Sites flow contract', () => {
  test('Sites list is cached by keep-alive: "/" is live only while shown, data is fetched again on return', () => {
    const source = read('components', 'pages', 'Sites.vue')
    expect(source).toMatch(/onActivated\(\(\) => \{[\s\S]*?addEventListener\('keydown', onKeydown\)/)
    expect(source).toMatch(/onDeactivated\(\(\) => window\.removeEventListener\('keydown', onKeydown\)\)/)
    expect(source).toMatch(/if \(activatedOnce\) void reload\(\)/)
  })

  test('New site dialog does not default to HTTPS while the certificate authority is missing', () => {
    const source = read('components', 'sites', 'CreateSiteDialog.vue')
    expect(source).toMatch(/form\.sslEnabled = !needsSetup/)
    expect(source).toMatch(/sslTouched/)
    // Installing the authority from the dialog switches HTTPS on.
    expect(source).toMatch(/async function installHttps[\s\S]*?form\.sslEnabled = true/)
  })

  test('New site dialog only offers a database while the database server runs', () => {
    const source = read('components', 'sites', 'CreateSiteDialog.vue')
    expect(source).toMatch(/dbBlocked = computed\(\(\) => \['missing', 'stopped', 'starting'\]\.includes\(siteState\.databaseServer\)\)/)
    expect(source).toMatch(/siteState\.startDatabase\(\)/)
  })

  test('New site dialog warns when another site already serves the folder', () => {
    expect(read('components', 'sites', 'CreateSiteDialog.vue')).toMatch(/useFolderOwner/)
    expect(read('components', 'pages', 'SiteEdit.vue')).toMatch(/useFolderOwner/)
  })

  test('A web server that is down while a site is being saved is "restarting", not a fault', () => {
    const state = read('stores', 'siteState.ts')
    expect(state).toMatch(/apply\.applying\.size > 0\) return 'restarting'/)
    // Creating a site goes through the same tracking as saving one.
    expect(read('components', 'sites', 'CreateSiteDialog.vue')).toMatch(/siteApplyStore\.track\(domain, \(\) => sitesStore\.create\(payload\)\)/)
  })

  test('web server and service states come from the shared service state helper', () => {
    expect(read('stores', 'siteState.ts')).toMatch(/serviceViewState/)
    expect(read('components', 'sites', 'SiteServices.vue')).toMatch(/serviceViewState/)
  })

  test('the web and database service is a visible one, running first (a switched-off MariaDB plugin must not mask MySQL)', () => {
    const state = read('stores', 'siteState.ts')
    expect(state).toMatch(/plugins\.isServiceVisible\(s\.id\)/)
    expect(state).toMatch(/list\.find\(s => serviceViewState\(s\) === 'running'\)/)
    expect(state).toMatch(/pickService\(\['mysql', 'mariadb'\]\)/)
  })

  test('New site dialog offers a tunnel only once the Cloudflare tunnel and default zone are set up', () => {
    expect(read('stores', 'siteState.ts')).toMatch(/cfg\.tunnelId && cfg\.defaultZoneId/)
    expect(read('components', 'sites', 'CreateSiteDialog.vue')).toMatch(/:disabled="!siteState\.cloudflareReady"/)
  })

  test('chart y-axis shows whole numbers only', () => {
    expect(read('components', 'sites', 'SiteMetrics.vue')).toMatch(/minInterval: 1/)
  })

  test('i18n keys of the new states exist in both locales', () => {
    const get = (o: Record<string, unknown>, path: string) =>
      path.split('.').reduce<unknown>((a, k) => (a && typeof a === 'object' ? (a as Record<string, unknown>)[k] : undefined), o)
    const required = [
      'sites.banner.restarting.title', 'sites.banner.stopping.title', 'sites.banner.crashed.detail',
      'sites.new.folderUsed', 'sites.new.httpsOffNeedsCa', 'sites.new.mysqlStopped', 'sites.new.creating',
      'sites.list.openWaiting', 'siteApply.applyHint', 'siteEdit.ov.explain.waitingBusy',
    ]
    for (const locale of ['cs', 'en']) {
      const json = JSON.parse(readFileSync(join(srcRoot, 'locales', `${locale}.json`), 'utf-8')) as Record<string, unknown>
      for (const key of required) expect(get(json, key), `${locale} ${key}`).toBeTruthy()
    }
  })
})
