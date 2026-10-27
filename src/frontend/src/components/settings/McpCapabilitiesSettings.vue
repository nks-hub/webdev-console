<template>
  <div class="mcp-caps">
    <p class="caps-intro">{{ t('mcpCapabilities.intro') }}</p>

    <p v-if="loadError" class="caps-error" role="alert">
      {{ t('mcpCapabilities.loadFailed', { err: loadError }) }}
      <el-button size="small" @click="load">{{ t('common.retry') }}</el-button>
    </p>

    <div v-else-if="loading && !catalog" class="caps-skeleton" aria-busy="true">
      <div v-for="n in 5" :key="n" class="skel-row" />
    </div>

    <template v-else-if="catalog">
      <ul class="caps-list">
        <li v-for="cap in catalog.capabilities" :key="cap.id" class="cap-row">
          <div class="cap-text">
            <span :id="`cap-${cap.id}`" class="cap-title">{{ capTitle(cap) }}</span>
            <span class="cap-desc">{{ capDescription(cap) }}</span>
            <span v-if="needsApproval(cap)" class="cap-note">
              <el-icon :size="12" aria-hidden="true"><Lock /></el-icon>
              {{ t('mcpCapabilities.approvalStillRequired') }}
            </span>
            <span v-if="cap.tools?.length" class="cap-tools mono">{{ cap.tools.join(' · ') }}</span>
          </div>
          <el-switch
            :model-value="cap.enabled"
            :loading="saving.has(cap.id)"
            :aria-labelledby="`cap-${cap.id}`"
            @change="(v: string | number | boolean) => toggle(cap, Boolean(v))"
          />
        </li>
      </ul>

      <details class="caps-more">
        <summary>{{ t('mcpCapabilities.alwaysTitle', { n: catalog.alwaysAllowed.length }) }}</summary>
        <p class="caps-more-hint">{{ t('mcpCapabilities.alwaysHint') }}</p>
        <ul class="route-list mono">
          <li v-for="r in catalog.alwaysAllowed" :key="r.method + r.pattern">{{ r.method }} {{ r.pattern }}</li>
        </ul>
      </details>
      <details class="caps-more">
        <summary>{{ t('mcpCapabilities.neverTitle') }}</summary>
        <p class="caps-more-hint">{{ t('mcpCapabilities.neverHint') }}</p>
        <ul class="route-list mono">
          <li v-for="r in catalog.neverAllowed" :key="r.method + r.pattern">{{ r.method }} {{ r.pattern }}</li>
          <li>{{ t('mcpCapabilities.settingsPrefix', { prefix: catalog.neverAllowedSettings }) }}</li>
        </ul>
      </details>
    </template>
  </div>
</template>

<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Lock } from '@element-plus/icons-vue'
import { daemonBaseUrl, daemonAuthHeaders, saveSettings } from '../../api/daemon'
import { errorMessage } from '../../utils/errors'

/**
 * Operator switches for what AI clients connected over MCP may do.
 * The daemon owns the catalog (GET /api/mcp/capabilities): every group,
 * its routes and tools, and whether it is on. Only the GUI token can
 * flip `mcp.capabilities.*`; the MCP token is refused by the daemon.
 * Destructive calls inside an enabled group still need a human-approved
 * intent or a persistent grant.
 */
interface RouteRef { method: string; pattern: string; intentKind?: string | null; intentBy?: string | null }
interface Capability {
  id: string
  settingKey: string
  title: string
  description: string
  enabled: boolean
  defaultEnabled: boolean
  routes: RouteRef[]
  bodyFields?: Array<{ method: string; pattern: string; fields: string[]; intentKind?: string | null }>
  tools?: string[]
}
interface Catalog {
  settingPrefix: string
  capabilities: Capability[]
  alwaysAllowed: RouteRef[]
  neverAllowed: RouteRef[]
  neverAllowedSettings: string
}

const { t, te } = useI18n()
const catalog = ref<Catalog | null>(null)
const loading = ref(false)
const loadError = ref('')
const saving = ref<Set<string>>(new Set())

