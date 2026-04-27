<template>
  <div class="page mcp-kinds-page">
    <div class="page-header">
      <h2>{{ t('mcpKinds.title') }}</h2>
      <div class="header-actions">
        <!-- Phase 7.5+++ — discoverable shortcut to the always-confirm
             picker in Settings. Per-row chip click does the same thing
             but only renders for already-locked rows; this button is
             visible regardless so an operator can reach the override
             editor from a clean list. -->
        <el-button @click="goToAlwaysConfirmSetting">
          🔒 {{ t('mcpKinds.editAlwaysConfirm') }}
        </el-button>
        <el-button :loading="loading" @click="refresh">
          <el-icon><Refresh /></el-icon> {{ t('mcpKinds.refresh') }}
        </el-button>
      </div>
    </div>

    <p class="muted">{{ t('mcpKinds.description') }}</p>

    <!-- Phase 7.5+++ — summary chips and filter toolbar. Mirrors the
         McpIntents stateCounts pattern: at-a-glance counts by danger
         level + per-plugin count, and a free-text filter that narrows
         the table client-side. -->
    <div v-if="kinds.length > 0" class="kinds-summary" role="group">
      <el-tag v-for="d in dangerCounts" :key="d.level"
        :type="d.level === 'destructive' ? 'danger' : 'warning'" effect="plain" size="small">
        {{ t('mcpKinds.danger.' + d.level) }}: {{ d.count }}
      </el-tag>
      <span class="separator" />
      <el-tag v-for="p in pluginCounts" :key="p.pluginId"
        :type="p.pluginId === 'core' ? 'info' : 'success'" effect="plain" size="small">
        {{ p.pluginId }}: {{ p.count }}
      </el-tag>
    </div>

    <div v-if="kinds.length > 0" class="filter-toolbar">
      <el-input
        v-model="search"
        :placeholder="t('mcpKinds.filterPlaceholder')"
        clearable size="small" class="search-input"
      />
      <el-select v-model="pluginFilter" clearable size="small"
        :placeholder="t('mcpKinds.pluginFilterPlaceholder')" class="plugin-select">
        <el-option v-for="p in pluginCounts" :key="p.pluginId"
          :label="p.pluginId" :value="p.pluginId" />
      </el-select>
      <!-- Phase 7.5+++ — danger-level tri-state filter (All / Reversible /
           Destructive). Mirrors the McpGrants usage filter pattern so
           the operator can isolate the riskiest kinds in one click.
           Counts inline so the breakdown is visible without flipping. -->
      <el-radio-group v-model="dangerFilter" size="small">
        <el-radio-button value="all">
          {{ t('mcpKinds.dangerFilter.all') }} ({{ kinds.length }})
        </el-radio-button>
        <el-radio-button value="reversible">
          {{ t('mcpKinds.danger.reversible') }} ({{ reversibleCount }})
        </el-radio-button>
        <el-radio-button value="destructive">
          {{ t('mcpKinds.danger.destructive') }} ({{ destructiveCount }})
        </el-radio-button>
      </el-radio-group>
    </div>

    <el-alert
      v-if="!loading && kinds.length === 0"
      :title="t('mcpKinds.empty')"
      type="info"
      show-icon
      :closable="false"
    />

    <el-table v-else :data="filteredKinds" stripe size="small" class="kinds-table">
      <el-table-column prop="id" :label="t('mcpKinds.col.id')" min-width="180">
        <template #default="{ row }">
          <code class="mono">{{ row.id }}</code>
        </template>
      </el-table-column>
      <el-table-column prop="label" :label="t('mcpKinds.col.label')" min-width="240" />
      <el-table-column prop="pluginId" :label="t('mcpKinds.col.plugin')" width="160">
        <template #default="{ row }">
          <el-tag :type="row.pluginId === 'core' ? 'info' : 'success'" size="small" effect="plain">
            {{ row.pluginId }}
          </el-tag>
        </template>
      </el-table-column>
      <el-table-column prop="danger" :label="t('mcpKinds.col.danger')" width="140">
        <template #default="{ row }">
          <el-tag
            :type="row.danger === 'destructive' ? 'danger' : 'warning'"
            size="small"
            effect="plain"
          >
            {{ t('mcpKinds.danger.' + row.danger) }}
          </el-tag>
        </template>
      </el-table-column>
      <el-table-column prop="intentCount" :label="t('mcpKinds.col.intentCount')" width="120" sortable>
        <template #default="{ row }">
          <strong v-if="(row.intentCount ?? 0) > 0">{{ row.intentCount }}</strong>
          <span v-else class="muted">{{ t('mcpKinds.unused') }}</span>
        </template>
      </el-table-column>
      <!-- Phase 7.5+++ — auto-approving grants per kind. Counts active
           grants whose kindPattern is '*' or this exact kind id, i.e.
           how many ways an AI could fire this kind without operator
           confirmation. Click → /mcp/grants pre-filtered for the kind.
           When the kind is also in always-confirm, render a 🔒 chip to
           make clear the operator has overridden auto-approval — those
           grants will NOT actually fire for this kind. -->
      <el-table-column :label="t('mcpKinds.col.autoApproveGrants')" width="220">
        <template #default="{ row }">
          <el-tooltip
            v-if="row.alwaysConfirm === true"
            :content="t('mcpKinds.alwaysConfirmTooltip', { n: autoApproveCount(row.id) })"
            placement="top"
          >
            <el-tag
              type="info"
              size="small"
              effect="dark"
              class="always-confirm-tag"
              @click="goToAlwaysConfirmSetting"
            >
              🔒 {{ t('mcpKinds.alwaysConfirm') }}
            </el-tag>
          </el-tooltip>
          <el-tag
            v-else-if="autoApproveCount(row.id) > 0"
            type="warning"
            size="small"
            effect="plain"
            class="auto-approve-tag"
            @click="goToGrants(row.id)"
          >
            ⚠ {{ autoApproveCount(row.id) }}
          </el-tag>
          <span v-else class="muted">{{ t('mcpKinds.noAutoApprove') }}</span>
        </template>
      </el-table-column>
    </el-table>
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { Refresh } from '@element-plus/icons-vue'
import {
  listMcpKinds, listMcpGrants, subscribeEventsMap,
  type McpKindRow, type McpGrantRow,
} from '../../api/daemon'

