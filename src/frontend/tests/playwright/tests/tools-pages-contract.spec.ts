import { test, expect } from './_fixtures'
import { readFileSync } from 'node:fs'
import { join, resolve } from 'node:path'

// Source contract for the tool pages (Binaries, Hosts, SSL, Databases, PHP,
// Plugins, Composer, Cloudflare): the daemon/UI agreements that a headless
// render cannot reach without a running daemon.

const srcRoot = resolve(process.cwd(), 'src')
const daemonRoot = resolve(process.cwd(), '..', 'daemon', 'NKS.WebDevConsole.Daemon')
const page = (name: string) => readFileSync(join(srcRoot, 'components', 'pages', name), 'utf-8')
const daemon = (path: string) => readFileSync(join(daemonRoot, path), 'utf-8')

test.describe('Tool pages — source contract', () => {
  test('binary install streams real progress and can be cancelled', () => {
    const endpoint = daemon('Binaries/BinaryEndpoints.cs')
    expect(endpoint).toContain('new BinaryInstallProgress(sse.BroadcastAsync)')
    expect(endpoint).toContain('"binary:progress"')
    const vue = page('Binaries.vue')
    expect(vue).toContain("'binary:progress'")
    expect(vue).toMatch(/installBinary\(app, version, installAbort\.signal\)/)
    expect(vue).not.toMatch(/progressPercent\.value = (10|30|90)\b/)
  })

  test('binary install errors are worded from the daemon code, not raw HTTP text', () => {
    expect(daemon('Binaries/BinaryEndpoints.cs')).toMatch(/new \{ error = ex\.Message, code,/)
    const util = readFileSync(join(srcRoot, 'utils', 'binaries.ts'), 'utf-8')
    for (const code of ['download_failed', 'not_an_archive', 'sha_mismatch', 'no_build_for_platform']) {
      expect(util).toContain(code)
    }
    expect(page('Binaries.vue')).toContain('installErrorMessage(')
  })

  test('hosts rows outside the WDC block are read-only and never reported as saved', () => {
    const vue = page('HostsManager.vue')
    expect(vue).toMatch(/return row\.source === 'wdc' \|\| row\._added/)
    expect(vue).toContain(':disabled="!isEditable(row)"')
    expect(vue).toMatch(/lineNumber: r\._added \? null : r\.lineNumber/)
    // Success toast only when nothing was left unwritten.
    expect(vue).toMatch(/if \(res\?\.notApplied\) \{[\s\S]*?\} else \{\s*ElMessage\.success/)
    expect(vue).toContain(":aria-label=\"$t('hosts.removeEntryAria'")
  })

  test('SSL status comes from the machine, not from the plugin being loaded', () => {
    const endpoint = daemon('Ssl/SslEndpoints.cs')
    expect(endpoint).not.toContain('mkcertInstalled = true')
    expect(endpoint).toContain('bm.ListInstalled("mkcert")')
    expect(endpoint).toContain('IsCaTrusted(MkcertCaRoot())')
    const vue = page('SslManager.vue')
    expect(vue).toMatch(/v-if="mkcertInstalled && !caInstalled/)
    expect(vue).toContain('installMkcert')
    expect(vue).toContain('<PluginMissingState')
  })

  test('plugin cards show only real data and accept a marketplace deep link', () => {
    const store = readFileSync(join(srcRoot, 'stores', 'plugins.ts'), 'utf-8')
    expect(store).not.toMatch(/network: true, process: true, gui: true/)
    const vue = page('PluginManager.vue')
    expect(vue).not.toContain('plugin.permissions')
    expect(vue).toMatch(/route\.query\.tab === 'marketplace'/)
    expect(vue).toContain('missingBinary(plugin)')
    expect(readFileSync(join(srcRoot, 'components', 'shared', 'PluginMissingState.vue'), 'utf-8'))
      .toMatch(/query: \{ tab: 'marketplace', q: props\.pluginId \}/)
  })

  test('composer page names a missing plugin instead of looping on install', () => {
    const vue = page('ComposerManager.vue')
    expect(vue).toContain('plugin-id="nks.wdc.composer"')
    expect(vue).toMatch(/if \(!composerPath\.value && body\.path\) composerPath\.value = body\.path/)
    expect(vue).not.toContain('label="composer.json"')
  })

  test('cloudflare page shows one plugin-missing state on 404 and no dead code', () => {
    const vue = page('CloudflareTunnel.vue')
    expect(vue).toMatch(/e instanceof DaemonError && e\.status === 404\) pluginMissing\.value = true/)
    expect(vue).toContain('plugin-id="nks.wdc.cloudflare"')
    expect(vue).not.toMatch(/ElMessage\.error\(pageLoadError/)
    for (const dead of ['function saveConfig', 'function verifyToken', 'function loadTunnels', 'function applyIngress', 'tunnelTokenInput']) {
      expect(vue).not.toContain(dead)
    }
    expect(vue).toContain(':disabled="!apiTokenInput"')
  })

  test('cloudflare page sets the default zone a tunnelled new site needs', () => {
    // siteState.cloudflareReady requires defaultZoneId and nothing else in the UI sets it.
    const vue = page('CloudflareTunnel.vue')
    expect(vue).toContain('v-model="config.defaultZoneId"')
    expect(vue).toContain('saveCloudflareConfig({ defaultZoneId: zoneId || null })')
    expect(vue).toContain("const DEFAULT_SUBDOMAIN_TEMPLATE = '{stem}-{hash}'")
    expect(vue).toContain('<code>{hash}</code>')
  })

  test('plugin marketplace reads the plugin catalog, not a feed that does not exist', () => {
    const endpoint = daemon('Plugin/PluginEndpoints.cs')
    expect(endpoint).not.toContain('"https://wdc.nks-hub.cz/plugins.json"')
    expect(endpoint).toMatch(/CatalogMarketplace\(pluginCatalog\.Cached, installed\)/)
    expect(endpoint).toContain('source = "catalog"')
  })

  test('databases page turns daemon reasons into a next step', () => {
    const endpoint = daemon('Data/DatabaseEndpoints.cs')
    for (const reason of ['mysql_not_installed', 'mysql_not_running', 'port_conflict']) {
      expect(endpoint).toContain(`"${reason}"`)
    }
    const vue = page('Databases.vue')
    expect(vue).toContain(`v-if="reason === 'mysql_not_installed'"`)
    expect(vue).toContain(`query: { app: 'mysql' }`)
    expect(vue).toContain(`:disabled="mysqlUnavailable"`)
    expect(vue).toContain(`reason.value === 'mysql_not_installed' || reason.value === 'mysql_not_running'`)
    expect(vue).toContain('startMysql')
    // el-alert's default slot hides `description`, so the hint must live in the slot.
    expect(vue).not.toContain(':description="errorHint"')
    expect(vue).not.toContain('accent-glow')
  })

  test('php page toggles zend extensions by bare name and sets the active version', () => {
    const vue = page('PhpManager.vue')
    expect(vue).not.toContain('[zend] ')
    expect(vue).toContain("zend: match[2] === 'zend_extension'")
    expect(vue).toContain('isActive(ver)')
    const endpoint = daemon('Services/PhpEndpoints.cs')
    expect(endpoint).toContain('app.MapPut("/api/php/default"')
    expect(endpoint).toContain('PatchExtensionLines(')
  })
})