async function load() {
  loading.value = true
  loadError.value = ''
  try {
    const r = await fetch(`${daemonBaseUrl()}/api/mcp/capabilities`, { headers: daemonAuthHeaders() })
    if (!r.ok) throw new Error(`HTTP ${r.status}`)
    catalog.value = await r.json() as Catalog
  } catch (e) {
    loadError.value = errorMessage(e)
  } finally {
    loading.value = false
  }
}
onMounted(load)

function capTitle(cap: Capability): string {
  const key = `mcpCapabilities.groups.${cap.id}.title`
  return te(key) ? t(key) : cap.title
}
function capDescription(cap: Capability): string {
  const key = `mcpCapabilities.groups.${cap.id}.description`
  return te(key) ? t(key) : cap.description
}
function needsApproval(cap: Capability): boolean {
  return cap.routes.some(r => !!r.intentKind) || (cap.bodyFields ?? []).some(b => !!b.intentKind)
}

async function toggle(cap: Capability, enabled: boolean) {
  if (enabled) {
    try {
      await ElMessageBox.confirm(
        t('mcpCapabilities.enableConfirm', { name: capTitle(cap) }),
        t('mcpCapabilities.enableTitle'),
        { type: 'warning', confirmButtonText: t('mcpCapabilities.enableButton'), cancelButtonText: t('common.cancel') },
      )
    } catch { return }
  }
  saving.value = new Set(saving.value).add(cap.id)
  try {
    await saveSettings({ [cap.settingKey]: String(enabled) })
    cap.enabled = enabled
    ElMessage.success(enabled
      ? t('mcpCapabilities.enabled', { name: capTitle(cap) })
      : t('mcpCapabilities.disabled', { name: capTitle(cap) }))
  } catch (e) {
    ElMessage.error(t('toast.saveFailed', { err: errorMessage(e) }))
  } finally {
    const next = new Set(saving.value)
    next.delete(cap.id)
    saving.value = next
  }
}
</script>

<style scoped>
.mcp-caps { display: grid; gap: var(--wdc-space-4); max-width: 72ch; }
.caps-intro { color: var(--wdc-text-2); }
.caps-error { display: flex; align-items: center; gap: var(--wdc-space-3); color: var(--wdc-status-error); }

.caps-list { list-style: none; margin: 0; padding: 0; border-top: 1px solid var(--wdc-border); }
.cap-row {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto;
  gap: var(--wdc-space-4);
  align-items: start;
  padding: var(--wdc-space-3) 0;
  border-bottom: 1px solid var(--wdc-border);
}
.cap-text { display: grid; gap: var(--wdc-space-1); min-width: 0; }
.cap-title { font-family: var(--wdc-font-display); font-weight: 500; color: var(--wdc-text); }
.cap-desc { font-size: 0.87rem; color: var(--wdc-text-2); }
.cap-note {
  display: inline-flex;
  align-items: center;
  gap: var(--wdc-space-1);
  font-size: 0.78rem;
  color: var(--wdc-text-3);
}
.cap-tools { font-size: 0.74rem; color: var(--wdc-text-3); overflow-wrap: anywhere; }

.caps-more summary {
  cursor: pointer;
  font-family: var(--wdc-font-display);
  font-weight: 500;
  color: var(--wdc-text-2);
  padding: var(--wdc-space-1) 0;
}
.caps-more summary:hover { color: var(--wdc-text); }
.caps-more-hint { margin: var(--wdc-space-1) 0 var(--wdc-space-2); font-size: 0.87rem; color: var(--wdc-text-3); }
.route-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: grid;
  gap: var(--wdc-space-0-5);
  font-size: 0.78rem;
  color: var(--wdc-text-2);
}

.skel-row {
  height: 56px;
  border-bottom: 1px solid var(--wdc-border);
  background: linear-gradient(90deg, var(--wdc-surface), var(--wdc-surface-2), var(--wdc-surface));
  background-size: 200% 100%;
  animation: skel 1.4s ease-in-out infinite;
}
@keyframes skel { from { background-position: 100% 0; } to { background-position: -100% 0; } }
</style>
