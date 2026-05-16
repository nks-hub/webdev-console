<template>
  <SettingsCard :title="title">
    <template #meta>
      <span class="account-email">{{ email }}</span>
    </template>
    <div class="sync-actions">
      <el-button size="small" type="primary" :loading="syncing" @click="emit('push')">
        <el-icon><Upload /></el-icon>
        <span>{{ t('common.push') }}</span>
      </el-button>
      <el-button size="small" :loading="pulling" @click="emit('pull')">
        <el-icon><Download /></el-icon>
        <span>{{ t('common.pull') }}</span>
      </el-button>
      <el-button size="small" type="danger" plain @click="emit('logout')">
        {{ t('common.logout') }}
      </el-button>
    </div>
  </SettingsCard>
</template>

<script setup lang="ts">
import { Download, Upload } from '@element-plus/icons-vue'
import SettingsCard from '../shared/SettingsCard.vue'

defineProps<{
  t: (key: string) => string
  title: string
  email: string
  syncing: boolean
  pulling: boolean
}>()

const emit = defineEmits<{
  push: []
  pull: []
  logout: []
}>()
</script>

<style scoped>
.sync-actions {
  display: flex;
  gap: 8px;
  flex-wrap: wrap;
}

.account-email {
  font-size: 0.78rem;
  color: var(--wdc-text-2);
}
</style>
