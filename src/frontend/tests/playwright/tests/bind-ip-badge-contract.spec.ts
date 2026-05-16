import { test, expect } from './_fixtures'
import { readFileSync } from 'node:fs'
import { join, resolve } from 'node:path'

// Source-level contract for the "doporučeno / recommended" badge in the
// bind-IP picker. The wildcard `*` and the IPv4/IPv6 loopback options
// must render the badge so beginners see at-a-glance what's safe to pick.
// Regression here means dropping the badge would silently downgrade the
// simple-create dialog UX without a build error.

const srcRoot = resolve(process.cwd(), 'src')

test.describe('Bind-IP badge contract', () => {
  test('Sites.vue create dialog wires bind-opt-badge on wildcard + loopback options', () => {
    const source = readFileSync(join(srcRoot, 'components', 'pages', 'Sites.vue'), 'utf-8')
    // Conditional v-if must check opt.wildcard or opt.loopback — that's
    // what decides whether the badge renders next to a given option.
    expect(source).toMatch(/v-if="opt\.wildcard \|\| opt\.loopback"/)
    expect(source).toMatch(/class="bind-opt-badge"/)
    expect(source).toMatch(/sites\.bindIpRecommended/)
  })

  test('SiteDetailSimple.vue carries the same badge wiring', () => {
    const source = readFileSync(join(srcRoot, 'components', 'pages', 'SiteDetailSimple.vue'), 'utf-8')
    expect(source).toMatch(/v-if="opt\.wildcard \|\| opt\.loopback"/)
    expect(source).toMatch(/class="bind-opt-badge"/)
    expect(source).toMatch(/sites\.bindIpRecommended/)
  })

  test('Global tokens.css defines bind-opt-badge styles', () => {
    const css = readFileSync(join(srcRoot, 'assets', 'tokens.css'), 'utf-8')
    // The badge styling lives in global CSS because el-option content
    // renders via the Element Plus tooltip portal — scoped CSS doesn't
    // reach into the slot. Verify the class is defined globally.
    expect(css).toMatch(/\.bind-opt-badge\s*{/)
    expect(css).toMatch(/\.bind-opt-label\s*{/)
  })

  test('i18n key sites.bindIpRecommended exists in both locales', () => {
    const cs = JSON.parse(readFileSync(join(srcRoot, 'locales', 'cs.json'), 'utf-8')) as {
      sites?: { bindIpRecommended?: string }
    }
    const en = JSON.parse(readFileSync(join(srcRoot, 'locales', 'en.json'), 'utf-8')) as {
      sites?: { bindIpRecommended?: string }
    }
    expect(cs.sites?.bindIpRecommended).toBeTruthy()
    expect(en.sites?.bindIpRecommended).toBeTruthy()
  })
})
