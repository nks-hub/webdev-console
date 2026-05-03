<template>
  <div class="tab-content">
    <p class="tab-desc">{{ t('settings.general.tabDesc') }}</p>
    <el-form label-position="left" label-width="180px" size="small" style="max-width: 500px">
      <el-form-item :label="t('settings.general.language')">
        <el-select
          :model-value="locale"
          @update:model-value="(value: string) => emit('update:locale', value)"
          style="width: 160px"
        >
          <el-option label="English" value="en" />
          <el-option label="Čeština" value="cs" />
        </el-select>
      </el-form-item>
      <el-form-item :label="t('settings.theme.label')">
        <el-radio-group
          :model-value="themeMode"
          @update:model-value="(value: ThemeMode) => emit('update:themeMode', value)"
        >
          <el-radio-button value="dark">{{ t('settings.theme.dark') }}</el-radio-button>
          <el-radio-button value="light">{{ t('settings.theme.light') }}</el-radio-button>
          <el-radio-button value="system">{{ t('settings.theme.system') }}</el-radio-button>
        </el-radio-group>
      </el-form-item>
      <el-form-item :label="t('settings.mode.label')">
        <el-switch
          :model-value="isAdvanced"
          :active-text="t('settings.mode.advanced')"
          :inactive-text="t('settings.mode.simple')"
          @change="(value: boolean) => emit('update:uiMode', value ? 'advanced' : 'simple')"
        />
        <div class="hint">{{ t('settings.mode.description') }}</div>
      </el-form-item>
      <el-form-item :label="t('settings.general.runOnStartup')">
        <el-switch
          :model-value="runOnStartup"
          @update:model-value="(value: boolean) => emit('update:runOnStartup', value)"
        />
      </el-form-item>
      <el-form-item :label="t('settings.general.defaultPhpVersion')">
        <el-select
          :model-value="defaultPhp"
          style="width: 160px"
          :placeholder="t('settings.general.selectPlaceholder')"
          @update:model-value="(value: string) => emit('update:defaultPhp', value)"
        >
          <el-option v-for="version in phpVersions" :key="version" :label="'PHP ' + version" :value="version" />
        </el-select>
      </el-form-item>
      <el-form-item :label="t('settings.general.dnsCache')">
        <el-button size="small" :loading="flushingDns" @click="emit('flushDns')">
          {{ t('settings.general.flushDnsCache') }}
        </el-button>
      </el-form-item>
      <el-form-item :label="t('settings.general.mampImport')">
        <el-button
          size="small"
          :loading="mampDiscovering"
          :title="t('settings.general.mampHint')"
          @click="emit('discoverMamp')"
        >
          {{ t('settings.general.mampMigrate') }}
        </el-button>
        <div class="hint">{{ t('settings.general.mampHint') }}</div>
      </el-form-item>

      <el-divider />

      <el-form-item :label="t('settings.general.telemetry')">
        <el-switch
          :model-value="telemetryEnabled"
          @update:model-value="(value: boolean) => emit('update:telemetryEnabled', value)"
        />
        <div class="hint">{{ t('settings.general.telemetryHint') }}</div>
      </el-form-item>
      <el-form-item v-if="telemetryEnabled" :label="t('settings.general.crashReports')">
        <el-switch
          :model-value="telemetryCrashReports"
          @update:model-value="(value: boolean) => emit('update:telemetryCrashReports', value)"
        />
        <div class="hint">
          Send crash stack traces via Sentry when a daemon exception occurs.
          Disabled when telemetry is off.
        </div>
      </el-form-item>
    </el-form>
  </div>
</template>

<script setup lang="ts">
import type { ThemeMode } from '../../../stores/theme'

defineProps<{
  t: (key: string) => string
  locale: string
  themeMode: ThemeMode
  isAdvanced: boolean
  runOnStartup: boolean
  defaultPhp: string
  phpVersions: string[]
  flushingDns: boolean
  mampDiscovering: boolean
  telemetryEnabled: boolean
  telemetryCrashReports: boolean
}>()

const emit = defineEmits<{
  'update:locale': [value: string]
  'update:themeMode': [value: ThemeMode]
  'update:uiMode': [value: 'advanced' | 'simple']
  'update:runOnStartup': [value: boolean]
  'update:defaultPhp': [value: string]
  'update:telemetryEnabled': [value: boolean]
  'update:telemetryCrashReports': [value: boolean]
  flushDns: []
  discoverMamp: []
}>()
</script>
