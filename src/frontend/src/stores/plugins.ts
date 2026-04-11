import { defineStore } from 'pinia'
import { ref, computed } from 'vue'
import { fetchPlugins, fetchPluginUi, enablePlugin, disablePlugin } from '../api/daemon'
import type { PluginManifest, PluginUiDefinition } from '../api/types'
import { useDaemonStore } from './daemon'

export interface SidebarCategory {
  id: string
  label: string
  plugins: Array<{
    id: string
    name: string
    icon: string
    serviceStatus: string
  }>
}

const CATEGORY_LABELS: Record<string, string> = {
  database: 'Databases',
  webserver: 'Web Servers',
  language: 'Languages',
  cache: 'Cache',
  mail: 'Mail',
  // "tools" is the catch-all for plugins that don't fit the other buckets
  // without forcing them into a category where the label would be misleading
  // (e.g. Cloudflare Tunnel doesn't belong under "Cache" or "Mail").
  tools: 'Tools',
  networking: 'Networking',
  other: 'Other',
}

export const usePluginsStore = defineStore('plugins', () => {
  const manifests = ref<PluginManifest[]>([])
  const uiDefinitions = ref<Map<string, PluginUiDefinition>>(new Map())
  const loading = ref(false)

  const enabledPlugins = computed(() => manifests.value.filter(p => p.enabled))

  const sidebarCategories = computed<SidebarCategory[]>(() => {
    const map = new Map<string, SidebarCategory>()

    for (const plugin of enabledPlugins.value) {
      if (!plugin.ui) continue
      const catId = plugin.ui.category
      if (!map.has(catId)) {
        map.set(catId, {
          id: catId,
          label: CATEGORY_LABELS[catId] ?? catId,
          plugins: [],
        })
      }
      map.get(catId)!.plugins.push({
        id: plugin.id,
        name: plugin.name,
        icon: plugin.ui.icon,
        serviceStatus: (() => {
          const daemonStore = useDaemonStore()
          const svc = daemonStore.services.find((s: any) => s.id === plugin.id || s.displayName === plugin.name)
          if (!svc) return 'stopped'
          return svc.state === 2 ? 'running' : svc.state === 4 ? 'error' : 'stopped'
        })(),
      })
    }

    return Array.from(map.values())
  })

  async function loadAll() {
    loading.value = true
    try {
      const raw = await fetchPlugins()
      manifests.value = raw.map(p => ({
        ...p,
        permissions: p.permissions ?? { network: true, process: true, gui: true },
      }))
      // Load UI definitions for enabled plugins
      for (const p of manifests.value.filter(x => x.enabled)) {
        try {
          const ui = await fetchPluginUi(p.id)
          if (ui) {
            uiDefinitions.value.set(p.id, ui)
            if (!p.ui) (p as any).ui = ui
          }
        } catch { /* plugin may not have UI */ }
      }
    } catch (err) {
      console.error('[plugins] loadAll failed:', err)
    } finally {
      loading.value = false
    }
  }

  async function toggleEnable(id: string) {
    const plugin = manifests.value.find(p => p.id === id)
    if (!plugin) return
    if (plugin.enabled) {
      await disablePlugin(id)
      plugin.enabled = false
    } else {
      await enablePlugin(id)
      plugin.enabled = true
      const ui = await fetchPluginUi(id).catch(() => null)
      if (ui) uiDefinitions.value.set(id, ui)
    }
  }

  function getUi(id: string): PluginUiDefinition | undefined {
    return uiDefinitions.value.get(id)
  }

  return { manifests, uiDefinitions, loading, enabledPlugins, sidebarCategories, loadAll, toggleEnable, getUi }
})
