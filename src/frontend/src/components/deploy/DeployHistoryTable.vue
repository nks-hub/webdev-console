<template>
  <div class="history">
    <h4 class="history-title">
      Deploy history
      <el-button v-if="entries.length" link size="small" @click="$emit('refresh')">
        <el-icon><Refresh /></el-icon> Refresh
      </el-button>
    </h4>

    <!-- Phase 6.11a — per-host success-rate summary. Useful at a glance:
         "production 12/14 (86%)" tells operators where to focus. Hidden
         when there's only one host (the table itself shows everything). -->
    <div
      v-if="hostSummaries.length > 1"
      class="history-summary"
      role="group"
      aria-label="Per-host deploy success rate"
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

    <el-empty v-if="!entries.length" :image-size="80" description="No deploys recorded yet" />
    <el-table v-else :data="entries" stripe size="small" :empty-text="'No history'">
      <el-table-column prop="startedAt" label="When" width="170">
        <template #default="{ row }">
          <span class="mono">{{ formatDate(row.startedAt) }}</span>
        </template>
      </el-table-column>
      <el-table-column prop="host" label="Host" width="120" />
      <el-table-column prop="branch" label="Branch" width="120" />
      <el-table-column prop="commitSha" label="Commit" width="100">
        <template #default="{ row }">
          <code v-if="row.commitSha" class="mono">{{ row.commitSha.slice(0, 7) }}</code>
          <span v-else class="muted">—</span>
        </template>
      </el-table-column>
      <el-table-column prop="finalPhase" label="Phase">
        <template #default="{ row }">
          <el-tag :type="phaseTagType(row.finalPhase)" size="small" effect="plain">
            {{ row.finalPhase }}
          </el-tag>
        </template>
      </el-table-column>
      <el-table-column label="Actions" width="160" align="right">
        <template #default="{ row }">
          <el-button size="small" link @click="$emit('rollback', row)">
            <el-icon><RefreshLeft /></el-icon> Rollback
          </el-button>
        </template>
      </el-table-column>
    </el-table>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { Refresh, RefreshLeft } from '@element-plus/icons-vue'
import type { DeployHistoryEntryDto } from '../../api/deploy'

const props = defineProps<{ entries: DeployHistoryEntryDto[] }>()
defineEmits<{ refresh: []; rollback: [entry: DeployHistoryEntryDto] }>()

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
</style>
