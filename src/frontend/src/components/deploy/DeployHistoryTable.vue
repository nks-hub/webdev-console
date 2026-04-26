<template>
  <div class="history">
    <h4 class="history-title">
      Deploy history
      <el-button v-if="entries.length" link size="small" @click="$emit('refresh')">
        <el-icon><Refresh /></el-icon> Refresh
      </el-button>
    </h4>
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
import { Refresh, RefreshLeft } from '@element-plus/icons-vue'
import type { DeployHistoryEntryDto } from '../../api/deploy'

defineProps<{ entries: DeployHistoryEntryDto[] }>()
defineEmits<{ refresh: []; rollback: [entry: DeployHistoryEntryDto] }>()

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
</style>
