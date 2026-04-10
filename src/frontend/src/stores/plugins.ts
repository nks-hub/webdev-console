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
      manifests.value = await fetchPlugins()
      // Load UI definitions for all enabled plugins in parallel
      await Promise.allSettled(
        manifests.value
          .filter(p => p.enabled && p.permissions.gui)
          .map(async p => {
            const ui = await fetchPluginUi(p.id)
            uiDefinitions.value.set(p.id, ui)
          })
      )
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
