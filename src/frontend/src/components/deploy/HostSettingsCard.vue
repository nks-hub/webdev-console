<template>
  <!--
    Phase 7.5+++ Deploy Settings redesign — rich host card.
    Replaces table rows with at-a-glance cards showing all the
    operationally relevant info: SSH target, branch, local paths,
    composer/migrations toggles, soak — all without opening the
    edit modal. Card actions trigger Edit / Remove / Test conn.
  -->
  <el-card
    class="host-settings-card"
    :class="{ 'host-prod': isProduction }"
    shadow="hover"
  >
    <template #header>
      <div class="card-header">
        <div class="header-left">
          <span class="host-name">{{ host.name }}</span>
          <el-tag v-if="isProduction" type="danger" size="small" effect="dark">PROD</el-tag>
          <el-tag v-else type="info" size="small" effect="plain">{{ envHint }}</el-tag>
        </div>
        <div class="header-actions">
          <el-button size="small" plain @click="$emit('edit')">
            {{ t('deploySettings.hostCardCmp.edit') }}
          </el-button>
          <el-button size="small" plain type="danger" @click="$emit('remove')">
            {{ t('deploySettings.hostCardCmp.remove') }}
          </el-button>
        </div>
      </div>
    </template>

    <div class="card-grid">
      <!-- SSH target row -->
      <div class="info-row">
        <span class="info-label">{{ t('deploySettings.hostCardCmp.sshTarget') }}</span>
        <span class="info-value mono">{{ host.sshUser }}@{{ host.sshHost }}:{{ host.sshPort }}</span>
      </div>

      <!-- Remote path -->
      <div class="info-row">
        <span class="info-label">{{ t('deploySettings.hostCardCmp.remotePath') }}</span>
        <span class="info-value mono">{{ host.remotePath }}</span>
      </div>

      <!-- Branch -->
      <div class="info-row">
        <span class="info-label">{{ t('deploySettings.hostCardCmp.branch') }}</span>
        <span class="info-value mono branch-pill">{{ host.branch }}</span>
      </div>

      <!-- Local paths (local-loopback backend) -->
      <div v-if="host.localSourcePath || host.localTargetPath" class="info-row info-row-split">
        <span class="info-label">{{ t('deploySettings.hostCardCmp.localPaths') }}</span>
        <div class="info-value local-paths">
          <span v-if="host.localSourcePath" class="mono local-path-line">
            <span class="local-path-arrow">→</span> {{ host.localSourcePath }}
          </span>
          <span v-if="host.localTargetPath" class="mono local-path-line">
            <span class="local-path-arrow">⇒</span> {{ host.localTargetPath }}
          </span>
        </div>
      </div>
      <div v-else class="info-row">
        <span class="info-label">{{ t('deploySettings.hostCardCmp.localPaths') }}</span>
        <span class="info-value muted">{{ t('deploySettings.hostCardCmp.localPathsNotSet') }}</span>
      </div>

      <!-- Build flags row -->
      <div class="info-row info-row-flags">
        <span class="info-label">{{ t('deploySettings.hostCardCmp.flags') }}</span>
        <div class="info-value flags">
          <el-tag :type="host.composerInstall ? 'success' : 'info'" size="small" effect="plain">
            <el-icon class="flag-icon">
              <Check v-if="host.composerInstall" />
              <Close v-else />
            </el-icon>
            composer
          </el-tag>
          <el-tag :type="host.runMigrations ? 'success' : 'info'" size="small" effect="plain">
            <el-icon class="flag-icon">
              <Check v-if="host.runMigrations" />
              <Close v-else />
            </el-icon>
            migrations
          </el-tag>
          <el-tag type="info" size="small" effect="plain">
            <el-icon class="flag-icon"><Timer /></el-icon>
            {{ t('deploySettings.hostCardCmp.soak', { n: host.soakSeconds }) }}
          </el-tag>
        </div>
      </div>

      <!-- Optional fields -->
      <div v-if="host.healthCheckUrl" class="info-row">
        <span class="info-label">{{ t('deploySettings.hostCardCmp.healthUrl') }}</span>
        <a class="info-value mono link" :href="host.healthCheckUrl" target="_blank" rel="noopener">
          {{ host.healthCheckUrl }}
        </a>
      </div>
      <div v-if="host.phpBinaryPath" class="info-row">
        <span class="info-label">{{ t('deploySettings.hostCardCmp.phpPath') }}</span>
        <span class="info-value mono">{{ host.phpBinaryPath }}</span>
      </div>
    </div>
  </el-card>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { Check, Close, Timer } from '@element-plus/icons-vue'
import type { DeployHostConfig } from '../../api/deploy'

const { t } = useI18n()
const props = defineProps<{ host: DeployHostConfig }>()
defineEmits<{ edit: []; remove: [] }>()

const isProduction = computed(() =>
  props.host.name.toLowerCase().includes('prod') ||
  props.host.name.toLowerCase() === 'production')

const envHint = computed(() => {
  const n = props.host.name.toLowerCase()
  if (n.includes('stag')) return 'STAGING'
  if (n.includes('dev')) return 'DEV'
  if (n.includes('test')) return 'TEST'
  return 'ENV'
})
</script>

<style scoped>
.host-settings-card {
  display: flex;
  flex-direction: column;
  border: 1px solid var(--el-border-color-lighter);
  transition: border-color 0.2s ease;
}
.host-settings-card.host-prod {
  border-left: 3px solid var(--el-color-danger);
}
.host-settings-card:hover {
  border-color: var(--el-color-primary-light-5);
}

.card-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}
.header-left {
  display: flex;
  align-items: center;
  gap: 8px;
}
.host-name {
  font-weight: 700;
  font-size: 15px;
  font-family: ui-monospace, 'JetBrains Mono', Consolas, monospace;
}
.header-actions {
  display: flex;
  gap: 8px;
}

.card-grid {
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.info-row {
  display: grid;
  grid-template-columns: 130px 1fr;
  gap: 12px;
  align-items: baseline;
  font-size: 13px;
}
.info-row-split,
.info-row-flags {
  align-items: flex-start;
}
.info-label {
  color: var(--el-text-color-secondary);
  font-size: 12px;
  text-transform: uppercase;
  letter-spacing: 0.04em;
}
.info-value {
  word-break: break-all;
}
.mono {
  font-family: ui-monospace, 'JetBrains Mono', Consolas, monospace;
  font-size: 12px;
}
.muted { color: var(--el-text-color-secondary); }
.link {
  color: var(--el-color-primary);
  text-decoration: none;
}
.link:hover { text-decoration: underline; }

.branch-pill {
  display: inline-block;
  padding: 2px 8px;
  background: var(--el-fill-color-light);
  border: 1px solid var(--el-border-color-lighter);
  border-radius: 10px;
}

.local-paths {
  display: flex;
  flex-direction: column;
  gap: 4px;
}
.local-path-line {
  display: inline-flex;
  align-items: baseline;
  gap: 6px;
}
.local-path-arrow {
  color: var(--el-color-primary);
  font-weight: 600;
}

.flags {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}
.flag-icon {
  font-size: 12px;
  margin-right: 2px;
  vertical-align: -2px;
}
</style>
