import { test, expect } from './_fixtures'
import { readFileSync } from 'node:fs'
import { join, resolve } from 'node:path'

// Source-level contract: a per-site card must not stop Apache. Apache
// serves every site, so a per-card Stop took all sites down (commit
// 606bdf8 first put a confirm in front of it; the redesign removed the
// button). Starting the web server from a card stays possible while it
// is stopped. This regression-locks both halves.

const srcRoot = resolve(process.cwd(), 'src')

test.describe('Per-site card web server contract', () => {
  test('SimpleSiteCard offers no Apache stop', () => {
    const source = readFileSync(join(srcRoot, 'components', 'pages', 'SimpleSiteCard.vue'), 'utf-8')
    expect(source).not.toMatch(/stop-apache/)
    expect(source).not.toMatch(/stopApacheTooltip/)
  })

  test('SimpleSiteCard offers Start web server only while Apache is stopped', () => {
    const source = readFileSync(join(srcRoot, 'components', 'pages', 'SimpleSiteCard.vue'), 'utf-8')
    expect(source).toMatch(/v-if="!apacheRunning"[\s\S]*?emit\('start-apache'\)[\s\S]*?sites\.card\.startWebServer/)
  })

  test('SitesListSimple no longer wires a stop handler', () => {
    const source = readFileSync(join(srcRoot, 'components', 'pages', 'SitesListSimple.vue'), 'utf-8')
    expect(source).not.toMatch(/@stop-apache=/)
    expect(source).not.toMatch(/async function stopApache/)
  })

  test('i18n key for Start web server exists in both locales', () => {
    for (const locale of ['cs', 'en']) {
      const json = JSON.parse(readFileSync(join(srcRoot, 'locales', `${locale}.json`), 'utf-8')) as
        { sites?: { card?: Record<string, string> } }
      expect(json.sites?.card?.startWebServer, `${locale} startWebServer`).toBeTruthy()
    }
  })
})
