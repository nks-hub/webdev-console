<template>
  <SettingsCard :title="t('settings.account.cardTitle')">
    <template #meta>
      <span class="account-email">{{ email }}</span>
    </template>
    <div class="sync-actions">
      <el-button size="small" :loading="devicesLoading" @click="emit('refreshDevices')">
        {{ t('settings.account.refreshDevices') }}
      </el-button>
      <el-button size="small" type="danger" plain @click="emit('logout')">{{ t('settings.account.signOut') }}</el-button>
    </div>
  </SettingsCard>
</template>

<script setup lang="ts">
import SettingsCard from '../shared/SettingsCard.vue'

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
