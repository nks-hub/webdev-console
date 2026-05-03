<template>
  <SettingsCard :title="t('settings.sync.cloudSync')">
    <template #meta>
      <span v-if="syncStatus" :class="['sync-badge', syncStatus.ok ? 'sync-ok' : 'sync-err']">
        {{ syncStatus.message }}
      </span>
    </template>
    <p class="tab-desc">{{ t('settings.sync.cloudSyncDesc') }}</p>
    <div class="sync-actions">
      <el-button
        type="primary"
        size="small"
        :loading="syncing"
        :disabled="disabled"
        @click="emit('push')"
      >
        <el-icon><Upload /></el-icon>
        <span>{{ t('settings.sync.pushToCloud') }}</span>
      </el-button>
      <el-button
        size="small"
        :loading="pulling"
        :disabled="disabled"
        @click="emit('pull')"
      >
        <el-icon><Download /></el-icon>
        <span>{{ t('settings.sync.pullFromCloud') }}</span>
      </el-button>
      <el-button
        size="small"
        :disabled="disabled"
        :loading="checkingCloud"
        @click="emit('check')"
      >
        {{ t('settings.sync.checkStatus') }}
      </el-button>
    </div>
    <div v-if="lastSyncTime" class="hint">
      {{ t('settings.sync.lastSynced') }}: {{ lastSyncDisplay }}
    </div>
  </SettingsCard>
</template>

<script setup lang="ts">
import { Download, Upload } from '@element-plus/icons-vue'
import SettingsCard from '../shared/SettingsCard.vue'

defineProps<{
  t: (key: string) => string
  syncStatus: { ok: boolean; message: string } | null
  lastSyncTime: string | null
  lastSyncDisplay: string
  syncing: boolean
  pulling: boolean
  checkingCloud: boolean
  disabled: boolean
}>()

const emit = defineEmits<{
  push: []
  pull: []
  check: []
}>()
</script>

<style scoped>
.tab-desc {
  margin: 0 0 12px;
  color: var(--wdc-text-3);
  font-size: 0.85rem;
}

.sync-actions {
  display: flex;
  gap: 8px;
  flex-wrap: wrap;
}

.sync-badge {
  font-size: 0.72rem;
  font-weight: 600;
  padding: 2px 10px;
  border-radius: 10px;
}

.sync-ok {
  background: rgba(34, 197, 94, 0.15);
  color: var(--wdc-status-running);
}

.sync-err {
  background: rgba(255, 107, 107, 0.15);
  color: var(--wdc-status-error);
}

.hint {
  color: var(--wdc-text-3);
  font-size: 0.78rem;
  margin-top: 8px;
}
</style>
