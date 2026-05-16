<template>
  <div class="tab-content">
    <el-descriptions :column="1" border size="small">
      <el-descriptions-item :label="t('settings.update.current')">
        <span class="mono">v{{ currentVersion }}</span>
      </el-descriptions-item>
      <el-descriptions-item :label="t('settings.update.latest')">
        <span v-if="updateCheck.loading">{{ t('common.loading') }}</span>
        <span v-else-if="updateCheck.latest" class="mono">
          v{{ updateCheck.latest }}
          <el-tag v-if="updateCheck.hasUpdate" type="warning" size="small" style="margin-left:8px">
            {{ t('settings.update.available') }}
          </el-tag>
          <el-tag v-else type="success" size="small" style="margin-left:8px">
            {{ t('settings.update.upToDate') }}
          </el-tag>
        </span>
        <span v-else class="text-muted">{{ t('settings.update.notChecked') }}</span>
      </el-descriptions-item>
      <el-descriptions-item v-if="updateCheck.lastCheckedIso" :label="t('settings.update.lastChecked')">
        <span class="text-muted">{{ formatRelativeTime(updateCheck.lastCheckedIso) }}</span>
      </el-descriptions-item>
    </el-descriptions>

    <div class="update-actions">
      <el-button size="small" :loading="updateCheck.loading" @click="emit('check')">
        {{ t('settings.update.check') }}
      </el-button>
      <el-button
        v-if="updateCheck.hasUpdate"
        type="primary"
        size="small"
        :loading="updateCheck.downloading"
        @click="emit('download')"
      >
        {{ t('settings.update.downloadInstall') }}
      </el-button>
      <el-link
        v-if="updateCheck.downloadUrl"
        :href="updateCheck.downloadUrl"
        target="_blank"
        type="primary"
      >
        {{ t('settings.update.openRelease') }} →
      </el-link>
    </div>

    <div v-if="updateCheck.progressPercent !== null" class="update-progress">
      <el-progress
        :percentage="updateCheck.progressPercent"
        :status="updateCheck.progressPercent >= 100 ? 'success' : undefined"
        :stroke-width="10"
      />
      <div class="update-progress-meta">
        <span>{{ updateCheck.progressPercent >= 100 ? t('common.installing') : t('common.downloading') }}</span>
        <span v-if="updateCheck.progressBytes" class="mono">{{ updateCheck.progressBytes }}</span>
      </div>
    </div>

    <section v-if="updateCheck.releaseNotes" class="settings-card" style="margin-top: 12px">
      <header class="settings-card-header">
        <span class="settings-card-title">
          {{ t('settings.update.releaseNotesTitle') }} v{{ updateCheck.latest }}
        </span>
        <el-link
          v-if="updateCheck.releaseUrl"
          :href="updateCheck.releaseUrl"
          target="_blank"
          type="primary"
          style="font-size: 0.78rem"
        >
          {{ t('settings.update.viewOnGithub') }} →
        </el-link>
      </header>
      <!-- eslint-disable-next-line vue/no-v-html — renderReleaseNotes escapes input first -->
      <div class="release-notes settings-card-body" v-html="renderReleaseNotes(updateCheck.releaseNotes)" />
    </section>

    <el-alert
      v-if="updateCheck.error"
      type="error"
      :closable="false"
      style="margin-top: 12px"
    >
      {{ updateCheck.error }}
    </el-alert>
  </div>
</template>

<script setup lang="ts">
export interface EasyUpdateCheck {
  loading: boolean
  downloading: boolean
  latest: string | null
  hasUpdate: boolean
  error: string | null
  downloadUrl: string | null
  lastCheckedIso: string | null
  releaseNotes: string | null
  releaseUrl: string | null
  progressPercent: number | null
  progressBytes: string | null
}

defineProps<{
  t: (key: string) => string
  currentVersion: string
  updateCheck: EasyUpdateCheck
  formatRelativeTime: (iso: string) => string
  renderReleaseNotes: (markdown: string) => string
}>()

const emit = defineEmits<{
  check: []
  download: []
}>()
</script>
