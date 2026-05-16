<template>
  <SettingsCard :title="$t('settings.devices.cardTitle')">
    <template #meta>
      <span class="device-count">{{ $t('settings.devices.registered', { n: devices.length }) }}</span>
    </template>
    <el-table v-if="devices.length > 0" :data="devices" size="small" stripe>
      <el-table-column :label="$t('settings.devices.colName')" min-width="180">
        <template #default="{ row }">
          <div class="device-name-cell">
            <el-input
              v-if="editingDeviceName === row.device_id"
              :model-value="editingDeviceValue"
              size="small"
              class="device-name-input"
              @update:model-value="(value: string) => emit('update:editingDeviceValue', value)"
              @blur="emit('saveName', row)"
              @keydown.enter.prevent="emit('saveName', row)"
              @keydown.escape.prevent="emit('update:editingDeviceName', null)"
            />
            <span
              v-else
              class="device-name-text mono"
              :style="row.is_current ? 'font-weight: 700' : ''"
              :title="$t('settings.devices.doubleClickRename')"
              @dblclick="emit('startEditName', row)"
            >
              {{ deviceLabel(row) }}
            </span>
            <el-tag v-if="row.is_current" size="small" type="success" effect="dark" class="current-tag">{{ $t('settings.devices.thisTag') }}</el-tag>
          </div>
        </template>
      </el-table-column>
      <el-table-column :label="$t('settings.devices.colOs')" width="120">
        <template #default="{ row }">
          <span class="mono">{{ (row.os ?? '') + '/' + (row.arch ?? '') }}</span>
        </template>
      </el-table-column>
      <el-table-column :label="$t('settings.devices.colSites')" width="70" align="center">
        <template #default="{ row }">{{ row.site_count ?? '—' }}</template>
      </el-table-column>
      <el-table-column :label="$t('settings.devices.colStatus')" width="90">
        <template #default="{ row }">
          <el-tag size="small" :type="row.online ? 'success' : 'info'" effect="dark">
            {{ row.online ? $t('settings.devices.online') : $t('settings.devices.offline') }}
          </el-tag>
        </template>
      </el-table-column>
      <el-table-column :label="$t('settings.devices.colLastSync')" width="150">
        <template #default="{ row }">
          <span class="last-sync">
            {{ row.last_seen_at ? new Date(row.last_seen_at).toLocaleString() : '—' }}
          </span>
        </template>
      </el-table-column>
      <el-table-column label="" width="200" align="right">
        <template #default="{ row }">
          <div class="row-actions">
            <el-button
              v-if="!row.is_current"
              size="small"
              type="primary"
              plain
              :loading="pushingTo === row.device_id"
              @click="emit('pushConfig', row.device_id)"
            >{{ $t('settings.devices.pushHere') }}</el-button>
            <el-button
              v-if="!row.is_current"
              size="small"
              type="danger"
              plain
              :loading="unlinkingDevice === row.device_id"
              @click="emit('unlink', row)"
            >{{ $t('settings.devices.unlink') }}</el-button>
          </div>
        </template>
      </el-table-column>
    </el-table>
    <el-empty v-else :description="$t('settings.devices.noDevices')" :image-size="48" />
  </SettingsCard>
</template>

<script setup lang="ts">
import type { DeviceInfo as CatalogDeviceInfo } from '../../../api/daemon'
import SettingsCard from '../shared/SettingsCard.vue'

defineProps<{
  devices: CatalogDeviceInfo[]
  editingDeviceName: string | null
  editingDeviceValue: string
  pushingTo: string | null
  unlinkingDevice: string | null
}>()

const emit = defineEmits<{
  'update:editingDeviceName': [value: string | null]
  'update:editingDeviceValue': [value: string]
  startEditName: [row: CatalogDeviceInfo]
  saveName: [row: CatalogDeviceInfo]
  pushConfig: [deviceId: string]
  unlink: [row: CatalogDeviceInfo]
}>()

function deviceLabel(row: CatalogDeviceInfo): string {
  return row.name || `${row.device_id.slice(0, 12)}…`
}
</script>

<style scoped>
.device-count {
  font-size: 0.72rem;
  color: var(--wdc-text-3);
}

.device-name-cell {
  display: flex;
  align-items: center;
  gap: 6px;
}

.device-name-text {
  cursor: pointer;
}

.device-name-text:hover {
  text-decoration: underline dashed var(--wdc-text-3);
  text-underline-offset: 3px;
}

.device-name-input {
  max-width: 160px;
}

.current-tag {
  margin-left: 6px;
}

.mono {
  font-family: 'JetBrains Mono', monospace;
  font-size: 0.88rem;
}

.last-sync {
  font-size: 0.72rem;
  color: var(--wdc-text-3);
}

.row-actions {
  display: flex;
  gap: 6px;
  justify-content: flex-end;
}
</style>
