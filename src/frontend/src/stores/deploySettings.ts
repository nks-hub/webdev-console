import { defineStore } from 'pinia'
import { ref } from 'vue'
import {
  fetchDeploySettings,
  saveDeploySettings as apiSaveDeploySettings,
  defaultDeploySettings,
  type DeploySettings,
  type DeployHostConfig,
} from '../api/deploy'

const STORAGE_KEY_PREFIX = 'deploy_settings_'

/**
 * Pinia store for per-site deploy settings.
 *
 * Persistence strategy (two-phase):
 *   Phase 6.2 (current): settings are persisted to localStorage, keyed by
 *   domain. On load the store first attempts the backend endpoint; a 404
 *   (expected until Phase 6.3) falls back to localStorage, then to hardcoded
 *   defaults. On save the store attempts the backend PUT; on 404 it falls back
 *   to localStorage and surfaces an informational toast to the caller via a
 *   thrown Error with the message "PENDING_BACKEND".
 *
 *   Phase 6.3 (future): swap the localStorage read/write paths for the HTTP
 *   calls — the HTTP helpers already exist in api/deploy.ts, so this is a
 *   one-line change per action.
 */
export const useDeploySettingsStore = defineStore('deploySettings', () => {
  const settingsByDomain = ref<Map<string, DeploySettings>>(new Map())

  function readFromStorage(domain: string): DeploySettings | null {
    try {
      const raw = localStorage.getItem(`${STORAGE_KEY_PREFIX}${domain}`)
      if (raw) return JSON.parse(raw) as DeploySettings
    } catch { /* ignore corrupt storage */ }
    return null
  }

  function writeToStorage(domain: string, settings: DeploySettings): void {
    try {
      localStorage.setItem(`${STORAGE_KEY_PREFIX}${domain}`, JSON.stringify(settings))
    } catch { /* ignore quota errors */ }
  }

  async function loadForDomain(domain: string): Promise<DeploySettings> {
    // Try the backend endpoint (returns defaults on 404 — see api/deploy.ts).
    // If it returns something other than the bare defaults we trust the backend.
    const fromApi = await fetchDeploySettings(domain)
    const hasRealData = fromApi.hosts.length > 0 || fromApi.hooks.length > 0

    let settings: DeploySettings
    if (hasRealData) {
      settings = fromApi
    } else {
      // Backend returned bare defaults — prefer localStorage if present.
      settings = readFromStorage(domain) ?? fromApi
    }

    settingsByDomain.value.set(domain, settings)
    settingsByDomain.value = new Map(settingsByDomain.value)
    return settings
  }

  /**
   * Save settings. On 404 (backend not yet wired) falls back to localStorage
   * and throws Error("PENDING_BACKEND") so the caller can show a toast.
   */
  async function save(domain: string, settings: DeploySettings): Promise<void> {
    settingsByDomain.value.set(domain, settings)
    settingsByDomain.value = new Map(settingsByDomain.value)

    try {
      await apiSaveDeploySettings(domain, settings)
      writeToStorage(domain, settings)
    } catch (e) {
      const msg = e instanceof Error ? e.message : ''
      if (msg.includes('404') || msg.includes('HTTP 404')) {
        // Phase 6.2: backend not yet wired — persist locally.
        writeToStorage(domain, settings)
        throw new Error('PENDING_BACKEND')
      }
      throw e
    }
  }

  function getForDomain(domain: string): DeploySettings {
    return settingsByDomain.value.get(domain) ?? defaultDeploySettings()
  }

  function addHost(domain: string, host: DeployHostConfig): void {
    const s = getForDomain(domain)
    s.hosts = [...s.hosts, host]
    settingsByDomain.value.set(domain, { ...s })
    settingsByDomain.value = new Map(settingsByDomain.value)
  }

  function updateHost(domain: string, hostName: string, patch: Partial<DeployHostConfig>): void {
    const s = getForDomain(domain)
    s.hosts = s.hosts.map(h => h.name === hostName ? { ...h, ...patch } : h)
    settingsByDomain.value.set(domain, { ...s })
    settingsByDomain.value = new Map(settingsByDomain.value)
  }

  function removeHost(domain: string, hostName: string): void {
    const s = getForDomain(domain)
    s.hosts = s.hosts.filter(h => h.name !== hostName)
    settingsByDomain.value.set(domain, { ...s })
    settingsByDomain.value = new Map(settingsByDomain.value)
  }

  return {
    settingsByDomain,
    loadForDomain,
    save,
    getForDomain,
    addHost,
    updateHost,
    removeHost,
  }
})
