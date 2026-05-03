<template>
  <section class="settings-card">
    <header class="settings-card-header">
      <span class="settings-card-title">{{ title }}</span>
      <span class="account-email">{{ email }}</span>
    </header>
    <div class="settings-card-body">
      <div class="sync-actions">
        <el-button size="small" type="primary" :loading="syncing" @click="emit('push')">
          <el-icon><Upload /></el-icon>
          <span>Push</span>
        </el-button>
        <el-button size="small" :loading="pulling" @click="emit('pull')">
          <el-icon><Download /></el-icon>
          <span>Pull</span>
        </el-button>
        <el-button size="small" type="danger" plain @click="emit('logout')">
          {{ t('common.logout') }}
        </el-button>
      </div>
    </div>
  </section>
</template>

<script setup lang="ts">
import { Download, Upload } from '@element-plus/icons-vue'

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
.settings-card {
  border: 1px solid var(--wdc-border);
  border-radius: 8px;
  background: var(--wdc-surface-2);
  margin-bottom: 12px;
}

.settings-card-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 14px 18px;
  border-bottom: 1px solid var(--wdc-border);
}

.settings-card-title {
  font-weight: 600;
  color: var(--wdc-text-1);
}

.settings-card-body {
  padding: 18px;
}

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
