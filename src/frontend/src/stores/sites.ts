import { defineStore } from 'pinia'
import { ref } from 'vue'
import { fetchSites, createSite, deleteSite, updateSite, daemonAuthHeaders } from '../api/daemon'
import type { SiteInfo } from '../api/types'

export const useSitesStore = defineStore('sites', () => {
  const sites = ref<SiteInfo[]>([])
  const loading = ref(false)
  // Side-channel for the last create() — surfaces daemon's soft warnings
  // (bind IP not on any active NIC) and hints (framework auto-detect)
  // to the caller without changing the create() return type.
  const lastCreateWarnings = ref<string[]>([])
  const lastCreateHints = ref<string[]>([])

  async function load() {
    loading.value = true
    try { sites.value = await fetchSites() } finally { loading.value = false }
  }

  async function create(data: Partial<SiteInfo>) {
    // The daemon may return either the raw SiteConfig (happy path) or a
    // wrapper `{ site, warnings, hints }` when soft validations fire
    // (bind IP not on any active NIC, framework detection hints, etc.).
    // Normalize here so callers always get the SiteInfo and the wrapper
    // payload as a side-channel they can surface to the user.
    const raw = await createSite(data) as unknown as SiteInfo
      | { site: SiteInfo; warnings?: string[]; hints?: string[] }
    const site = (raw as { site?: SiteInfo }).site ?? (raw as SiteInfo)
    lastCreateWarnings.value = (raw as { warnings?: string[] }).warnings ?? []
    lastCreateHints.value = (raw as { hints?: string[] }).hints ?? []
    // Optimistic push so the table updates instantly…
    sites.value.push(site)
    // …and an immediate refetch so any daemon-side enrichment (framework
    // detection, resolved phpVersion, SSL cert result) lands in the list
    // without requiring the user to reload. Prior code only pushed the
    // POST response; if that response was missing fields (older daemon
    // builds, partial JSON, or a rename normalisation), the table showed
    // a half-blank row until the user manually refreshed.
    try { sites.value = await fetchSites() } catch { /* keep optimistic state */ }
    return site
  }

  async function update(domain: string, data: Partial<SiteInfo>) {
    const updated = await updateSite(domain, data)
    const idx = sites.value.findIndex(s => s.domain === domain)
    if (idx >= 0) sites.value[idx] = updated
    return updated
  }

  async function remove(domain: string) {
    await deleteSite(domain)
    sites.value = sites.value.filter(s => s.domain !== domain)
  }

  // Delegates to the shared helper so the token resolution stays in one
  // place. Kept as a store method because 5 call-sites already reference
  // it via `sitesStore.authHeaders()` — swapping them all would be a
  // bigger churn than is warranted for this refactor.
  const authHeaders = daemonAuthHeaders

  return {
    sites, loading, load, create, update, remove, authHeaders,
    lastCreateWarnings, lastCreateHints,
  }
})
