<template>
  <SettingsCard title="Snapshots">
    <template #meta>
      <el-button size="small" :loading="loading" @click="emit('refresh')">
        {{ t('common.refresh') }}
      </el-button>
    </template>
    <p class="tab-desc">
      Revert to a previous configuration. Cloud keeps the last
      10 snapshots per device — older ones are pruned automatically.
    </p>
    <el-table v-if="snapshots.length > 0" :data="snapshots" size="small" stripe>
      <el-table-column label="When" min-width="180">
        <template #default="{ row }">
          {{ formatDate(row.created_at) }}
        </template>
      </el-table-column>
      <el-table-column label="Device" min-width="140">
        <template #default="{ row }">
          <span class="mono">{{ row.device_id.slice(0, 12) }}…</span>
        </template>
      </el-table-column>
      <el-table-column label="Size" width="100">
        <template #default="{ row }">
          <span class="mono">{{ Math.round(row.size_bytes / 1024) }} KB</span>
        </template>
      </el-table-column>
      <el-table-column label="" width="180" align="right">
        <template #default="{ row }">
          <div class="row-actions">
            <el-button size="small" plain :loading="snapshotAction === row.id" @click="emit('restore', row)">
              Restore
            </el-button>
            <el-button size="small" type="danger" plain :loading="snapshotAction === row.id" @click="emit('delete', row)">
              {{ t('common.delete') }}
            </el-button>
          </div>
        </template>
      </el-table-column>
    </el-table>
    <el-empty
      v-else
      :description="loading ? t('common.loading') : 'No snapshots yet — push to cloud first'"
      :image-size="48"
    />
  </SettingsCard>
</template>

<script setup lang="ts">
import SettingsCard from '../shared/SettingsCard.vue'

export interface SyncSnapshotRow {
  id: number
  created_at: string
  device_id: string
  size_bytes: number
}

defineProps<{
  t: (key: string) => string
  snapshots: SyncSnapshotRow[]
  loading: boolean
  snapshotAction: number | null
  formatDate: (value: string | number | null | undefined) => string
}>()

const emit = defineEmits<{
  refresh: []
  restore: [row: SyncSnapshotRow]
  delete: [row: SyncSnapshotRow]
}>()
</script>

<style scoped>
.tab-desc {
  margin: 0 0 10px;
  color: var(--wdc-text-3);
  font-size: 0.85rem;
}

.mono {
  font-family: 'JetBrains Mono', monospace;
  font-size: 0.88rem;
}

.row-actions {
  display: flex;
  gap: 6px;
  justify-content: flex-end;
}
</style>
