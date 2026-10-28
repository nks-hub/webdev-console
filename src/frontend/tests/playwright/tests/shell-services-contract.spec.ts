import { test, expect } from './_fixtures'
import { readdirSync, readFileSync } from 'node:fs'
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

  test('settings save bar paints above Element Plus input-number buttons (z-index 1)', () => {
    const shell = read('components', 'settings', 'SettingsShell.vue')
    const footer = shell.match(/\.shell-footer \{([^}]*)\}/)?.[1] ?? ''
    expect(footer).toMatch(/position: sticky/)
    expect(footer).toMatch(/background: var\(--wdc-bg\)/)
    expect(Number(footer.match(/z-index: (\d+)/)?.[1])).toBeGreaterThan(1)
  })

  test('sign-in entry is marked current on the login page', () => {
    const sidebar = read('components', 'layout', 'AppSidebar.vue')
    expect(sidebar).toMatch(/active: isPathActive\('\/login'\)/)
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

  test('palette offers every menu page and skips pages of a disabled plugin', () => {
    const palette = read('components', 'shared', 'CommandPalette.vue')
    for (const path of ['/composer', '/hosts', '/help']) {
      expect(palette, path).toContain(`go('${path}'`)
    }
    // The router bounces a disabled plugin's page to /sites, so the palette must not offer it.
    expect(palette).toMatch(/pluginsStore\.isRouteVisible\(c\.route\)/)
  })

  test('app tour points at the MCP page, not at a wdc CLI the installer does not ship', () => {
    const tour = read('components', 'shared', 'GettingStartedWizard.vue')
    expect(tour).not.toMatch(/wdc (sites|db|ssl)/)
    expect(tour).toMatch(/key: 'mcp'/)
    for (const locale of ['cs', 'en']) {
      const data = JSON.parse(read('locales', `${locale}.json`)) as {
        help: { wizard: { steps: Record<string, { title: string; desc: string; tip: string }> } }
      }
      const steps = data.help.wizard.steps
      expect(steps.cli, `${locale} help.wizard.steps.cli`).toBeUndefined()
      expect(steps.mcp?.desc, `${locale} help.wizard.steps.mcp`).toBeTruthy()
      expect(steps.mcp.desc).not.toMatch(/<code>wdc<\/code>/)
    }
  })

  test('help names only MCP tools that exist', () => {
    const toolsDir = resolve(srcRoot, '..', '..', '..', 'services', 'mcp-server', 'src', 'tools')
    const toolSource = readdirSync(toolsDir)
      .filter(f => f.endsWith('.ts') && !f.includes('.test.'))
      .map(f => readFileSync(join(toolsDir, f), 'utf-8'))
      .join('\n')
    for (const locale of ['cs', 'en']) {
      const data = JSON.parse(read('locales', `${locale}.json`)) as { help: { topics: { mcp: { body: string } } } }
      const names = data.help.topics.mcp.body.match(/wdc_[a-z_]+/g) ?? []
      expect(names.length, locale).toBeGreaterThan(0)
      for (const name of names) expect(toolSource, `${locale} ${name}`).toContain(`'${name}'`)
    }
  })

  test('a new page opens at the top of the shared scroll container', () => {
    const app = read('App.vue')
    expect(app).toMatch(/watch\(\(\) => route\.path, \(\) => \{[\s\S]*?getElementById\('main'\)\?\.scrollTo\(0, 0\)/)
  })

  test('side form labels share the control height so they sit on its midline', () => {
    const tokens = read('assets', 'tokens.css')
    const rule = tokens.match(/\.el-form-item--label-left \.el-form-item__label,\s*\.el-form-item--label-right \.el-form-item__label \{([^}]*)\}/)?.[1] ?? ''
    expect(rule).toMatch(/height: 36px/)
    expect(rule).toMatch(/line-height: 36px/)
  })

  test('simple status bar says which services it counts and counts the same set', () => {
    const bar = read('components', 'layout', 'AppStatusBar.vue')
    // "Everything is running" covers installed services only; the rest falls back to the advanced wording.
    expect(bar).toMatch(/s\.installed !== false/)
    expect(bar).toMatch(/t\('footer\.servicesRunning', \{ running: runningCount\.value, total: totalCount\.value \}/)
    for (const [locale, text] of [['cs', 'Všechny nainstalované služby běží'], ['en', 'All installed services are running']]) {
      const data = JSON.parse(read('locales', `${locale}.json`)) as { footer: { simpleAllRunning: string } }
      expect(data.footer.simpleAllRunning, locale).toBe(text)
    }
  })

  test('simple dashboard totals come from the access and error logs the Metrics tab reads', () => {
    const source = read('components', 'pages', 'DashboardSimple.vue')
    expect(source).toMatch(/fetchHourlyRequests\(domain\)/)
    expect(source).toMatch(/fetchRecentErrors\(domain\)/)
    // /metrics/history has no per-request fields (requestCount is cumulative) and returns samples oldest first.
    expect(source).not.toMatch(/metrics\/history/)
    expect(source).not.toMatch(/s\.requests \?\? s\.hits/)
    // The component is a lazy chunk that mounts after the page's first activation, so mount must load too.
    expect(source).toMatch(/onMounted\(\(\) => \{ void loadAggregates\(\) \}\)/)
    const helper = read('utils', 'siteActivity.ts')
    expect(helper).toMatch(/granularity: '1h'/)
    expect(helper).toMatch(/ERROR_SEVERITIES/)
  })

  test('help documents the wdc CLI with commands that exist', () => {
    expect(read('components', 'pages', 'Help.vue')).toContain("{ key: 'cli' }")
    const cliSource = readFileSync(
      resolve(srcRoot, '..', '..', 'daemon', 'NKS.WebDevConsole.Cli', 'Program.cs'), 'utf-8')
    for (const locale of ['cs', 'en']) {
      const data = JSON.parse(read('locales', `${locale}.json`)) as { help: { topics: { cli: { body: string } } } }
      const commands = [...data.help.topics.cli.body.matchAll(/<code>wdc ([a-z]+)(?: ([a-z]+))?/g)]
      expect(commands.length, locale).toBeGreaterThan(3)
      for (const [, command, sub] of commands) {
        expect(cliSource, `${locale} wdc ${command}`).toContain(`new Command("${command}"`)
        if (sub && /^(start|generate)$/.test(sub)) expect(cliSource, `${locale} wdc ${command} ${sub}`).toMatch(new RegExp(`"${sub}"|\\("${sub}"`))
      }
    }
  })

  test('activity feed hides the database trigger as a source and labels the other sources', () => {
    const dashboard = read('components', 'pages', 'Dashboard.vue')
    expect(dashboard).toMatch(/SILENT_SOURCES = new Set\(\['app', 'trigger'\]\)/)
    for (const locale of ['cs', 'en']) {
      const data = JSON.parse(read('locales', `${locale}.json`)) as { dashboard: { activity: { source: Record<string, string> } } }
      for (const key of ['mcp', 'cli', 'api', 'migration', 'import']) {
        expect(data.dashboard.activity.source[key], `${locale} ${key}`).toBeTruthy()
      }
    }
  })

  test('mode names are lower case inside a sentence', () => {
    const cs = JSON.parse(read('locales', 'cs.json')) as { app: { advancedPageNote: string } }
    expect(cs.app.advancedPageNote).toBe('Tato stránka patří do pokročilého režimu, proto ji menu v jednoduchém režimu neukazuje.')
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
