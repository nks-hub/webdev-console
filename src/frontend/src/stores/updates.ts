import { defineStore } from 'pinia'
import { ref, computed } from 'vue'

/**
 * F96 self-updater badge — background polling against github.com
 * releases/latest for the WDC app itself so the AppHeader can show a
 * subtle "new version available" badge without the user opening
 * Settings → Update. The actual install flow stays in Settings (manual
 * download or electron-updater auto-install when that ships) — this
 * store is strictly a discovery surface.
 *
 * Poll cadence: every 6 hours after first call, or immediately on
 * demand via ``refresh()``. The last checked-at ISO is persisted to
 * localStorage so that restarting the app within 6h doesn't re-check
 * unnecessarily (rate-limit friendly for the public GitHub API, which
 * allows ~60 req/h/IP unauthenticated).
 */
export const useUpdatesStore = defineStore('updates', () => {
  const currentVersion = ref<string>('')
  const latestVersion = ref<string>('')
  const downloadUrl = ref<string>('')
  const releaseUrl = ref<string>('')
  const lastCheckedIso = ref<string>('')
  const loading = ref(false)
  const error = ref<string | null>(null)

  const LS_KEY = 'wdc-updates-last-check'
  const CHECK_INTERVAL_MS = 6 * 60 * 60 * 1000 // 6 hours

  const hasUpdate = computed(() => {
    if (!currentVersion.value || !latestVersion.value) return false
    return compareSemver(latestVersion.value, currentVersion.value) > 0
  })

  function readCurrent(): string {
    // package.json is baked into the asar; surface it via Vite's import.meta.
    // At runtime the Electron main process injects __APP_VERSION__ global
    // when available, otherwise we fall back to the string embedded by vite
    // at build time via define.APP_VERSION.
    return window.__APP_VERSION__
      || import.meta.env.VITE_APP_VERSION
      || ''
  }

  function loadCached(): void {
    try {
      const raw = localStorage.getItem(LS_KEY)
      if (!raw) return
      const data = JSON.parse(raw)
      if (typeof data?.latest === 'string') latestVersion.value = data.latest
      if (typeof data?.downloadUrl === 'string') downloadUrl.value = data.downloadUrl
      if (typeof data?.releaseUrl === 'string') releaseUrl.value = data.releaseUrl
      if (typeof data?.lastCheckedIso === 'string') lastCheckedIso.value = data.lastCheckedIso
    } catch { /* corrupt LS — ignore */ }
  }

  function saveCache(): void {
    try {
      localStorage.setItem(LS_KEY, JSON.stringify({
        latest: latestVersion.value,
        downloadUrl: downloadUrl.value,
        releaseUrl: releaseUrl.value,
        lastCheckedIso: lastCheckedIso.value,
      }))
    } catch { /* quota / private mode — ignore */ }
  }

  async function refresh(force = false): Promise<void> {
    if (!currentVersion.value) currentVersion.value = readCurrent()
    const now = Date.now()
    if (!force && lastCheckedIso.value) {
      const last = Date.parse(lastCheckedIso.value)
      if (Number.isFinite(last) && now - last < CHECK_INTERVAL_MS) return
    }
    loading.value = true
    error.value = null
    try {
      const r = await fetch(
        'https://api.github.com/repos/nks-hub/webdev-console/releases/latest',
        { headers: { Accept: 'application/vnd.github+json' } },
      )
      if (!r.ok) throw new Error(`GitHub ${r.status}`)
      const data = await r.json()
      const tag = String(data?.tag_name ?? '').replace(/^v/, '')
      if (tag) latestVersion.value = tag
      releaseUrl.value = String(data?.html_url ?? '')
      // Pick the Windows setup.exe asset (matching electron-builder output).
      const winAsset = (data?.assets ?? []).find((a: any) =>
        typeof a?.name === 'string' && /setup-x64\.exe$/.test(a.name))
      downloadUrl.value = String(winAsset?.browser_download_url ?? '')
      lastCheckedIso.value = new Date().toISOString()
      saveCache()
    } catch (e: any) {
      error.value = e?.message || String(e)
    } finally {
      loading.value = false
    }
  }

  function startAutoCheck(): void {
    loadCached()
    // Fire once immediately so the badge reflects reality from the first
    // render (subject to the cached-within-6h guard inside refresh()).
    void refresh()
    // Re-check every hour; refresh() itself skips the fetch when cached
    // data is still fresh, so this is cheap.
    setInterval(() => { void refresh() }, 60 * 60 * 1000)
  }

  return {
    currentVersion, latestVersion, downloadUrl, releaseUrl,
    lastCheckedIso, loading, error,
    hasUpdate,
    refresh, startAutoCheck,
  }
})

/** Compare two semver strings. Returns 1 if a > b, -1 if a < b, 0 if equal. */
function compareSemver(a: string, b: string): number {
  const pa = a.split('.').map((x) => parseInt(x, 10) || 0)
  const pb = b.split('.').map((x) => parseInt(x, 10) || 0)
  const len = Math.max(pa.length, pb.length)
  for (let i = 0; i < len; i++) {
    const da = pa[i] ?? 0
    const db = pb[i] ?? 0
    if (da > db) return 1
    if (da < db) return -1
  }
  return 0
}
