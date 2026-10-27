import { test, expect } from './_fixtures'
import { readFileSync } from 'node:fs'
import { join, resolve } from 'node:path'

// Source-level contract for the app shell: the menu badge, status bar and
// Overview must count the same services, the shortcuts must not depend on the
// keyboard layout, and the onboarding wizard must stay closable.

const srcRoot = resolve(process.cwd(), 'src')
const read = (...parts: string[]) => readFileSync(join(srcRoot, ...parts), 'utf-8')

test.describe('Shell services contract', () => {
  test('menu, status bar and Overview read the shared visible/crashed sets', () => {
    for (const file of [
      ['components', 'layout', 'AppSidebar.vue'],
      ['components', 'layout', 'AppStatusBar.vue'],
      ['components', 'pages', 'Dashboard.vue'],
      ['components', 'overview', 'StackTable.vue'],
      ['components', 'overview', 'AttentionList.vue'],
    ]) {
      const source = read(...file)
      expect(source, file.join('/')).toMatch(/daemonStore\.(visibleServices|problemServices|runningServices)/)
      // No private re-count over every service, including those of disabled plugins.
      expect(source, file.join('/')).not.toMatch(/daemonStore\.services\.filter/)
      expect(source, file.join('/')).not.toMatch(/state === 4\)\.length/)
    }
    const store = read('stores', 'daemon.ts')
    expect(store).toMatch(/visibleServices = computed/)
    expect(store).toMatch(/problemServices = computed/)
    // A failed start (startError) counts like a crash, the same test the Services page uses.
    expect(store).toMatch(/needsAttention\(s\)/)
  })

  test('problem badge shows a number with text and links to the problem filter', () => {
    const source = read('components', 'layout', 'AppSidebar.vue')
    expect(source).toMatch(/class="nav-problems"/)
    expect(source).toMatch(/filter: 'problems'/)
    expect(source).toMatch(/aria-describedby/)
    // The old unlabeled red cross is gone.
    expect(source).not.toMatch(/CircleCloseFilled/)
  })

  test('MCP stays in the menu and palette while MCP is off, marked as off', () => {
    const sidebar = read('components', 'layout', 'AppSidebar.vue')
    expect(sidebar).not.toMatch(/if \(featureFlagsStore\.showMcpSurface\) ops\.push/)
    expect(sidebar).toMatch(/tag: featureFlagsStore\.mcpEnabled \? undefined : t\('nav\.mcpOff'\)/)
    expect(sidebar).toMatch(/mcpConfirmStore\.count/)
    const palette = read('components', 'shared', 'CommandPalette.vue')
    expect(palette).not.toMatch(/if \(featureFlagsStore\.showMcpSurface\)/)
  })

  test('Overview opens the full service editor instead of a second side panel', () => {
    const dashboard = read('components', 'pages', 'Dashboard.vue')
    expect(dashboard).not.toMatch(/ConfigSidePanel/)
    expect(dashboard).toMatch(/\/service\/\$\{encodeURIComponent\(id\)\}\/config/)
  })

  test('a disabled primary button is a neutral key, not a faded cobalt one', () => {
    const tokens = read('assets', 'tokens.css')
    expect(tokens).toMatch(/\.el-button--primary\.is-disabled:not\(\.is-text\):not\(\.is-link\)[\s\S]*?--el-button-bg-color: var\(--wdc-surface-2\)/)
  })

  test('palette offers restart and manage next to start and stop', () => {
    const palette = read('components', 'shared', 'CommandPalette.vue')
    expect(palette).toMatch(/commandPalette\.restartService/)
    expect(palette).toMatch(/commandPalette\.manageService/)
    expect(palette).toMatch(/manageRouteOf\(svc\.id\)/)
  })

  test('browser sign-in can be cancelled and times out', () => {
    const auth = read('stores', 'auth.ts')
    expect(auth).toMatch(/function cancelLogin/)
    expect(auth).toMatch(/SSO_TIMEOUT_MS/)
    const login = read('components', 'pages', 'Login.vue')
    expect(login).toMatch(/authStore\.cancelLogin\(\)/)
  })

  test('shortcuts read the typed letter or physical key, not a fixed e.key', () => {
    const source = read('App.vue')
    expect(source).not.toMatch(/e\.key === 'k'/)
    expect(source).not.toMatch(/e\.key >= '1'/)
    expect(source).toMatch(/\^Digit\(\[1-7\]\)\$/)
    expect(source).toMatch(/isEditableTarget/)
  })

  test('onboarding wizard can be closed with the cross, Esc and a click outside', () => {
    const source = read('components', 'shared', 'OnboardingWizard.vue')
    expect(source).not.toMatch(/:show-close="false"/)
    expect(source).not.toMatch(/:close-on-press-escape="false"/)
    expect(source).not.toMatch(/:close-on-click-modal="false"/)
    expect(source).toMatch(/fetchOnboardingState/)
    expect(source).toMatch(/setInterval/)
  })

  test('daemon reports installed, version and crashKind on services', () => {
    const projection = readFileSync(
      resolve(srcRoot, '..', '..', 'daemon', 'NKS.WebDevConsole.Daemon', 'Services', 'ServiceInfoProjection.cs'), 'utf-8')
    expect(projection).toMatch(/bool Installed/)
    expect(projection).toMatch(/string\? CrashKind/)
    const types = read('api', 'types.ts')
    expect(types).toMatch(/installed\?: boolean/)
    expect(types).toMatch(/crashKind\?:/)
  })

  test('new shell copy exists in cs and en', () => {
    for (const locale of ['cs', 'en']) {
      const data = JSON.parse(read('locales', `${locale}.json`)) as {
        nav: Record<string, unknown>
        footer: Record<string, unknown>
        overview: { status: Record<string, unknown>; attention: Record<string, unknown>; batch: Record<string, unknown> }
        onboarding: Record<string, unknown>
      }
      expect(data.nav.problemsBadge, `${locale} nav.problemsBadge`).toBeTruthy()
      expect(data.footer.servicesRunning, `${locale} footer.servicesRunning`).toBeTruthy()
      expect(data.overview.status.notInstalled, `${locale} overview.status.notInstalled`).toBeTruthy()
      expect(data.overview.attention.apacheMissing, `${locale} overview.attention.apacheMissing`).toBeTruthy()
      expect(data.overview.batch.startFailed, `${locale} overview.batch.startFailed`).toBeTruthy()
      expect(data.onboarding.later, `${locale} onboarding.later`).toBeTruthy()
    }
  })
})
