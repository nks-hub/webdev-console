<!--
  Shared per-plugin auto-start toggle. Renders as a label + switch pair.
  Used on the plugin card in Plugin Manager AND on every plugin's detail
  page, so "enable auto-start" has exactly one UX pattern no matter
  where the user finds the control.

  Backend contract: reads/writes daemon setting `service.<serviceId>.autoStart`
  (bool, default true). Backend gates every entry on pluginState.IsEnabled,
  so disabling the plugin always wins — no defensive double-check needed
  here.
-->
<template>
  <div
    v-if="eligible"
    class="plugin-autostart"
    :class="{ 'plugin-autostart--compact': compact }"
  >
    <span class="plugin-autostart-label">
      <el-icon class="plugin-autostart-icon"><VideoPlay /></el-icon>
      Auto-start při spuštění WDC
    </span>
    <el-switch
      :model-value="currentValue"
      :size="compact ? 'small' : 'default'"
      :loading="saving"
      @change="(v: string | number | boolean) => toggle(Boolean(v))"
    />
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import { VideoPlay } from '@element-plus/icons-vue'
import { fetchSettings, saveSettings } from '../../api/daemon'
import { errorMessage } from '../../utils/errors'
import { usePluginsStore } from '../../stores/plugins'

const props = defineProps<{
  /** Plugin-like object — backend-resolved serviceId is what drives the toggle.
   *  Prefer this when the caller already has a manifest row handy
   *  (e.g. PluginManager card). */
  plugin?: { enabled: boolean; type?: string; serviceId?: string | null } | null
  /** Alternatively pass a pluginId and we resolve the manifest from the
   *  plugins store. Used by custom plugin detail pages (Apache / MySQL / …)
   *  so each page doesn't have to plumb the manifest itself. */
  pluginId?: string
  /** Compact layout for cramped card footers; default is the roomier
   *  detail-page variant with a bigger switch and clearer spacing. */
  compact?: boolean
}>()

const pluginsStore = usePluginsStore()
const resolvedPlugin = computed(() => {
  if (props.plugin) return props.plugin
  if (!props.pluginId) return null
  return pluginsStore.manifests.find(p => p.id === props.pluginId) ?? null
})

const saving = ref(false)
const currentRaw = ref<boolean | undefined>(undefined)

const eligible = computed(() =>
  resolvedPlugin.value?.enabled === true
  && resolvedPlugin.value?.type === 'service'
  && !!resolvedPlugin.value?.serviceId,
)

// Undefined = never explicitly toggled → treat as on (backend default).
const currentValue = computed(() =>
  currentRaw.value === undefined ? true : currentRaw.value,
)

async function loadCurrent() {
  const sid = resolvedPlugin.value?.serviceId
  if (!sid) return
  try {
    const s = await fetchSettings()
    const v = s[`service.${sid}.autoStart`]
    currentRaw.value = v === undefined ? undefined : v === 'true'
  } catch { /* daemon not reachable — leave default */ }
}

async function toggle(next: boolean) {
  const sid = resolvedPlugin.value?.serviceId
  if (!sid) return
  const prev = currentRaw.value
  currentRaw.value = next
  saving.value = true
  try {
    await saveSettings({ [`service.${sid}.autoStart`]: String(next) })
    ElMessage.success(`${sid}: auto-start ${next ? 'zapnut' : 'vypnut'}`)
  } catch (e) {
    currentRaw.value = prev
    ElMessage.error(`Uložení selhalo: ${errorMessage(e)}`)
  } finally {
    saving.value = false
  }
}

onMounted(() => {
  // If called with pluginId and the store hasn't loaded yet, make sure
  // we have manifests before trying to read serviceId.
  if (props.pluginId && pluginsStore.manifests.length === 0) {
    void pluginsStore.loadAll().then(loadCurrent)
  } else {
    void loadCurrent()
  }
})
watch(() => resolvedPlugin.value?.serviceId, () => { void loadCurrent() })
</script>

<style scoped>
.plugin-autostart {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 10px 12px;
  background: var(--wdc-surface-2, rgba(255, 255, 255, 0.03));
  border: 1px solid var(--wdc-border, rgba(255, 255, 255, 0.08));
  border-radius: 8px;
  margin: 10px 0;
}
.plugin-autostart--compact {
  padding: 6px 10px;
  margin: 8px 0;
  font-size: 0.82rem;
}
.plugin-autostart-label {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  color: var(--wdc-text-2);
  font-weight: 500;
  line-height: 1.2;
}
.plugin-autostart--compact .plugin-autostart-label {
  font-size: 0.82rem;
}
.plugin-autostart-icon {
  color: var(--el-color-primary);
  font-size: 0.95em;
}
</style>
