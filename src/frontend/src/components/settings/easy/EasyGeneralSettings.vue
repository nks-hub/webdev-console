<template>
  <div class="tab-content">
    <!-- Simple-mode panel header already renders settings.general.tabDesc
         as its subtitle (Settings.vue), so duplicating it inside the
         component produced the same sentence twice. The advanced tab is
         a separate tab-pane without that wrapper, so we render the
         description only when used standalone. -->
    <p v-if="standalone" class="tab-desc">{{ t('settings.general.tabDesc') }}</p>

    <h4 class="section-heading">{{ t('settings.general.sectionAppearance') }}</h4>
    <el-form class="easy-settings-form" label-position="top" size="small">
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
    </el-form>

    <h4 class="section-heading">{{ t('settings.general.sectionStartup') }}</h4>
    <el-form class="easy-settings-form" label-position="top" size="small">
      <el-form-item :label="t('settings.general.runOnStartup')">
        <el-switch
          :model-value="runOnStartup"
          @update:model-value="(value: boolean) => emit('update:runOnStartup', value)"
        />
        <div class="hint">{{ t('settings.general.runOnStartupHint') }}</div>
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
        <div class="hint">{{ t('settings.general.defaultPhpHint') }}</div>
      </el-form-item>
    </el-form>

    <h4 class="section-heading">{{ t('settings.general.sectionTools') }}</h4>
    <el-form class="easy-settings-form" label-position="top" size="small">
      <el-form-item :label="t('settings.general.dnsCache')">
        <el-button size="small" :loading="flushingDns" @click="emit('flushDns')">
          {{ t('settings.general.flushDnsCache') }}
        </el-button>
        <div class="hint">{{ t('settings.general.dnsCacheHint') }}</div>
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
    </el-form>

    <h4 class="section-heading">{{ t('settings.general.sectionPrivacy') }}</h4>
    <el-form class="easy-settings-form" label-position="top" size="small">
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
        <div class="hint">{{ t('settings.general.crashReportsHint') }}</div>
      </el-form-item>
    </el-form>
  </div>
</template>

<script setup lang="ts">
import type { ThemeMode } from '../../../stores/theme'

withDefaults(defineProps<{
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
  /** Render the in-card tab description. Defaults to true for the
   *  Advanced tab. The Simple-mode panel passes false because the
   *  surrounding SettingsCard header already shows the description. */
  standalone?: boolean
}>(), { standalone: true })

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

<style scoped>
.tab-content {
  min-width: 0;
}

.tab-desc {
  margin: 0 0 14px;
  color: var(--wdc-text-2);
  font-size: 0.86rem;
  line-height: 1.45;
}

.section-heading {
  margin: 18px 0 10px;
  font-size: 0.72rem;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.08em;
  color: var(--wdc-text-2);
}

.section-heading:first-of-type {
  margin-top: 4px;
}

.easy-settings-form {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(260px, 1fr));
  gap: 14px 18px;
  width: 100%;
}

.easy-settings-form :deep(.el-form-item) {
  align-content: start;
  min-width: 0;
  margin-bottom: 0;
  padding: 12px;
  background: var(--wdc-surface-2);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius);
}

.easy-settings-form :deep(.el-form-item__label) {
  color: var(--wdc-text);
  font-weight: 700;
  line-height: 1.25;
  margin-bottom: 8px;
}

.easy-settings-form :deep(.el-select),
.easy-settings-form :deep(.el-button),
.easy-settings-form :deep(.el-radio-group) {
  max-width: 100%;
}

.hint {
  width: 100%;
  margin-top: 8px;
  color: var(--wdc-text-2);
  font-size: 0.8rem;
  line-height: 1.45;
}

@media (max-width: 760px) {
  .easy-settings-form {
    grid-template-columns: minmax(0, 1fr);
  }

  .easy-settings-form :deep(.el-radio-group) {
    display: flex;
    flex-wrap: wrap;
  }
}
</style>
