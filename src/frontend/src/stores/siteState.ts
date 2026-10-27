import { defineStore } from 'pinia'
import { computed, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import { i18n } from '../i18n'
import {
  fetchCloudflareConfig, fetchLatestBinary, fetchOnboardingState, fetchPhpVersions, fetchSettings, installBinary,
  installSslCa, type OnboardingState,
} from '../api/daemon'
import type { SiteInfo } from '../api/types'
import { appDisplayName, installErrorMessage } from '../utils/binaries'
import { serviceViewState } from '../utils/serviceState'
import { phpLabel } from '../utils/siteUrl'
import { useDaemonStore } from './daemon'
import { usePluginsStore } from './plugins'
import { useServicesStore } from './services'
import { useSiteApplyStore } from './siteApply'

/** What a site is doing right now. The one definition shared by the list, the detail header and the overview. */
export type SiteStateKind = 'running' | 'waiting' | 'failed' | 'applying' | 'disabled'
export interface SiteState {
  kind: SiteStateKind
  /** Name of the first failed apply step (failed only). */
  step?: string
  /** Raw detail of that step from the daemon (failed only). */
  detail?: string
}

/**
 * State of the web server that every site depends on. `restarting` is the
 * short gap while a site is being saved (the daemon reloads Apache by stopping
 * and starting it), `crashed` a web server that died or was switched off.
 */
export type WebServerKind =
  | 'running' | 'starting' | 'stopping' | 'restarting' | 'stopped' | 'crashed' | 'missing' | 'unknown'

/** The database server a new site's database would be created on. */
export type DatabaseServerKind = 'running' | 'starting' | 'stopped' | 'missing' | 'unknown'

export type PrerequisiteApp = 'apache' | 'php' | 'mysql' | 'https'

export interface InstalledPhp {
  /** major.minor, as stored in the site config. */
  value: string
  full: string
  /** The version a new site starts with: the Settings choice when installed, else the daemon's active one. */
  isDefault: boolean
}

const majorMinor = (v: string) => v.split('.').slice(0, 2).join('.')

export const useSiteStateStore = defineStore('siteState', () => {
  const daemon = useDaemonStore()
  const plugins = usePluginsStore()
  const apply = useSiteApplyStore()
  const services = useServicesStore()
  const t = (key: string, named?: Record<string, unknown>) => i18n.global.t(key, named ?? {}) as string

  const prerequisites = ref<OnboardingState['prerequisites'] | null>(null)
  /** null until the first successful load, so "PHP missing" is never claimed on a failed request. */
  const phpVersions = ref<InstalledPhp[] | null>(null)
  const starting = ref(false)
  const installing = ref<Set<PrerequisiteApp>>(new Set())

  /**
   * The daemon also lists the services of plugins that are switched off (MariaDB next to MySQL), as
   * stopped. The one that counts is a visible one, running first, installed second.
   */
  function pickService(ids: string[]) {
    const list = daemon.services.filter(s => ids.includes(s.id) && plugins.isServiceVisible(s.id))
    return list.find(s => serviceViewState(s) === 'running') ?? list.find(s => s.installed !== false) ?? list[0]
  }

  const apacheService = computed(() => pickService(['apache', 'httpd']))

  /** Apache died before it ever reached "running" (a bad config, a busy port), as opposed to crashing later. */
  const apacheStartFailed = computed(() => apacheService.value?.crashKind === 'startFailed')

  /** Why the last start of Apache failed, from the shared services store (also set by the Services page). */
  const startError = computed(() => services.lastError[apacheService.value?.id ?? 'apache'] ?? '')

  const webServer = computed<WebServerKind>(() => {
    const svc = apacheService.value
    const view = svc ? serviceViewState(svc) : null
    if (view === 'running') return 'running'
    if (view === 'notInstalled' || (!svc && prerequisites.value?.apacheInstalled === false)) return 'missing'
    if (!svc) return 'unknown'
    // Saving a site reloads Apache, so a web server that is down during a save is only restarting.
    if (apply.applying.size > 0) return 'restarting'
    if (starting.value || view === 'starting') return 'starting'
    if (view === 'stopping') return 'stopping'
    return view === 'crashed' || view === 'disabled' ? 'crashed' : 'stopped'
  })

  /** MySQL or MariaDB, whichever the daemon manages. */
  const databaseService = computed(() => pickService(['mysql', 'mariadb']))
  const databaseServer = computed<DatabaseServerKind>(() => {
    const svc = databaseService.value
    const view = svc ? serviceViewState(svc) : null
    if (view === 'running') return 'running'
    if (view === 'notInstalled' || (!svc && prerequisites.value?.mysqlInstalled === false)) return 'missing'
    if (!svc) return 'unknown'
    return view === 'starting' ? 'starting' : 'stopped'
  })

  const cloudflareActive = computed(() =>
    plugins.manifests.some(m => m.id === 'nks.wdc.cloudflare' && m.enabled))

  /** A new site can only get a tunnel when the tunnel and a default zone are set up on the Cloudflare page. */
  const cloudflareReady = ref(false)
  async function loadCloudflareReady() {
    if (!cloudflareActive.value) { cloudflareReady.value = false; return }
    try {
      const cfg = await fetchCloudflareConfig()
      cloudflareReady.value = !!(cfg.tunnelId && cfg.defaultZoneId)
    } catch { cloudflareReady.value = false }
  }

  async function loadPrerequisites() {
    try { prerequisites.value = (await fetchOnboardingState()).prerequisites } catch { /* keep the last known state */ }
  }

  async function loadPhpVersions() {
    try {
      const [list, settings] = await Promise.all([
        fetchPhpVersions(),
        fetchSettings().catch(() => ({} as Record<string, string>)),
      ])
      // Several patch versions of one major.minor collapse into one entry; the active patch wins.
      const byMinor = new Map<string, { value: string; full: string; active: boolean }>()
      for (const v of list) {
        const value = v.majorMinor || majorMinor(v.version)
        const seen = byMinor.get(value)
        if (!seen) byMinor.set(value, { value, full: v.version, active: !!v.isActive })
        else if (v.isActive) Object.assign(seen, { full: v.version, active: true })
      }
      const wanted = majorMinor(settings['general.defaultPhp']?.trim() ?? '')
      const preferred = byMinor.has(wanted) ? wanted : [...byMinor.values()].find(v => v.active)?.value
      phpVersions.value = [...byMinor.values()].map(v => ({ value: v.value, full: v.full, isDefault: v.value === preferred }))
    } catch { /* keep the last known list */ }
  }

  const refresh = () => Promise.all([loadPrerequisites(), loadPhpVersions(), loadCloudflareReady()])

  // The checklist ("Apache installed? PHP? HTTPS?") must follow installs done anywhere: a binary being
  // installed or removed changes the services' `installed` flag, so that is the cue to read it again.
  let prerequisitesTimer: ReturnType<typeof setTimeout> | undefined
  watch(() => daemon.services.map(s => `${s.id}:${s.installed}`).join('|'), () => {
    clearTimeout(prerequisitesTimer)
    prerequisitesTimer = setTimeout(() => void refresh(), 400)
  })

  function stateOf(site: SiteInfo): SiteState {
    if (site.enabled === false) return { kind: 'disabled' }
    if (apply.applying.has(site.domain)) return { kind: 'applying' }
    const failed = apply.results[site.domain]?.steps.find(s => s.status === 'failed')
    if (failed) return { kind: 'failed', step: failed.name, detail: failed.detail ?? undefined }
    return webServer.value === 'running' || webServer.value === 'unknown' ? { kind: 'running' } : { kind: 'waiting' }
  }

  /** Czech/English name of an apply step ("Certifikát", "Virtual host", …). */
  function stepLabel(name: string): string {
    const key = `siteApply.steps.${name}`
    return i18n.global.te(key) ? t(key) : name
  }

  /** What the site runs on. `missing` is set for a PHP version that is not installed. */
  function runtimeOf(site: SiteInfo): { kind: 'node' | 'php' | 'static'; label: string; title?: string; missing: boolean } {
    if (site.nodeUpstreamPort && site.nodeUpstreamPort > 0) {
      return { kind: 'node', label: `Node :${site.nodeUpstreamPort}`, missing: false }
    }
    if (!site.phpVersion || site.phpVersion === 'none') {
      return { kind: 'static', label: t('sites.runtimeStatic'), missing: false }
    }
    const wanted = majorMinor(site.phpVersion)
    const installed = phpVersions.value?.find(p => p.value === wanted)
    if (phpVersions.value && !installed) {
      return { kind: 'php', label: phpLabel(wanted), title: t('sites.list.phpMissingTitle', { v: wanted }), missing: true }
    }
    return {
      kind: 'php',
      label: phpLabel(wanted),
      title: installed ? `PHP ${installed.full}${installed.isDefault ? ` · ${t('sites.list.phpDefault')}` : ''}` : undefined,
      missing: false,
    }
  }

  /** Starts Apache; a failure is toasted and its reason kept in the services store for the banner. */
  async function startWebServer(): Promise<boolean> {
    starting.value = true
    try {
      return await services.act(apacheService.value?.id ?? 'apache', 'start', 'Apache')
    } finally {
      starting.value = false
      void loadPrerequisites()
    }
  }

  async function startDatabase(): Promise<boolean> {
    const svc = databaseService.value
    return svc ? services.act(svc.id, 'start', svc.id === 'mariadb' ? 'MariaDB' : 'MySQL') : false
  }

  async function latestVersion(app: string): Promise<string> {
    const latest = await fetchLatestBinary(app)
    if (!latest?.version) throw new Error(t('toast.noBinaryForPlatform', { app }))
    return latest.version
  }

  /** Installs one prerequisite the way the first-run wizard does. */
  async function install(app: PrerequisiteApp): Promise<boolean> {
    installing.value = new Set(installing.value).add(app)
    const binary = app === 'https' ? 'mkcert' : app
    let version = ''
    try {
      if (app === 'https') {
        if (!prerequisites.value?.mkcertBinaryInstalled) {
          version = await latestVersion(binary)
          await installBinary(binary, version)
        }
        await installSslCa()
        ElMessage.success(t('ssl.caTrusted'))
      } else {
        version = await latestVersion(binary)
        await installBinary(binary, version)
        ElMessage.success(t('toast.installedVersion', { app: appDisplayName(binary), version }))
      }
      return true
    } catch (e) {
      ElMessage.error(installErrorMessage(t, e, binary, version))
      return false
    } finally {
      const next = new Set(installing.value)
      next.delete(app)
      installing.value = next
      await Promise.all([daemon.poll(), refresh()])
    }
  }

  return {
    prerequisites, phpVersions, starting, startError, installing,
    webServer, apacheStartFailed, databaseServer, cloudflareActive, cloudflareReady,
    refresh, loadPrerequisites, loadPhpVersions, stateOf, stepLabel, runtimeOf, startWebServer, startDatabase, install,
  }
})
