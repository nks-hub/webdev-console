<template>
  <SettingsCard :title="t('settings.sync.device')">
    <el-form label-position="left" label-width="140px" size="small" class="device-form">
      <el-form-item :label="t('settings.sync.deviceId')">
        <el-input :model-value="deviceId" disabled class="mono-input">
          <template #append>
            <el-button :title="t('settings.sync.copy')" @click="emit('copy')">
              {{ t('settings.sync.copy') }}
            </el-button>
          </template>
        </el-input>
        <div class="hint">{{ t('settings.sync.deviceIdHint') }}</div>
      </el-form-item>
      <el-form-item :label="t('settings.sync.deviceName')">
        <el-input
          :model-value="deviceName"
          :placeholder="t('settings.sync.deviceNamePlaceholder')"
          @update:model-value="(value: string) => emit('update:deviceName', value)"
        />
        <div class="hint">{{ t('settings.sync.deviceNameHint') }}</div>
      </el-form-item>
    </el-form>
  </SettingsCard>
</template>

<script setup lang="ts">
import SettingsCard from '../shared/SettingsCard.vue'

defineProps<{
  t: (key: string) => string
  deviceId: string
  deviceName: string
}>()

const emit = defineEmits<{
  'update:deviceName': [value: string]
  copy: []
}>()
</script>

<style scoped>
.device-form {
  /* responsive: removed fixed cap */
}

.mono-input :deep(.el-input__inner) {
  font-family: 'JetBrains Mono', monospace;
}

.hint {
  color: var(--wdc-text-3);
  font-size: 0.78rem;
  margin-top: 4px;
}
</style>
