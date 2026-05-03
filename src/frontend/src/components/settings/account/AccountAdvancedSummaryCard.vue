<template>
  <section class="settings-card">
    <header class="settings-card-header">
      <span class="settings-card-title">Account</span>
      <span class="account-email">{{ email }}</span>
    </header>
    <div class="settings-card-body">
      <div class="sync-actions">
        <el-button size="small" :loading="devicesLoading" @click="emit('refreshDevices')">
          {{ t('common.refresh') }} devices
        </el-button>
        <el-button size="small" type="danger" plain @click="emit('logout')">Sign out</el-button>
      </div>
    </div>
  </section>
</template>

<script setup lang="ts">
defineProps<{
  t: (key: string) => string
  email: string
  devicesLoading: boolean
}>()

const emit = defineEmits<{
  refreshDevices: []
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
