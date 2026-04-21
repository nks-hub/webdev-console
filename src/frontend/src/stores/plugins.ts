import { defineStore } from 'pinia'
import { ref, computed } from 'vue'
import { fetchPlugins, fetchPluginUi, fetchPluginNavEntries, enablePlugin, disablePlugin, type PluginNavEntry } from '../api/daemon'
import type { PluginManifest, PluginUiDefinition } from '../api/types'

export const usePluginsStore = defineStore('plugins', () => {
  const manifests = ref<PluginManifest[]>([])
  const uiDefinitions = ref<Map<string, PluginUiDefinition>>(new Map())
  const navEntries = ref<PluginNavEntry[]>([])
  const loading = ref(false)
  // F91: tracks whether /api/plugins/ui has returned at least once. Used by
  // isRouteVisible to "fail open" until we actually know which plugins are
  // enabled — otherwise the header /ssl tab flashes off on first mount.
  const navEntriesLoaded = ref(false)

  const enabledPlugins = computed(() => manifests.value.filter(p => p.enabled))

  // Shared sort comparator: group by category (alpha), then by explicit
  // order field, then by label as tiebreaker. Used by both loadAll and
  // toggleEnable so the sidebar stays deterministic after plugin toggles.
  function sortNavEntries(entries: readonly PluginNavEntry[]): PluginNavEntry[] {
    return entries.slice().sort((a, b) => {
      if (a.category !== b.category) return a.category.localeCompare(b.category)
      if (a.order !== b.order) return a.order - b.order
      return a.label.localeCompare(b.label)
    })
  }

  async function refreshNavEntries(): Promise<void> {
    try {
      const nav = await fetchPluginNavEntries()
      navEntries.value = sortNavEntries(nav?.entries ?? [])
      navEntriesLoaded.value = true
    } catch { /* older daemon, sidebar will render without plugin-contributed entries */ }
  }

  async function loadAll() {
    loading.value = true
    try {
      const raw = await fetchPlugins()
      manifests.value = raw.map(p => ({
        ...p,
        permissions: p.permissions ?? { network: true, process: true, gui: true },
      }))
      // Load UI definitions for enabled plugins. Fan out with Promise.all so
      // the 10+ HTTP round-trips run in parallel instead of sequentially —
      // previously turned a fast /api/plugins/{id}/ui probe into N× the
      // slowest probe's latency on initial render.
      const enabled = manifests.value.filter(x => x.enabled)
      await Promise.all(enabled.map(async p => {
        try {
          const ui = await fetchPluginUi(p.id)
          if (ui) {
            uiDefinitions.value.set(p.id, ui)
            if (!p.ui) p.ui = ui
          }
        } catch { /* plugin may not have UI */ }
      }))
      // F91: pull aggregated nav contributions in a single round-trip so the
      // sidebar can render without any hardcoded plugin names.
      await refreshNavEntries()
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
    // Re-pull aggregator so sidebar toolsNavEntries repaints without a full page reload.
    await refreshNavEntries()
  }

  function getUi(id: string): PluginUiDefinition | undefined {
    return uiDefinitions.value.get(id)
  }

  const toolsNavEntries = computed(() => navEntries.value.filter(e => e.category === 'Tools'))

  // F91: routes that are "plugin-owned" — i.e., shipped by a plugin rather
  // than the core shell. Keeping the list static lets the header / router
  // decide "this route belongs to a plugin, is the plugin currently on?"
  // without each caller hardcoding plugin ids. Add a line here when a new
  // plugin ships a nav route.
  const PLUGIN_OWNED_ROUTES: ReadonlySet<string> = new Set([
    '/ssl', '/composer', '/hosts', '/cloudflare',
  ])

  // Routes currently contributed by enabled plugins (reactive). Derived from
  // navEntries which is already filtered server-side to enabled plugins, so
  // this set empties the moment the user toggles a plugin off.
  const activePluginRoutes = computed<ReadonlySet<string>>(() => {
    const s = new Set<string>()
    for (const e of navEntries.value) s.add(e.route)
    return s
  })

  function isPluginOwnedRoute(route: string): boolean {
    return PLUGIN_OWNED_ROUTES.has(route)
  }

  // Returns true when the route is either non-plugin (always allowed) or is
  // owned by a currently-enabled plugin. Callers use this to decide whether
  // to render a nav item / allow a router navigation. Before the first
  // /api/plugins/ui round-trip we fail open so the UI doesn't flash items
  // off and back on during the initial load.
  function isRouteVisible(route: string): boolean {
    if (!PLUGIN_OWNED_ROUTES.has(route)) return true
    if (!navEntriesLoaded.value) return true
    return activePluginRoutes.value.has(route)
  }

  return {
    manifests, uiDefinitions, navEntries, toolsNavEntries,
    loading, navEntriesLoaded, enabledPlugins, activePluginRoutes,
    loadAll, toggleEnable, getUi,
    isPluginOwnedRoute, isRouteVisible,
  }
})
