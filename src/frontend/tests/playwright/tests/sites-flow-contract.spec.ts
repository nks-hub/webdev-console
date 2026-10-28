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

  test('POST /api/sites creates the folder before the site; only the create dialog promises that', () => {
    const endpoints = readFileSync(
      resolve(srcRoot, '..', '..', 'daemon', 'NKS.WebDevConsole.Daemon', 'Sites', 'SiteEndpoints.cs'), 'utf-8')
    expect(endpoints).toMatch(/MapPost\("\/api\/sites",[\s\S]*?EnsureDocumentRoot\([\s\S]*?sm\.CreateAsync\(site\)/)
    expect(read('components', 'pages', 'SiteEdit.vue')).toMatch(/\$t\('siteEdit\.folderMissing'\)/)
  })

  test('the overview activity card reads the real log and metrics endpoints, not fields they never return', () => {
    const card = read('components', 'sites', 'SiteActivity.vue')
    expect(card).toMatch(/fetchRecentErrors/)
    expect(card).toMatch(/fetchHourlyRequests/)
    expect(card).not.toMatch(/metrics\/history/)
    const util = read('utils', 'siteActivity.ts')
    expect(util).toMatch(/getErrorLogs\(domain, \{ lines: 1000, since:/)
    expect(util).toMatch(/granularity: '1h'/)
  })

  test('metric series toggles are buttons that expose their state', () => {
    const source = read('components', 'sites', 'SiteMetrics.vue')
    expect(source).toMatch(/<button[\s\S]*?:aria-pressed="activeSeries\.includes\(s\.key\)"/)
    expect(source).not.toMatch(/el-check-tag/)
  })

  test('New site dialog suggests a folder under the home folder the daemon reports; aliases and IP binding are Advanced only', () => {
    const dialog = read('components', 'sites', 'CreateSiteDialog.vue')
    expect(dialog).toMatch(/os\.homeDir/)
    expect(dialog).not.toMatch(/htdocs/)
    expect(dialog).toMatch(/v-if="uiMode\.isAdvanced" :label="\$t\('sites\.aliases'\)"/)
    expect(dialog).toMatch(/v-if="uiMode\.isAdvanced" :label="\$t\('sites\.bindIp'\)"/)
    const system = readFileSync(
      resolve(srcRoot, '..', '..', 'daemon', 'NKS.WebDevConsole.Daemon', 'Services', 'SystemEndpoints.cs'), 'utf-8')
    expect(system).toMatch(/homeDir = Environment\.GetFolderPath\(Environment\.SpecialFolder\.UserProfile\)/)
  })

  test('failed apply steps are not toasted in English, the site page shows every one of them translated', () => {
    const store = read('stores', 'sites.ts')
    const cs = JSON.parse(readFileSync(join(srcRoot, 'locales', 'cs.json'), 'utf-8')) as { siteApply: { steps: Record<string, string> } }
    for (const step of Object.keys(cs.siteApply.steps)) expect(store, step).toMatch(new RegExp(`APPLY_STEP_WARNING = .*\\b${step}\\b`))
  })

  test('the duplicate dialog checks the new domain with the same rules as the new site dialog', () => {
    expect(read('components', 'sites', 'CreateSiteDialog.vue')).toMatch(/domainProblem\(/)
    expect(read('components', 'sites', 'DuplicateSiteDialog.vue')).toMatch(/domainProblem\(/)
  })

  test('the Cloudflare and Composer site tabs and the tunnel badge come from the host when the plugin declares none; plugin tabs are Advanced only', () => {
    const store = read('stores', 'plugins.ts')
    for (const type of ['cloudflare-site-tab', 'cloudflare-tunnel-badge', 'composer-site-tab']) expect(store, type).toContain(`componentType: '${type}'`)
    expect(store).toMatch(/declared\.some\(d => d\.slot === slot\) \? declared : hostContributions\(m\.id\)/)
    const edit = read('components', 'pages', 'SiteEdit.vue')
    expect(edit).toMatch(/<PluginSlot\s+v-if="uiMode\.isAdvanced"\s+name="site-edit-tabs"/)
    expect(edit).toMatch(/pluginTabNames\.value\.includes\(tab\)\) \{\s+if \(uiMode\.isSimple\) activeTab\.value = 'overview'/)
  })

  test('closing the new site dialog empties it without validating the emptied fields', () => {
    const reset = read('components', 'sites', 'CreateSiteDialog.vue').match(/function reset\(\) \{[\s\S]*?\n\}/)?.[0] ?? ''
    expect(reset).toMatch(/formRef\.value\?\.resetFields\(\)/)
    // clearValidate lifts the guard that resetFields holds until the emptied fields have been looked at.
    expect(reset).not.toMatch(/clearValidate/)
  })

  test('logs/errors reads a site\'s own Apache error log and falls back to the global one only for a vhost without a log of its own', () => {
    const endpoints = readFileSync(
      resolve(srcRoot, '..', '..', 'daemon', 'NKS.WebDevConsole.Daemon', 'Sites', 'SiteEndpoints.cs'), 'utf-8')
    const handler = endpoints.match(/"\/api\/sites\/\{domain\}\/logs\/errors"[\s\S]*?"\/api\/sites\/\{domain\}\/logs\/access"/)?.[0] ?? ''
    expect(handler).toMatch(/SiteErrorLogReader\.Read\(/)
    // PHP: the site's own log only when its vhost points PHP at an ini that writes there.
    expect(handler).toMatch(/SitePhpErrorLog\.Resolve\(domain, apacheInstalls\)/)
    const reader = readFileSync(
      resolve(srcRoot, '..', '..', 'daemon', 'NKS.WebDevConsole.Daemon', 'Sites', 'SiteErrorLogReader.cs'), 'utf-8')
    expect(reader).toMatch(/ApacheSiteErrorLogs\.Resolve\(/)
    // The global log is chosen inside ApacheSiteErrorLogs, not added for every site here.
    expect(reader).not.toMatch(/"error\.log"/)
  })

  test('the Cloudflare and Composer tabs cost the Overview nothing and never ask for zones without an account', () => {
    const cloudflare = read('plugin-components', 'CloudflareSiteTab.vue')
    expect(cloudflare).toMatch(/<el-tab-pane :name="name" lazy>/)
    expect(cloudflare).not.toMatch(/onMounted/)
    expect(cloudflare).toMatch(/watch\(\(\) => props\.activeTab, \(tab\) => \{ if \(tab === props\.name\) opened\.value = true \}/)
    expect(cloudflare).toMatch(/if \(hasCloudflareSetup\.value && enabled\.value && zones\.value\.length === 0/)
    expect(cloudflare).toMatch(/v-if="metaLoaded && !hasCloudflareSetup" class="setup-warning"/)
    expect(read('plugin-components', 'ComposerSiteTab.vue')).toMatch(/<el-tab-pane v-if="site\?\.phpVersion" :name="name" lazy>/)
    expect(read('components', 'pages', 'SiteEdit.vue')).toMatch(/:context="\{ domain, site, redirectHttps, activeTab \}"/)
  })

  test('the Composer tab asks for outdated packages only for a project, and its empty state has a visible Init button', () => {
    const composer = read('components', 'pages', 'SiteComposer.vue')
    expect(composer).toMatch(/if \(status\.value\.hasComposerJson\) \{[\s\S]*?await loadOutdated\(\)/)
    // el-empty has no "extra" slot; content in it is dropped.
    expect(composer).not.toMatch(/<template #extra>/)
    const endpoints = readFileSync(
      resolve(srcRoot, '..', '..', 'daemon', 'NKS.WebDevConsole.Daemon', 'Sites', 'ComposerEndpoints.cs'), 'utf-8')
    expect(endpoints).toMatch(/OutdatedHasNothingToCheck\(composerRoot, available\)/)
  })

  test('i18n keys of the new states exist in both locales', () => {
    const get = (o: Record<string, unknown>, path: string) =>
      path.split('.').reduce<unknown>((a, k) => (a && typeof a === 'object' ? (a as Record<string, unknown>)[k] : undefined), o)
    const required = [
      'sites.banner.restarting.title', 'sites.banner.stopping.title', 'sites.banner.crashed.detail',
      'sites.new.folderUsed', 'sites.new.httpsOffNeedsCa', 'sites.new.mysqlStopped', 'sites.new.creating',
      'sites.list.openWaiting', 'siteApply.applyHint', 'siteEdit.ov.explain.waitingBusy', 'siteEdit.folderMissing',
    ]
    for (const locale of ['cs', 'en']) {
      const json = JSON.parse(readFileSync(join(srcRoot, 'locales', `${locale}.json`), 'utf-8')) as Record<string, unknown>
      for (const key of required) expect(get(json, key), `${locale} ${key}`).toBeTruthy()
    }
  })
})
