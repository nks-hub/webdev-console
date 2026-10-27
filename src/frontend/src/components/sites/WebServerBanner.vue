<template>
  <div v-if="kind" class="ws-banner" :class="`ws-${kind}`" :role="problem ? 'alert' : 'status'">
    <el-icon :class="{ 'is-loading': transitional }" :size="18" aria-hidden="true">
      <Loading v-if="transitional" />
      <WarningFilled v-else />
    </el-icon>
    <div class="ws-text">
      <strong>{{ $t(`sites.banner.${kind}.title`) }}</strong>
      <span>{{ detail }}</span>
    </div>
    <div v-if="!transitional" class="ws-actions">
      <el-button
        v-if="kind === 'missing'"
        type="primary"
        :loading="siteState.installing.has('apache')"
        @click="siteState.install('apache')"
      >{{ $t('sites.banner.installApache') }}</el-button>
      <el-button
        v-else
        type="primary"
        :loading="siteState.starting"
        @click="siteState.startWebServer()"
      >{{ problem ? $t('common.retry') : $t('sites.banner.startApache') }}</el-button>
      <el-button v-if="problem" @click="router.push('/plugins/apache')">{{ $t('sites.banner.showLog') }}</el-button>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { Loading, WarningFilled } from '@element-plus/icons-vue'
import { useSiteStateStore } from '../../stores/siteState'

/**
 * The single place where "no site answers because the web server is down"
 * is said, with the button that fixes it. Replaces a "stopped" label in
 * every table row. While a site is being saved the web server is briefly
 * restarting; that is said as such instead of as a fault.
 */
const router = useRouter()
const { t } = useI18n()
const siteState = useSiteStateStore()

type Kind = 'missing' | 'stopped' | 'crashed' | 'failed' | 'starting' | 'stopping' | 'restarting'
const kind = computed<Kind | null>(() => {
  switch (siteState.webServer) {
    case 'missing': case 'starting': case 'stopping': case 'restarting':
      return siteState.webServer
    case 'crashed':
      return siteState.apacheStartFailed ? 'failed' : 'crashed'
    case 'stopped':
      return siteState.startError ? 'failed' : 'stopped'
    default:
      return null
  }
})
const transitional = computed(() => kind.value === 'starting' || kind.value === 'stopping' || kind.value === 'restarting')
const problem = computed(() => kind.value === 'crashed' || kind.value === 'failed')

// The reason of a failed start comes from the daemon; without one the generic sentence is shown.
const detail = computed(() => {
  if (!kind.value) return ''
  if (problem.value && siteState.startError) return t('sites.banner.failed.detail', { err: siteState.startError })
  return t(kind.value === 'failed' ? 'sites.banner.failed.noReason' : `sites.banner.${kind.value}.detail`)
})
</script>

<style scoped>
.ws-banner {
  display: flex;
  align-items: center;
  gap: var(--wdc-space-3);
  padding: var(--wdc-space-3) var(--wdc-space-4);
  border: 1px solid color-mix(in oklab, var(--wdc-status-starting) 55%, var(--wdc-border));
  border-radius: var(--wdc-radius-lg);
  background: color-mix(in oklab, var(--wdc-status-starting) 11%, var(--wdc-surface));
}
.ws-crashed,
.ws-failed {
  border-color: color-mix(in oklab, var(--wdc-status-error) 55%, var(--wdc-border));
  background: color-mix(in oklab, var(--wdc-status-error) 9%, var(--wdc-surface));
}
.ws-starting,
.ws-stopping,
.ws-restarting {
  border-color: var(--wdc-border-strong);
  background: var(--wdc-surface-2);
}
.ws-banner > .el-icon { flex-shrink: 0; color: var(--wdc-status-starting); }
.ws-crashed > .el-icon,
.ws-failed > .el-icon { color: var(--wdc-status-error); }
.ws-starting > .el-icon,
.ws-stopping > .el-icon,
.ws-restarting > .el-icon { color: var(--wdc-text-2); }
.ws-text {
  display: grid;
  gap: var(--wdc-space-0-5);
  flex: 1;
  min-width: 0;
}
.ws-text strong {
  color: var(--wdc-text);
  font-family: var(--wdc-font-display);
  font-weight: 600;
}
.ws-text span { color: var(--wdc-text-2); font-size: 0.87rem; overflow-wrap: anywhere; }
.ws-actions { display: flex; flex-wrap: wrap; gap: var(--wdc-space-2); }
.ws-actions :deep(.el-button + .el-button) { margin-left: 0; }

@media (max-width: 700px) {
  .ws-banner { flex-wrap: wrap; }
  .ws-actions { width: 100%; }
}
</style>
