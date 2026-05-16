<template>
  <div class="advanced-backups">
    <div class="advanced-backups-header">
      <span class="advanced-backups-title">{{ t('settings.advancedBackups.title') }}</span>
      <div class="advanced-backups-actions">
        <el-button size="small" type="primary" :loading="creating" @click="$emit('create')">
          {{ t('settings.advancedBackups.create') }}
        </el-button>
        <el-button size="small" :loading="loading" @click="$emit('refresh')">
          {{ t('common.refresh') }}
        </el-button>
      </div>
    </div>

    <div v-if="loading" class="hint">{{ t('settings.advancedBackups.loading') }}</div>
    <div v-else-if="backups.length === 0" class="hint">
      {{ t('settings.advancedBackups.empty') }}
    </div>
    <el-table v-else :data="backups" size="small" stripe style="width: 100%">
      <el-table-column :label="t('settings.advancedBackups.colDate')" width="180">
        <template #default="{ row }">
          {{ formatBackupDate(row.createdUtc) }}
        </template>
      </el-table-column>
      <el-table-column :label="t('settings.advancedBackups.colSize')" width="130">
        <template #default="{ row }">
          {{ formatBackupSize(row.size) }}
        </template>
      </el-table-column>
      <el-table-column :label="t('common.actions')">
        <template #default="{ row }">
          <el-button size="small" @click="$emit('download', row.path)">
            {{ t('common.download') }}
          </el-button>
        </template>
      </el-table-column>
    </el-table>
  </div>
</template>

<script setup lang="ts">
import type { BackupEntry } from '../../../api/daemon'

defineProps<{
  t: (key: string, params?: Record<string, unknown>) => string
  backups: BackupEntry[]
  loading: boolean
  creating: boolean
}>()

defineEmits<{
  create: []
  refresh: []
  download: [path: string]
}>()

function formatBackupDate(createdUtc: string): string {
  return new Date(createdUtc).toLocaleString()
}

function formatBackupSize(size: number): string {
  return `${(size / 1024 / 1024).toFixed(1)} MB`
}
</script>

<style scoped>
.advanced-backups {
  margin-top: 24px;
  border-top: 1px solid var(--wdc-border);
  padding-top: 16px;
}

.advanced-backups-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 12px;
}

.advanced-backups-title {
  font-weight: 600;
  font-size: 0.95rem;
}

.advanced-backups-actions {
  display: flex;
  gap: 8px;
  flex-wrap: wrap;
  justify-content: flex-end;
}

@media (max-width: 640px) {
  .advanced-backups-header {
    align-items: flex-start;
    flex-direction: column;
  }
}
</style>