const { t } = useI18n()
const router = useRouter()
const route = useRoute()
const loading = ref(false)
const kinds = ref<McpKindRow[]>([])
const grants = ref<McpGrantRow[]>([])

function autoApproveCount(kindId: string): number {
  return grants.value.filter((g) => {
    const kp = (g.kindPattern || '').toLowerCase()
    return kp === '*' || kp === kindId.toLowerCase()
  }).length
}

function goToGrants(kindId: string): void {
  void router.push({ path: '/mcp/grants', query: { kind: kindId } })
}

// Phase 7.5+++ — deep-link to the Settings → Advanced → MCP section
// where the always-confirm picker lives. Operator can edit the
// override directly from a kind row.
function goToAlwaysConfirmSetting(): void {
  void router.push({ path: '/settings', query: { tab: 'advanced', scroll: 'mcp-section' } })
}

const search = ref('')
const pluginFilter = ref<string | null>(null)
// Phase 7.5+++ — danger filter pre-set from URL ?danger=. Driven by
// the McpHub stats card click on the ring-fenced count chip.
function parseDangerQuery(): 'all' | 'reversible' | 'destructive' {
  const q = typeof route.query.danger === 'string' ? route.query.danger : ''
  return q === 'reversible' || q === 'destructive' ? q : 'all'
}
const dangerFilter = ref<'all' | 'reversible' | 'destructive'>(parseDangerQuery())
watch(() => route.query.danger, () => { dangerFilter.value = parseDangerQuery() })
const reversibleCount = computed<number>(() =>
  kinds.value.filter((k) => k.danger === 'reversible').length)
const destructiveCount = computed<number>(() =>
  kinds.value.filter((k) => k.danger === 'destructive').length)

