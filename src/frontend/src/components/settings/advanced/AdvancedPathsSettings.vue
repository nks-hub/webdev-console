<template>
  <div class="tab-content">
    <p class="tab-desc">{{ t('settings.paths.tabDesc') }}</p>

    <el-form label-position="top" size="small" class="paths-form">
      <el-form-item :label="t('settings.paths.apache')">
        <el-input
          :model-value="paths.apache"
          placeholder="C:\nks-wdc\binaries\apache\2.4\bin\httpd.exe"
          @update:model-value="emitPath('apache', $event)"
        >
          <template #append>
            <el-button @click="$emit('browse', 'apache', 'file')">{{ t('settings.paths.browse') }}</el-button>
          </template>
        </el-input>
      </el-form-item>
      <el-form-item :label="t('settings.paths.mysql')">
        <el-input
          :model-value="paths.mysql"
          placeholder="C:\nks-wdc\binaries\mysql\8.0\bin\mysqld.exe"
          @update:model-value="emitPath('mysql', $event)"
        >
          <template #append>
            <el-button @click="$emit('browse', 'mysql', 'file')">{{ t('settings.paths.browse') }}</el-button>
          </template>
        </el-input>
      </el-form-item>
      <el-form-item :label="t('settings.paths.php')">
        <el-input
          :model-value="paths.php"
          placeholder="C:\nks-wdc\binaries\php\8.4\php.exe"
          @update:model-value="emitPath('php', $event)"
        >
          <template #append>
            <el-button @click="$emit('browse', 'php', 'file')">{{ t('settings.paths.browse') }}</el-button>
          </template>
        </el-input>
      </el-form-item>
      <el-form-item :label="t('settings.paths.redis')">
        <el-input
          :model-value="paths.redis"
          placeholder="C:\nks-wdc\binaries\redis\7.2\redis-server.exe"
          @update:model-value="emitPath('redis', $event)"
        >
          <template #append>
            <el-button @click="$emit('browse', 'redis', 'file')">{{ t('settings.paths.browse') }}</el-button>
          </template>
        </el-input>
      </el-form-item>
      <el-form-item :label="t('settings.paths.sitesDir')">
        <el-input
          :model-value="paths.sitesDir"
          placeholder="C:\nks-wdc\conf\vhosts"
          @update:model-value="emitPath('sitesDir', $event)"
        >
          <template #append>
            <el-button @click="$emit('browse', 'sitesDir', 'folder')">{{ t('settings.paths.browse') }}</el-button>
          </template>
        </el-input>
      </el-form-item>
      <el-form-item :label="t('settings.paths.hostsFile')">
        <el-input
          :model-value="paths.hostsFile"
          placeholder="C:\Windows\System32\drivers\etc\hosts"
          @update:model-value="emitPath('hostsFile', $event)"
        >
          <template #append>
            <el-button @click="$emit('browse', 'hostsFile', 'file')">{{ t('settings.paths.browse') }}</el-button>
          </template>
        </el-input>
        <div class="hint">{{ t('settings.paths.hostsHint') }}</div>
      </el-form-item>

      <el-divider />

      <el-form-item :label="t('settings.paths.dataDir')">
        <el-input :model-value="dataDirDisplay" disabled class="mono-input" />
        <div class="hint">
          {{ t('settings.paths.dataHint') }}
          Override with <code>WDC_DATA_DIR</code> environment variable or
          <code>portable.txt</code> next to the executable.
        </div>
      </el-form-item>
      <el-form-item label="Backup directory">
        <el-input
          :model-value="backupDir"
          placeholder="~/.wdc/backups"
          @update:model-value="$emit('update:backupDir', String($event))"
        />
      </el-form-item>
      <el-form-item label="Auto-backup interval">
        <el-input-number
          :model-value="backupScheduleHours"
          :min="0"
          :max="720"
          controls-position="right"
          class="schedule-control"
          @update:model-value="$emit('update:backupScheduleHours', Number($event ?? 0))"
        />
        <span class="schedule-unit">hours</span>
        <div class="hint">
          Set to 0 to disable. When &gt; 0, the daemon creates a timestamped
          backup every N hours and prunes old ones (keeps 10).
        </div>
      </el-form-item>
    </el-form>

    <AdvancedBackupSettings
      :t="t"
      :backups="backups"
      :loading="backupsLoading"
      :creating="backupCreating"
      @create="$emit('createBackup')"
      @refresh="$emit('refreshBackups')"
      @download="$emit('downloadBackup', $event)"
    />
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import type { BackupEntry, SystemInfo } from '../../../api/daemon'
import AdvancedBackupSettings from './AdvancedBackupSettings.vue'

export interface AdvancedPathValues {
  apache: string
  mysql: string
  php: string
  redis: string
  sitesDir: string
  hostsFile: string
}

const props = defineProps<{
  t: (key: string, params?: Record<string, unknown>) => string
  paths: AdvancedPathValues
  systemInfo: SystemInfo | null
  backupDir: string
  backupScheduleHours: number
  backups: BackupEntry[]
  backupsLoading: boolean
  backupCreating: boolean
}>()

const emit = defineEmits<{
  'update:path': [key: keyof AdvancedPathValues, value: string]
  'update:backupDir': [value: string]
  'update:backupScheduleHours': [value: number]
  browse: [key: keyof AdvancedPathValues, kind: 'file' | 'folder']
  createBackup: []
  refreshBackups: []
  downloadBackup: [path: string]
}>()

const dataDirDisplay = computed(() => (
  props.systemInfo?.os?.machine ? '~/.wdc' : '~/.wdc'
))

function emitPath(key: keyof AdvancedPathValues, value: string | number): void {
  emit('update:path', key, String(value))
}
</script>

<style scoped>
.paths-form {
  max-width: 560px;
}

.schedule-control {
  width: 160px;
}

.schedule-unit {
  margin-left: 8px;
  font-size: 0.82rem;
  color: var(--wdc-text-3);
}
</style>
