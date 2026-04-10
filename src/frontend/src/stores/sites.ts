import { defineStore } from 'pinia'
import { ref } from 'vue'
import { fetchSites, createSite, deleteSite, updateSite } from '../api/daemon'
import type { SiteInfo } from '../api/types'

export const useSitesStore = defineStore('sites', () => {
  const sites = ref<SiteInfo[]>([])
  const loading = ref(false)

  async function load() {
    loading.value = true
    try { sites.value = await fetchSites() } finally { loading.value = false }
  }

  async function create(data: Partial<SiteInfo>) {
    const site = await createSite(data)
    sites.value.push(site)
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

  return { sites, loading, load, create, update, remove }
})
