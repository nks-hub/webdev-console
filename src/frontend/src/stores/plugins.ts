import { defineStore } from 'pinia'
import { ref, computed } from 'vue'
import { fetchPlugins, fetchPluginUi, fetchPluginNavEntries, enablePlugin, disablePlugin, type PluginNavEntry } from '../api/daemon'
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
  const navEntries = ref<PluginNavEntry[]>([])
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
      // F91: pull aggregated nav contributions in a single round-trip so the
      // sidebar can render without any hardcoded plugin names. Failures are
      // swallowed — the sidebar falls back gracefully if the daemon predates
      // the /api/plugins/ui endpoint.
      try {
        const nav = await fetchPluginNavEntries()
        navEntries.value = (nav?.entries ?? []).slice().sort((a, b) => {
          if (a.category !== b.category) return a.category.localeCompare(b.category)
          if (a.order !== b.order) return a.order - b.order
          return a.label.localeCompare(b.label)
        })
      } catch { /* older daemon, sidebar will render without plugin-contributed entries */ }
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
    // Re-pull aggregator so sidebar toolsNavEntries repaints without a
    // full page reload. Swallows fetch failures so older daemons still
    // get their per-plugin state updated.
    try {
      const nav = await fetchPluginNavEntries()
      navEntries.value = (nav?.entries ?? []).slice().sort((a, b) => {
        if (a.category !== b.category) return a.category.localeCompare(b.category)
        if (a.order !== b.order) return a.order - b.order
        return a.label.localeCompare(b.label)
      })
    } catch { /* already logged in loadAll */ }
  }

  function getUi(id: string): PluginUiDefinition | undefined {
    return uiDefinitions.value.get(id)
  }

  const toolsNavEntries = computed(() => navEntries.value.filter(e => e.category === 'Tools'))

  return { manifests, uiDefinitions, navEntries, toolsNavEntries, loading, enabledPlugins, sidebarCategories, loadAll, toggleEnable, getUi }
})
