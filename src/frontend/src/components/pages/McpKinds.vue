<template>
  <div class="page mcp-kinds-page">
    <div class="page-header">
      <h2>{{ t('mcpKinds.title') }}</h2>
      <el-button :loading="loading" @click="refresh">
        <el-icon><Refresh /></el-icon> {{ t('mcpKinds.refresh') }}
      </el-button>
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
           confirmation. Click → /mcp/grants pre-filtered for the kind. -->
      <el-table-column :label="t('mcpKinds.col.autoApproveGrants')" width="180">
        <template #default="{ row }">
          <el-tag
            v-if="autoApproveCount(row.id) > 0"
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
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { Refresh } from '@element-plus/icons-vue'
import { listMcpKinds, listMcpGrants, type McpKindRow, type McpGrantRow } from '../../api/daemon'

const { t } = useI18n()
const router = useRouter()
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

const search = ref('')
const pluginFilter = ref<string | null>(null)

const filteredKinds = computed<McpKindRow[]>(() => {
  const q = search.value.trim().toLowerCase()
  return kinds.value.filter((k) => {
    if (pluginFilter.value && k.pluginId !== pluginFilter.value) return false
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

onMounted(refresh)

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
</style>
