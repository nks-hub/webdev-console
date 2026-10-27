import { test, expect } from './_fixtures'
import { readFileSync } from 'node:fs'
import { join, resolve } from 'node:path'

// Source-level contract: a single site must not stop Apache. Apache serves
// every site, so a per-site Stop took all sites down (commit 606bdf8 first
// put a confirm in front of it; the redesign removed the button). Starting
// the web server stays possible, in one place per page, while it is stopped.
// This regression-locks both halves.

const srcRoot = resolve(process.cwd(), 'src')
const read = (...parts: string[]) => readFileSync(join(srcRoot, ...parts), 'utf-8')

test.describe('Per-site web server contract', () => {
  test('site list and site page offer no Apache stop', () => {
    for (const file of [
      ['components', 'sites', 'SitesTable.vue'],
      ['components', 'sites', 'WebServerBanner.vue'],
      ['components', 'pages', 'Sites.vue'],
    ]) {
      const source = read(...file)
      expect(source, file.join('/')).not.toMatch(/stop-apache/)
      expect(source, file.join('/')).not.toMatch(/stopApache/)
      expect(source, file.join('/')).not.toMatch(/stopService/)
    }
  })

  test('the banner above the list offers Start only while Apache is stopped, Install when it is missing', () => {
    const source = read('components', 'sites', 'WebServerBanner.vue')
    expect(source).toMatch(/siteState\.startWebServer\(\)/)
    expect(source).toMatch(/siteState\.install\('apache'\)/)
    expect(source).toMatch(/sites\.banner\.startApache/)
    expect(source).toMatch(/ws === 'missing'/)
    expect(source).toMatch(/ws === 'stopped'/)
  })

  test('a failed start is reported instead of swallowed', () => {
    const source = read('stores', 'siteState.ts')
    expect(source).toMatch(/async function startWebServer[\s\S]*?catch \(e\)[\s\S]*?ElMessage\.error\(t\('toast\.startFailed'/)
  })

  test('i18n keys for Start/Install Apache exist in both locales', () => {
    for (const locale of ['cs', 'en']) {
      const json = JSON.parse(readFileSync(join(srcRoot, 'locales', `${locale}.json`), 'utf-8')) as
        { sites?: { banner?: Record<string, unknown> } }
      expect(json.sites?.banner?.startApache, `${locale} startApache`).toBeTruthy()
      expect(json.sites?.banner?.installApache, `${locale} installApache`).toBeTruthy()
    }
  })
})