const filteredKinds = computed<McpKindRow[]>(() => {
  const q = search.value.trim().toLowerCase()
  return kinds.value.filter((k) => {
    if (pluginFilter.value && k.pluginId !== pluginFilter.value) return false
    if (dangerFilter.value !== 'all' && k.danger !== dangerFilter.value) return false
    if (q && !k.id.toLowerCase().includes(q) && !k.label.toLowerCase().includes(q)) return false
    return true
  })
})

const dangerCounts = computed(() => {
  const map = new Map<string, number>()
  for (const k of kinds.value) map.set(k.danger, (map.get(k.danger) ?? 0) + 1)
  // Stable order: destructive first (more visible), then reversible.
  return ['destructive', 'reversible']
    .filter((d) => map.has(d))
    .map((level) => ({ level, count: map.get(level) ?? 0 }))
})

const pluginCounts = computed(() => {
  const map = new Map<string, number>()
  for (const k of kinds.value) map.set(k.pluginId, (map.get(k.pluginId) ?? 0) + 1)
  // 'core' first; rest alphabetical so plugin order is predictable.
  const entries = Array.from(map.entries())
    .sort((a, b) => {
      if (a[0] === 'core') return -1
      if (b[0] === 'core') return 1
      return a[0].localeCompare(b[0])
    })
  return entries.map(([pluginId, count]) => ({ pluginId, count }))
})

// Live-refresh on grant changes elsewhere so the auto-approve column
// stays accurate without operator hitting Refresh. Cleaned on unmount.
let unsubscribeGrantSse: (() => void) | null = null

onMounted(() => {
  void refresh()
  // Phase 7.5+++ — also listen to mcp:settings-changed so the
  // alwaysConfirm column reflects always_confirm_kinds edits in
  // another tab without manual Refresh.
  unsubscribeGrantSse = subscribeEventsMap({
    'mcp:grant-changed': () => { void refresh() },
    'mcp:settings-changed': () => { void refresh() },
  })
})

onBeforeUnmount(() => {
  if (unsubscribeGrantSse) { unsubscribeGrantSse(); unsubscribeGrantSse = null }
})

async function refresh(): Promise<void> {
  loading.value = true
  try {
    const [k, g] = await Promise.all([
      listMcpKinds(),
      // Best-effort grants fetch — pulled together so the auto-approve
      // column lights up without a second round-trip. Failures here
      // shouldn't block the main kinds list.
      listMcpGrants(false, 1, 500).catch(() => ({ entries: [] as McpGrantRow[] })),
    ])
    kinds.value = k.entries
    grants.value = g.entries as McpGrantRow[]
  } catch (e) {
    ElMessage.error(t('mcpKinds.toastLoadFailed', { error: (e as Error).message }))
  } finally {
    loading.value = false
  }
}
</script>

<style scoped>
.page { padding: 16px; display: flex; flex-direction: column; gap: 12px; }
.page-header { display: flex; align-items: center; justify-content: space-between; }
.page-header h2 { margin: 0; }
.header-actions { display: flex; gap: 8px; }
.muted { color: var(--el-text-color-secondary); }
.kinds-table { margin-top: 8px; }
.mono { font-family: ui-monospace, 'JetBrains Mono', Consolas, monospace; font-size: 12px; }
.kinds-summary {
  display: flex; flex-wrap: wrap; align-items: center; gap: 6px;
}
.kinds-summary .separator {
  width: 1px; height: 16px;
  background: var(--el-border-color); margin: 0 4px;
}
.filter-toolbar {
  display: flex; gap: 8px; align-items: center;
}
.filter-toolbar .search-input { max-width: 280px; }
.filter-toolbar .plugin-select { width: 180px; }
.auto-approve-tag {
  cursor: pointer;
  user-select: none;
}
.auto-approve-tag:hover {
  background: var(--el-color-warning-light-7);
}
.always-confirm-tag {
  font-weight: 600;
  cursor: pointer;
  user-select: none;
}
.always-confirm-tag:hover {
  filter: brightness(1.1);
}
</style>
