<template>
  <!-- Not .tab-content: Settings.vue styles that class as a 480 px grid,
       which overflowed the narrow Simple-mode column. -->
  <div class="update-settings">
    <el-descriptions :column="1" border size="small">
      <el-descriptions-item :label="t('settings.update.current')">
        <span class="mono">v{{ currentVersion }}</span>
      </el-descriptions-item>
      <el-descriptions-item :label="t('settings.update.latest')">
        <span v-if="updateCheck.loading">{{ t('common.loading') }}</span>
        <span v-else-if="latestVersion" class="mono">
          v{{ latestVersion }}
          <el-tag v-if="hasUpdate" type="warning" size="small" style="margin-left:var(--wdc-space-2)">
            {{ t('settings.update.available') }}
          </el-tag>
          <el-tag v-else type="success" size="small" style="margin-left:var(--wdc-space-2)">
            {{ t('settings.update.upToDate') }}
          </el-tag>
        </span>
        <span v-else class="text-muted">{{ t('settings.update.notChecked') }}</span>
      </el-descriptions-item>
      <el-descriptions-item v-if="updaterLabel" :label="t('settings.update.autoUpdate')">
        {{ updaterLabel }}
      </el-descriptions-item>
      <el-descriptions-item v-if="lastCheckedIso" :label="t('settings.update.lastChecked')">
        <span class="text-muted">{{ formatRelativeTime(lastCheckedIso) }}</span>
      </el-descriptions-item>
    </el-descriptions>

    <div v-if="updater?.status === 'downloading'" class="update-progress">
      <el-progress :percentage="updater.percent ?? 0" :stroke-width="10" />
      <div class="update-progress-meta">
        <span>{{ t('settings.update.downloadingVersion', { v: updater.version ?? '' }) }}</span>
        <span v-if="updater.total" class="mono">{{ formatSize(updater.transferred ?? 0) }} / {{ formatSize(updater.total) }}</span>
      </div>
    </div>

    <div class="update-actions">
      <el-button
        size="small"
        :loading="updateCheck.loading || updater?.status === 'checking'"
        @click="emit('check')"
      >
        {{ t('settings.update.check') }}
      </el-button>
      <el-button v-if="updater?.status === 'downloaded'" type="primary" size="small" @click="emit('install')">
        {{ t('settings.update.restartInstall') }}
      </el-button>
      <!-- Portable and dev builds have no updater, and a failed updater
           leaves only the manual route: the release page, not a bare .exe.
           A failed updater gets the link even before the GitHub check ran. -->
      <el-button
        v-else-if="manualOnly && (hasUpdate || updater?.status === 'error')"
        type="primary"
        size="small"
        @click="emit('openRelease')"
      >
        {{ t('settings.update.openReleasePage') }}
      </el-button>
    </div>

    <el-alert
      v-if="updater?.status === 'error'"
      type="error"
      :closable="false"
      :title="t('settings.update.updaterError')"
      :description="t(updater.errorCode === 'ERR_UPDATER_INVALID_SIGNATURE' ? 'settings.update.errorHint.signature' : 'settings.update.errorHint.generic')"
      show-icon
      style="margin-top: var(--wdc-space-3)"
    />
    <!-- The raw electron-updater text (a signature failure dumps the whole
         certificate as JSON) stays available without filling the tab. -->
    <details v-if="updater?.status === 'error' && updater.error" class="updater-error-detail">
      <summary>{{ t('settings.update.errorDetail') }}</summary>
      <pre>{{ updater.error }}</pre>
    </details>

    <el-alert
      v-if="updateCheck.error"
      type="warning"
      :closable="false"
      :title="updateCheck.error"
      show-icon
      style="margin-top: var(--wdc-space-3)"
    />

    <SettingsCard
      v-if="updateCheck.releaseNotes"
      :title="`${t('settings.update.releaseNotesTitle')} v${updateCheck.latest}`"
      style="margin-top: var(--wdc-space-3)"
    >
      <template #meta>
        <el-link
          v-if="updateCheck.releaseUrl"
          :href="updateCheck.releaseUrl"
          target="_blank"
          type="primary"
          style="font-size: 0.78rem"
        >
          {{ t('settings.update.viewOnGithub') }} →
        </el-link>
      </template>
      <!-- eslint-disable-next-line vue/no-v-html — renderReleaseNotes escapes input first -->
      <div class="release-notes" v-html="renderReleaseNotes(updateCheck.releaseNotes)" />
    </SettingsCard>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { formatBytes } from '../../../utils/format'
import SettingsCard from '../shared/SettingsCard.vue'

/** GitHub release data: the version on offer and its notes. */
export interface EasyUpdateCheck {
  loading: boolean
  latest: string | null
  error: string | null
  lastCheckedIso: string | null
  releaseNotes: string | null
  releaseUrl: string | null
}

const props = defineProps<{
  t: (key: string, params?: Record<string, unknown>) => string
  currentVersion: string
  updateCheck: EasyUpdateCheck
  /** GitHub's answer, else what electron-updater's feed reported. */
  latestVersion: string | null
  lastCheckedIso: string | null
  hasUpdate: boolean
  /** electron-updater in the main process; null outside Electron. */
  updater: AppUpdaterState | null
  formatRelativeTime: (iso: string) => string
  renderReleaseNotes: (markdown: string) => string
}>()

const emit = defineEmits<{
  check: []
  install: []
  openRelease: []
}>()

const manualOnly = computed(() => !props.updater || props.updater.status === 'disabled' || props.updater.status === 'error')

const updaterLabel = computed(() => {
  const u = props.updater
  if (!u) return ''
  switch (u.status) {
    case 'disabled': return props.t(u.disabledReason === 'portable' ? 'settings.update.updater.portable' : 'settings.update.updater.dev')
    case 'checking': return props.t('settings.update.updater.checking')
    case 'downloading': return props.t('settings.update.updater.downloading', { v: u.version ?? '', p: u.percent ?? 0 })
    case 'downloaded': return props.t('settings.update.updater.downloaded', { v: u.version ?? '' })
    case 'up-to-date': return props.t('settings.update.updater.upToDate')
    case 'error': return props.t('settings.update.updater.error')
    default: return ''
  }
})

const { locale } = useI18n()
function formatSize(bytes: number): string {
  return formatBytes(bytes, locale.value)
}
</script>

<style scoped>
.update-settings :deep(.el-descriptions__cell) {
  overflow-wrap: anywhere;
}

.update-actions {
  display: flex;
  gap: var(--wdc-space-2);
  align-items: center;
  flex-wrap: wrap;
  margin-top: var(--wdc-space-3);
}

/* The gap spaces the buttons; Element's margin would indent a wrapped one. */
.update-actions .el-button + .el-button {
  margin-left: 0;
}

.update-progress {
  margin-top: var(--wdc-space-3);
}

.update-progress-meta {
  display: flex;
  justify-content: space-between;
  margin-top: var(--wdc-space-1);
  font-size: 0.78rem;
  color: var(--wdc-text-3);
}

.mono {
  font-family: 'JetBrains Mono', monospace;
}

.updater-error-detail {
  margin-top: var(--wdc-space-2);
  font-size: 0.78rem;
  color: var(--wdc-text-3);
}

.updater-error-detail summary {
  cursor: pointer;
}

.updater-error-detail pre {
  max-height: 12rem;
  overflow: auto;
  margin: var(--wdc-space-1) 0 0;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
  font-family: 'JetBrains Mono', monospace;
}
</style>
