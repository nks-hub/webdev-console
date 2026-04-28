import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import { fetchSettings } from '../api/daemon'

/**
 * Phase 6.23 — feature flags hydrated from the daemon's settings store.
 * Centralises booleans like `mcp.enabled` so components don't all
 * fetch settings independently. Default values match the daemon's
 * own defaults so first-render before hydration matches steady state.
 *
 * Hydrated lazily on first ensureLoaded() call (App.vue triggers it
 * during onMounted after the daemon is reachable). Subsequent calls
 * are no-ops; the load() method forces a refetch when settings change.
 */
export const useFeatureFlagsStore = defineStore('featureFlags', () => {
  /** mcp.enabled — when false, hide MCP intent UI and refuse intent endpoints. */
  const mcpEnabled = ref(false)

  /**
   * deploy.enabled — when false, hide Deploy tab in SiteEdit and the
   * deploy plugin's REST endpoints return 404. Defaults TRUE because
   * site management without deploy is the unusual case.
   */
  const deployEnabled = ref(true)

  /** Set true after first successful fetch so guards know they have ground truth. */
  const loaded = ref(false)

  /** Combined flag: AI agent integration surface (banner, inventory page). */
  const showMcpSurface = computed(() => mcpEnabled.value)

  /** Combined flag: deploy subsystem UI (Deploy tab, history, groups, settings). */
  const showDeploySurface = computed(() => deployEnabled.value)

  async function load(): Promise<void> {
    try {
      const s = await fetchSettings() as Record<string, unknown>
      const rawMcp = s['mcp.enabled']
      mcpEnabled.value = rawMcp === true || rawMcp === 'true' || rawMcp === '1'
      // deploy.enabled defaults TRUE — only flip false on explicit "false"/"0"
      const rawDeploy = s['deploy.enabled']
      deployEnabled.value = !(rawDeploy === false || rawDeploy === 'false' || rawDeploy === '0')
    } catch {
      // Daemon flaky — keep defaults (mcp OFF, deploy ON), retry on next manual reload
    } finally {
      loaded.value = true
    }
  }

  async function ensureLoaded(): Promise<void> {
    if (loaded.value) return
    await load()
  }

  return {
    mcpEnabled,
    deployEnabled,
    loaded,
    showMcpSurface,
    showDeploySurface,
    load,
    ensureLoaded,
  }
})
