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

  // F91.2: generic "UI surface" system. A surface is any piece of UI that a
  // plugin can own — sidebar/header nav, SiteEdit tab, dashboard card,
  // sites-list badge, etc. Surface keys are namespaced strings the plugin
  // itself declares via UiSchemaBuilder, e.g. `nav:/ssl`, `site-tab:ssl`,
  // `dashboard-card:tunnel-status`. The shell queries isUiVisible(key) to
  // decide whether to render the surface, so disabling a plugin removes
  // everything it contributed without any hardcoded per-surface table.

  // Union of every surface declared across ALL manifests (enabled or not) —
  // "is this surface plugin-owned by anyone?".
  const ownedSurfaces = computed<ReadonlySet<string>>(() => {
    const s = new Set<string>()
    for (const m of manifests.value) {
      for (const key of m.uiSurfaces ?? []) s.add(key)
    }
    return s
  })

  // Union of surfaces declared by currently-ENABLED plugins — "is any
  // enabled plugin currently contributing this surface?".
  const activeSurfaces = computed<ReadonlySet<string>>(() => {
    const s = new Set<string>()
    for (const m of manifests.value) {
      if (!m.enabled) continue
      for (const key of m.uiSurfaces ?? []) s.add(key)
    }
    return s
  })

  // True when manifests have been fetched at least once. Used by isUiVisible
  // to fail open before the first /api/plugins round-trip so the UI doesn't
  // flash plugin items off and back on during initial boot.
  const manifestsLoaded = computed(() => manifests.value.length > 0)

  /**
   * F91.2: generic visibility check for any UI surface.
   *  - If the surface is not owned by any plugin → always visible (non-plugin UI).
   *  - If it is plugin-owned → visible only when at least one *enabled* plugin claims it.
   *  - Before manifests are loaded → fail open to avoid flicker.
   *
   * Callers pass the namespaced key directly: `isUiVisible('site-tab:ssl')`,
   * `isUiVisible('nav:/composer')`, `isUiVisible('dashboard-card:tunnel')`.
   */
  function isUiVisible(surfaceKey: string): boolean {
    if (!manifestsLoaded.value) return true
    if (!ownedSurfaces.value.has(surfaceKey)) return true
    return activeSurfaces.value.has(surfaceKey)
  }

  // Convenience: same check but for nav routes (auto-namespaces to `nav:…`).
  // Kept as its own function so the router guard reads naturally.
  function isRouteVisible(route: string): boolean {
    return isUiVisible(`nav:${route}`)
  }

  function isPluginOwnedRoute(route: string): boolean {
    return ownedSurfaces.value.has(`nav:${route}`)
  }

  /**
   * F91.3: resolve the set of service IDs an *enabled* plugin contributes
   * to a given sidebar service section. The plugin declared both
   * `service-section:{category}` AND `service-row:{category}:{serviceId}`
   * via UiSchemaBuilder.SetServiceCategory — we pick out the row surfaces
   * that match the category prefix. The returned set is reactive, so the
   * sidebar re-filters automatically when plugins toggle on/off.
   */
  function serviceIdsInCategory(category: string): ReadonlySet<string> {
    const prefix = `service-row:${category}:`
    const ids = new Set<string>()
    for (const key of activeSurfaces.value) {
      if (key.startsWith(prefix)) ids.add(key.slice(prefix.length))
    }
    return ids
  }

  /**
   * F91.3: the full set of service IDs currently exposed by ANY enabled
   * plugin, across all categories. Callers (Dashboard tiles, counters,
   * "start all" targets) use this to filter daemonStore.services so a
   * disabled plugin's service never shows up, even if the daemon still
   * lists it in /api/services. Fail-open before manifests load.
   */
  const activeServiceIds = computed<ReadonlySet<string>>(() => {
    const ids = new Set<string>()
    for (const key of activeSurfaces.value) {
      if (!key.startsWith('service-row:')) continue
      const idx = key.indexOf(':', 'service-row:'.length)
      if (idx < 0) continue
      ids.add(key.slice(idx + 1))
    }
    return ids
  })

  // True when the given service row is contributed by any enabled plugin.
  // Before manifest load → fail-open so initial render isn't blank.
  function isServiceVisible(serviceId: string): boolean {
    if (!manifestsLoaded.value) return true
    // If no plugin claims this service id via SetServiceCategory, leave it
    // visible — keeps backward-compat with services that predate F91.3.
    let anyPluginClaimsIt = false
    for (const m of manifests.value) {
      for (const key of m.uiSurfaces ?? []) {
        if (key.startsWith(`service-row:`) && key.endsWith(`:${serviceId}`)) {
          anyPluginClaimsIt = true
          break
        }
      }
      if (anyPluginClaimsIt) break
    }
    if (!anyPluginClaimsIt) return true
    return activeServiceIds.value.has(serviceId)
  }

  return {
    manifests, uiDefinitions, navEntries, toolsNavEntries,
    loading, navEntriesLoaded, enabledPlugins,
    ownedSurfaces, activeSurfaces, activeServiceIds,
    loadAll, toggleEnable, getUi,
    isUiVisible, isRouteVisible, isPluginOwnedRoute,
    serviceIdsInCategory, isServiceVisible,
  }
})
