import { test, expect } from './_fixtures'
import { readFileSync } from 'node:fs'
import { join, resolve } from 'node:path'

// A11y contract for the simple-mode site cards. The card-body div is the
// primary navigation target — without keyboard support a screen-reader or
// keyboard-only operator can't open a site without using the dropdown menu.
// Source-level template checking suffices for static-attribute coverage —
// a future refactor that drops role/tabindex/aria-label lands here.

// Playwright's CWD for `npx playwright test` is `src/frontend/`, so resolve
// the Vue source dir relative to that. Avoids `import.meta.url` which
// Playwright's CJS test loader (transformIgnorePatterns) can't parse.
const srcRoot = resolve(process.cwd(), 'src')

test.describe('Simple-mode card a11y', () => {
  test('SimpleSiteCard.vue template carries role + tabindex + aria-label', () => {
    // Read the .vue source directly from disk. Source-level template
    // checking is sufficient for static-attribute coverage and avoids
    // a full headless render.
    const source = readFileSync(join(srcRoot, 'components', 'pages', 'SimpleSiteCard.vue'), 'utf-8')

    // The card-body div must carry all three a11y attributes.
    const cardBodyMatch = source.match(/<div\s+class="card-body"[\s\S]*?>/)
    expect(cardBodyMatch, 'card-body div present in template').not.toBeNull()
    const opening = cardBodyMatch![0]
    expect(opening).toMatch(/role="button"/)
    expect(opening).toMatch(/tabindex="0"/)
    expect(opening).toMatch(/aria-label/)
    expect(opening).toMatch(/keydown\.enter/)
    expect(opening).toMatch(/keydown\.space/)
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
