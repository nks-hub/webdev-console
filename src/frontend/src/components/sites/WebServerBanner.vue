<template>
  <div v-if="kind" class="ws-banner" :class="`ws-${kind}`" role="status">
    <el-icon :class="{ 'is-loading': kind === 'starting' }" :size="18" aria-hidden="true">
      <Loading v-if="kind === 'starting'" />
      <WarningFilled v-else />
    </el-icon>
    <div class="ws-text">
      <strong>{{ $t(`sites.banner.${kind}.title`) }}</strong>
      <span>{{ kind === 'failed' ? $t('sites.banner.failed.detail', { err: siteState.startError }) : $t(`sites.banner.${kind}.detail`) }}</span>
    </div>
    <div v-if="kind !== 'starting'" class="ws-actions">
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
      >{{ kind === 'failed' ? $t('common.retry') : $t('sites.banner.startApache') }}</el-button>
      <el-button v-if="kind === 'failed'" @click="router.push('/plugins/apache')">{{ $t('sites.banner.showLog') }}</el-button>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { useRouter } from 'vue-router'
import { Loading, WarningFilled } from '@element-plus/icons-vue'
import { useSiteStateStore } from '../../stores/siteState'

/**
 * The single place where "no site answers because the web server is down"
 * is said, with the button that fixes it. Replaces a "stopped" label in
 * every table row.
 */
const router = useRouter()
const siteState = useSiteStateStore()

const kind = computed<'missing' | 'stopped' | 'starting' | 'failed' | null>(() => {
  const ws = siteState.webServer
  if (ws === 'missing') return 'missing'
  if (ws === 'starting') return 'starting'
  if (ws === 'stopped') return siteState.startError ? 'failed' : 'stopped'
  return null
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
.ws-failed {
  border-color: color-mix(in oklab, var(--wdc-status-error) 55%, var(--wdc-border));
  background: color-mix(in oklab, var(--wdc-status-error) 9%, var(--wdc-surface));
}
.ws-starting {
  border-color: var(--wdc-border-strong);
  background: var(--wdc-surface-2);
}
.ws-banner > .el-icon { flex-shrink: 0; color: var(--wdc-status-starting); }
.ws-failed > .el-icon { color: var(--wdc-status-error); }
.ws-starting > .el-icon { color: var(--wdc-text-2); }
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
