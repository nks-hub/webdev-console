import { test, expect } from './_fixtures'
import { readFileSync } from 'node:fs'
import { join, resolve } from 'node:path'

// A11y contract for the sites table (one table for Simple and Advanced). The
// domain is a real link, so a keyboard or screen-reader user can open a site
// without the row click; every icon-only button names its site.
// Source-level template checking suffices for static-attribute coverage —
// a future refactor that drops a link, caption or label lands here.

// Playwright's CWD for `npx playwright test` is `src/frontend/`, so resolve
// the Vue source dir relative to that. Avoids `import.meta.url` which
// Playwright's CJS test loader (transformIgnorePatterns) can't parse.
const srcRoot = resolve(process.cwd(), 'src')

test.describe('Sites table a11y', () => {
  test('SitesTable.vue has a caption, column scopes, a domain link and named row buttons', () => {
    const source = readFileSync(join(srcRoot, 'components', 'sites', 'SitesTable.vue'), 'utf-8')
    expect(source).toMatch(/<caption class="visually-hidden">/)
    expect(source).toMatch(/<th[^>]*scope="col"/)
    // The domain opens the detail through a router-link, not a click handler on a div.
    expect(source).toMatch(/<router-link class="web-domain"/)
    // Icon-only and menu buttons carry the site in their label.
    expect(source).toMatch(/sites\.list\.openAria/)
    expect(source).toMatch(/sites\.list\.folderAria/)
    expect(source).toMatch(/sites\.card\.moreActions/)
    // Sort state is announced.
    expect(source).toMatch(/:aria-sort=/)
  })

  test('status is always a word, never colour alone', () => {
    const source = readFileSync(join(srcRoot, 'components', 'sites', 'SiteStatusBadge.vue'), 'utf-8')
    expect(source).toMatch(/<span class="ss-text">\{\{ label \}\}<\/span>/)
    expect(source).toMatch(/aria-hidden="true"/)
  })

  test('DashboardSimple.vue recent-sites rows are links inside plain list items', () => {
    const source = readFileSync(join(srcRoot, 'components', 'pages', 'DashboardSimple.vue'), 'utf-8')

    // Each recent site is a router-link (navigation, keyboard and screen
    // reader support built in) inside a plain <li>. A role on the <li>
    // itself breaks list semantics (axe: list / listitem).
    const linkMatch = source.match(/<router-link[\s\S]*?class="simple-recent-item"[\s\S]*?>/)
    expect(linkMatch, 'simple-recent-item router-link present in template').not.toBeNull()
    const opening = linkMatch![0]
    expect(opening).toMatch(/:to="/)
    expect(opening).toMatch(/aria-label/)
    expect(source).not.toMatch(/<li[^>]*role="button"/)
  })

  test('i18n key sites.card.openSiteAria exists in both locales', () => {
    const cs = JSON.parse(
      readFileSync(join(srcRoot, 'locales', 'cs.json'), 'utf-8'),
    ) as { sites?: { card?: Record<string, string> } }
    const en = JSON.parse(
      readFileSync(join(srcRoot, 'locales', 'en.json'), 'utf-8'),
    ) as { sites?: { card?: Record<string, string> } }

    expect(cs.sites?.card?.openSiteAria).toBeTruthy()
    expect(en.sites?.card?.openSiteAria).toBeTruthy()
    // The label must contain a {domain} placeholder so the rendered text
    // actually names the site rather than reading a generic "open site".
    expect(cs.sites!.card!.openSiteAria).toContain('{domain}')
    expect(en.sites!.card!.openSiteAria).toContain('{domain}')
  })
})
