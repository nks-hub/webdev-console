import { defineStore } from 'pinia'
import { ref, computed } from 'vue'
import { fetchPlugins, fetchPluginUi, fetchPluginNavEntries, enablePlugin, disablePlugin, type PluginNavEntry } from '../api/daemon'
import type { PluginManifest, PluginUiDefinition } from '../api/types'

export const usePluginsStore = defineStore('plugins', () => {
  const manifests = ref<PluginManifest[]>([])
  const uiDefinitions = ref<Map<string, PluginUiDefinition>>(new Map())
  const navEntries = ref<PluginNavEntry[]>([])
  const loading = ref(false)

  const enabledPlugins = computed(() => manifests.value.filter(p => p.enabled))

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

  return { manifests, uiDefinitions, navEntries, toolsNavEntries, loading, enabledPlugins, loadAll, toggleEnable, getUi }
})
