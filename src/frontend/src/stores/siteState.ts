import { defineStore } from 'pinia'
import { computed, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import { i18n } from '../i18n'
import {
  fetchLatestBinary, fetchOnboardingState, fetchPhpVersions, fetchSettings, installBinary, installSslCa, startService,
  type OnboardingState,
} from '../api/daemon'
import type { SiteInfo } from '../api/types'
import { errorMessage } from '../utils/errors'
import { phpLabel } from '../utils/siteUrl'
import { useDaemonStore } from './daemon'
import { usePluginsStore } from './plugins'
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

/** State of the web server that every site depends on. */
export type WebServerKind = 'running' | 'starting' | 'stopped' | 'missing' | 'unknown'

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
  const t = (key: string, named?: Record<string, unknown>) => i18n.global.t(key, named ?? {}) as string

  const prerequisites = ref<OnboardingState['prerequisites'] | null>(null)
  /** null until the first successful load, so "PHP missing" is never claimed on a failed request. */
  const phpVersions = ref<InstalledPhp[] | null>(null)
  const starting = ref(false)
  const startError = ref('')
  const installing = ref<Set<PrerequisiteApp>>(new Set())

  const apacheService = computed(() => daemon.services.find(s => s.id === 'apache' || s.id === 'httpd'))

  const webServer = computed<WebServerKind>(() => {
    const svc = apacheService.value
    if (svc && (svc.state === 2 || svc.status === 'running')) return 'running'
    if (starting.value || svc?.state === 1) return 'starting'
    if (prerequisites.value?.apacheInstalled === false) return 'missing'
    return svc ? 'stopped' : 'unknown'
  })

  // A running web server makes an earlier failed start irrelevant.
  watch(webServer, (ws) => { if (ws === 'running') startError.value = '' })

  const cloudflareActive = computed(() =>
    plugins.manifests.some(m => m.id === 'nks.wdc.cloudflare' && m.enabled))

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

  const refresh = () => Promise.all([loadPrerequisites(), loadPhpVersions()])

  function stateOf(site: SiteInfo): SiteState {
    if (site.enabled === false) return { kind: 'disabled' }
    if (apply.applying.has(site.domain)) return { kind: 'applying' }
    const failed = apply.results[site.domain]?.steps.find(s => s.status === 'failed')
    if (failed) return { kind: 'failed', step: failed.name, detail: failed.detail ?? undefined }
    const ws = webServer.value
    return ws === 'stopped' || ws === 'missing' || ws === 'starting' ? { kind: 'waiting' } : { kind: 'running' }
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

  /** Starts Apache and reports a failure to the user instead of swallowing it. */
  async function startWebServer(): Promise<boolean> {
    starting.value = true
    startError.value = ''
    try {
      const r = await startService(apacheService.value?.id ?? 'apache')
      if (r && r.ok === false) throw new Error(r.message)
      return true
    } catch (e) {
      startError.value = errorMessage(e)
      ElMessage.error(t('toast.startFailed', { err: startError.value }))
      return false
    } finally {
      starting.value = false
      await daemon.poll()
      void loadPrerequisites()
    }
  }

  async function latestVersion(app: string): Promise<string> {
    const latest = await fetchLatestBinary(app)
    if (!latest?.version) throw new Error(t('toast.noBinaryForPlatform', { app }))
    return latest.version
  }

  /** Installs one prerequisite the way the first-run wizard does. */
  async function install(app: PrerequisiteApp): Promise<boolean> {
    installing.value = new Set(installing.value).add(app)
    try {
      if (app === 'https') {
        if (!prerequisites.value?.mkcertBinaryInstalled) await installBinary('mkcert', await latestVersion('mkcert'))
        await installSslCa()
        ElMessage.success(t('ssl.caTrusted'))
      } else {
        const version = await latestVersion(app)
        await installBinary(app, version)
        ElMessage.success(t('toast.installedVersion', { app, version }))
      }
      return true
    } catch (e) {
      ElMessage.error(t('toast.installFailed', { err: errorMessage(e) }))
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
    webServer, cloudflareActive,
    refresh, loadPrerequisites, loadPhpVersions, stateOf, stepLabel, runtimeOf, startWebServer, install,
  }
})
