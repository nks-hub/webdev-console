import { test, expect } from './_fixtures'
import { readFileSync } from 'node:fs'
import { join, resolve } from 'node:path'

// Source-level contract: stopping Apache from a per-site card must
// trigger a confirm dialog. Per-card Stop is the most-misleading button
// in the simple UI — it stops Apache globally, taking down EVERY site.
// Commit 606bdf8 added an ElMessageBox.confirm gate so an accidental
// click doesn't break unrelated work. This regression-locks the gate.

const srcRoot = resolve(process.cwd(), 'src')

test.describe('Apache stop confirm contract', () => {
  test('SitesListSimple stopApache wraps service call with ElMessageBox.confirm', () => {
    const source = readFileSync(join(srcRoot, 'components', 'pages', 'SitesListSimple.vue'), 'utf-8')
    // Find the stopApache function definition + body span — we need to
    // verify it asks for confirmation BEFORE invoking servicesStore.stop /
    // stopService. A regression where the confirm is removed shows up
    // as the function calling stopService('apache') unconditionally.
    const match = source.match(/async function stopApache\s*\([^)]*\)\s*{([\s\S]*?)\n\}/)
    expect(match, 'stopApache function present').not.toBeNull()
    const body = match![1]
    expect(body).toMatch(/ElMessageBox\.confirm/)
    expect(body).toMatch(/sites\.card\.stopApacheConfirm/)
    expect(body).toMatch(/sites\.card\.stopApacheTitle/)
    // The confirm must come before the stopService/server call so a
    // rejected confirm bails before any side-effect.
    expect(body.indexOf('ElMessageBox.confirm')).toBeLessThan(body.indexOf('stopService'))
  })

  test('i18n keys for Apache stop confirm exist in both locales', () => {
    const cs = JSON.parse(
      readFileSync(join(srcRoot, 'locales', 'cs.json'), 'utf-8'),
    ) as { sites?: { card?: Record<string, string> } }
    const en = JSON.parse(
      readFileSync(join(srcRoot, 'locales', 'en.json'), 'utf-8'),
    ) as { sites?: { card?: Record<string, string> } }

    for (const k of ['stopApacheConfirm', 'stopApacheTitle', 'stopApacheTooltip']) {
      expect(cs.sites?.card?.[k], `cs ${k}`).toBeTruthy()
      expect(en.sites?.card?.[k], `en ${k}`).toBeTruthy()
    }
    // The Czech confirm copy must warn about the global scope (every site).
    expect(cs.sites!.card!.stopApacheConfirm).toMatch(/VŠECHNY|všechny|všech/i)
  })

  test('SimpleSiteCard stop button uses the stopApacheTooltip key, not generic stop', () => {
    const source = readFileSync(join(srcRoot, 'components', 'pages', 'SimpleSiteCard.vue'), 'utf-8')
    // The hint that explains "stops Apache globally" lives in the
    // tooltip — generic `t('sites.card.stop')` would be misleading
    // because the action takes down every site, not just this one.
    expect(source).toMatch(/sites\.card\.stopApacheTooltip/)
  })
})
