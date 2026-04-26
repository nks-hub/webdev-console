<template>
  <div class="history">
    <h4 class="history-title">
      {{ t('deploy.history.title') }}
      <div class="history-title-actions">
        <!-- Phase 7.5+++ — triggeredBy filter. Lets operators audit
             what AI/MCP has been doing vs human/CI deploys. Client-side
             over the loaded set since the table is already capped at 50. -->
        <el-select
          v-if="hasTriggeredByData"
          v-model="triggeredByFilter"
          size="small" clearable
          :placeholder="t('deploy.history.filterTriggeredByPlaceholder')"
          class="triggered-by-filter"
        >
          <el-option v-for="opt in triggeredByOptions" :key="opt"
            :label="opt" :value="opt" />
        </el-select>
        <el-button v-if="entries.length" link size="small" @click="$emit('refresh')">
          <el-icon><Refresh /></el-icon> {{ t('deploy.history.refresh') }}
        </el-button>
      </div>
    </h4>

    <!-- Phase 6.11a — per-host success-rate summary. Useful at a glance:
         "production 12/14 (86%)" tells operators where to focus. Hidden
         when there's only one host (the table itself shows everything). -->
    <div
      v-if="hostSummaries.length > 1"
      class="history-summary"
      role="group"
      :aria-label="t('deploy.history.successRateAria')"
    >
      <el-tag
        v-for="s in hostSummaries"
        :key="s.host"
        :type="rateTagType(s.successRate)"
        size="small"
        effect="plain"
        class="history-summary-tag"
      >
        <strong>{{ s.host }}</strong>
        {{ s.successes }}/{{ s.total }}
        <span class="history-summary-pct">({{ Math.round(s.successRate * 100) }}%)</span>
      </el-tag>
    </div>

    <el-empty v-if="!entries.length" :image-size="80" :description="t('deploy.history.noDeploys')" />
    <el-table v-else :data="filteredEntries" stripe size="small" :empty-text="t('deploy.history.noHistory')">
      <el-table-column prop="startedAt" :label="t('deploy.history.col.when')" width="170">
        <template #default="{ row }">
          <span class="mono">{{ formatDate(row.startedAt) }}</span>
        </template>
      </el-table-column>
      <el-table-column prop="host" :label="t('deploy.history.col.host')" width="120" />
      <el-table-column prop="branch" :label="t('deploy.history.col.branch')" width="120" />
      <el-table-column prop="commitSha" :label="t('deploy.history.col.commit')" width="100">
        <template #default="{ row }">
          <code v-if="row.commitSha" class="mono">{{ row.commitSha.slice(0, 7) }}</code>
          <span v-else class="muted">—</span>
        </template>
      </el-table-column>
      <el-table-column prop="finalPhase" :label="t('deploy.history.col.phase')">
        <template #default="{ row }">
          <el-tag :type="phaseTagType(row.finalPhase)" size="small" effect="plain">
            {{ row.finalPhase }}
          </el-tag>
        </template>
      </el-table-column>
      <el-table-column prop="triggeredBy" :label="t('deploy.history.col.triggeredBy')" width="100">
        <template #default="{ row }">
          <el-tag v-if="row.triggeredBy"
            :type="triggeredByTagType(row.triggeredBy)" size="small" effect="plain">
            {{ row.triggeredBy }}
          </el-tag>
          <span v-else class="muted">—</span>
        </template>
      </el-table-column>
      <el-table-column :label="t('deploy.history.col.actions')" width="160" align="right">
        <template #default="{ row }">
          <el-button size="small" link @click="$emit('rollback', row)">
            <el-icon><RefreshLeft /></el-icon> {{ t('deploy.history.rollback') }}
          </el-button>
        </template>
      </el-table-column>
    </el-table>
  </div>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { Refresh, RefreshLeft } from '@element-plus/icons-vue'
import type { DeployHistoryEntryDto } from '../../api/deploy'

const { t } = useI18n()

const props = defineProps<{ entries: DeployHistoryEntryDto[] }>()
defineEmits<{ refresh: []; rollback: [entry: DeployHistoryEntryDto] }>()

// Phase 7.5+++ — triggeredBy filter (client-side over loaded set).
const triggeredByFilter = ref<string | null>(null)

const triggeredByOptions = computed<string[]>(() => {
  const seen = new Set<string>()
  for (const e of props.entries) {
    if (e.triggeredBy) seen.add(e.triggeredBy)
  }
  return Array.from(seen).sort()
})

const hasTriggeredByData = computed(() => triggeredByOptions.value.length > 0)

const filteredEntries = computed<DeployHistoryEntryDto[]>(() => {
  if (!triggeredByFilter.value) return props.entries
  return props.entries.filter(e => e.triggeredBy === triggeredByFilter.value)
})

function triggeredByTagType(value: string): 'success' | 'warning' | 'info' | 'danger' {
  switch (value) {
    case 'gui':   return 'success'
    case 'mcp':   return 'warning'  // AI-triggered — surface visually
    case 'cli':   return 'info'
    default:      return 'info'
  }
}

interface HostSummary {
  host: string
  total: number
  successes: number
  successRate: number
}

/**
 * Phase 6.11a — group history entries by host, count successes, compute
 * success rate. Sort by total deploys descending so the most-deployed
 * host (usually production) leads the strip. "Done" is the success
 * phase per phaseTagType convention.
 */
const hostSummaries = computed<HostSummary[]>(() => {
  const byHost = new Map<string, { total: number; successes: number }>()
  for (const e of props.entries) {
    const cur = byHost.get(e.host) ?? { total: 0, successes: 0 }
    cur.total++
    if (e.finalPhase === 'Done') cur.successes++
    byHost.set(e.host, cur)
  }
  return Array.from(byHost.entries())
    .map(([host, s]) => ({
      host,
      total: s.total,
      successes: s.successes,
      successRate: s.total > 0 ? s.successes / s.total : 0,
    }))
    .sort((a, b) => b.total - a.total)
})

function rateTagType(rate: number): 'success' | 'warning' | 'danger' | 'info' {
  if (rate >= 0.9) return 'success'
  if (rate >= 0.7) return 'warning'
  if (rate > 0) return 'danger'
  return 'info'
}

function formatDate(iso: string): string {
  const d = new Date(iso)
  return d.toLocaleString()
}

function phaseTagType(phase: string): 'success' | 'danger' | 'warning' | 'info' {
  if (phase === 'Done') return 'success'
  if (phase === 'Failed') return 'danger'
  if (phase === 'RolledBack') return 'warning'
  return 'info'
}
</script>

<style scoped>
.history { display: flex; flex-direction: column; gap: 8px; }
.history-title {
  display: flex; align-items: center; justify-content: space-between;
  margin: 0; font-size: 14px;
}
.muted { color: var(--el-text-color-secondary); }
.mono { font-family: ui-monospace, 'JetBrains Mono', Consolas, monospace; }
.history-summary {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  padding: 4px 0;
}
.history-summary-tag {
  display: inline-flex;
  align-items: center;
  gap: 6px;
}
.history-summary-pct {
  font-variant-numeric: tabular-nums;
}
.history-title-actions {
  display: inline-flex; align-items: center; gap: 8px;
}
.triggered-by-filter {
  width: 140px;
}
</style>
