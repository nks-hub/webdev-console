<template>
  <footer class="status-bar" :class="{ 'status-bar-simple': uiMode.isSimple }">
    <!-- Simple mode: one plain sentence about the whole stack -->
    <template v-if="uiMode.isSimple">
      <span class="status-item status-item-center" role="status">
        <HealthStatusDot :level="simpleLevel" :pulse="simpleLevel === 'ok'" :title="simpleText" />
        <router-link v-if="crashedCount > 0" class="status-link status-alert" :to="problemsLink">{{ simpleText }}</router-link>
        <span v-else>{{ simpleText }}</span>
      </span>
      <span class="status-right mono">{{ versionLabel }}</span>
    </template>

    <!-- Advanced mode: full status bar -->
    <template v-else>
      <span class="status-item">
        <HealthStatusDot :level="daemonStore.connected ? 'ok' : 'err'" :title="daemonStore.connected ? $t('header.connected') : $t('header.offline')" />
        <span>{{ t('footer.daemon') }}</span>
      </span>

      <span class="status-sep" />

      <router-link class="status-item status-link" to="/services">
        {{ t('footer.servicesRunning', { running: runningCount, total: totalCount }, totalCount) }}
      </router-link>

      <router-link v-if="crashedCount > 0" class="status-item status-link status-alert" :to="problemsLink">
        {{ t('footer.problems', { n: crashedCount }, crashedCount) }}
      </router-link>

      <span
        v-if="tunnelRunning"
        class="status-item status-tunnel"
        :title="t('footer.tunnelTitle')"
      >
        <svg viewBox="0 0 20 14" fill="currentColor" width="13" height="13" style="vertical-align: middle; margin-right: var(--wdc-space-1)"><path d="M16 6a4 4 0 0 0-7.74-1.32A3.5 3.5 0 1 0 3.5 11H16a3 3 0 0 0 0-6z"/></svg>{{ t('footer.tunnel') }}
      </span>

      <template v-if="daemonStore.connected && totalRam > 0">
        <span class="status-sep" />
        <span class="status-item mono">CPU {{ formatPercent(totalCpu, localeName) }}</span>
        <span class="status-item mono">RAM {{ formatBytes(totalRam, localeName) }}</span>
      </template>

      <span class="status-right mono">{{ versionLabel }}</span>
    </template>
  </footer>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { useDaemonStore } from '../../stores/daemon'
import { useUiModeStore } from '../../stores/uiMode'
import HealthStatusDot from '../shared/HealthStatusDot.vue'
import { useAppVersion } from '../../utils/appVersion'
import { isRunning } from '../../utils/serviceState'
import { formatBytes, formatPercent } from '../../utils/format'

const { t, locale } = useI18n()
const daemonStore = useDaemonStore()
const uiMode = useUiModeStore()
const versionRef = useAppVersion()
const versionLabel = computed(() => `${t('app.short')} v${versionRef.value.full}`)
const localeName = computed(() => String(locale.value))

// The same set and counting the Overview uses (visible plugins only).
const services = computed(() => daemonStore.visibleServices)
const totalCount = computed(() => services.value.length)
const runningCount = computed(() => daemonStore.runningServices.length)
const crashedCount = computed(() => daemonStore.problemServices.length)
const problemsLink = { path: '/services', query: { filter: 'problems' } }
const tunnelRunning = computed(() => services.value.some(s => s.id === 'cloudflare' && isRunning(s)))
const totalCpu = computed(() => services.value.reduce((sum, s) => sum + (s.cpuPercent ?? 0), 0))
const totalRam = computed(() => services.value.reduce((sum, s) => sum + (s.memoryBytes ?? 0), 0))

const simpleLevel = computed<'ok' | 'warn' | 'err' | 'muted'>(() => {
  if (!daemonStore.connected || crashedCount.value > 0) return 'err'
  return allInstalledRunning.value ? 'ok' : 'muted'
})
// Services that are not installed are neither running nor a fault.
const allInstalledRunning = computed(() => {
  const installed = services.value.filter(s => s.installed !== false)
  return installed.length > 0 && installed.every(isRunning)
})
const simpleText = computed(() => {
  if (!daemonStore.connected) return t('footer.simpleOffline')
  if (crashedCount.value > 0) return t('footer.simpleProblems', { n: crashedCount.value }, crashedCount.value)
  if (allInstalledRunning.value) return t('footer.simpleAllRunning')
  return t('footer.servicesRunning', { running: runningCount.value, total: totalCount.value }, totalCount.value)
})
</script>

<style scoped>
.status-bar {
  display: flex;
  align-items: center;
  gap: var(--wdc-space-3-5);
  height: 30px;
  padding: 0 var(--wdc-space-4);
  background: color-mix(in srgb, var(--wdc-surface) 78%, transparent);
  backdrop-filter: blur(12px) saturate(1.2);
  -webkit-backdrop-filter: blur(12px) saturate(1.2);
  border-top: 1px solid var(--wdc-border);
  font-size: 0.78rem;
  color: var(--wdc-text-2);
  flex-shrink: 0;
}

.status-bar-simple {
  position: relative;
}

.status-item-center {
  position: absolute;
  left: 50%;
  transform: translateX(-50%);
}

.status-item {
  display: flex;
  align-items: center;
  gap: var(--wdc-space-1);
  white-space: nowrap;
}

.status-link {
  color: inherit;
  text-decoration: none;
  border-radius: var(--wdc-radius-xs);
}
@media (hover: hover) {
  .status-link:hover { color: var(--wdc-text); text-decoration: underline; }
}
.status-link:focus-visible { outline: 2px solid var(--wdc-accent); outline-offset: 2px; }
.status-alert { color: var(--wdc-status-error); font-weight: 600; }
.status-tunnel { color: var(--wdc-tunnel-text); font-weight: 700; }
:global(.dark) .status-tunnel,
:global(html.dark) .status-tunnel { color: var(--wdc-tunnel-text) !important; }

.status-sep {
  width: 1px;
  height: 12px;
  background: var(--wdc-border-strong);
}

/* Daemon connection dot moved to HealthStatusDot shared primitive
   (plan §6). Old .dot/.dot-ok/.dot-err rules deleted; the pulse
   animation now lives inside HealthStatusDot. */

.mono {
  font-family: 'JetBrains Mono', 'Cascadia Code', monospace;
  font-variant-numeric: tabular-nums;
}

.status-right {
  margin-left: auto;
  color: var(--wdc-text-3);
}

@media (max-width: 520px) {
  .status-bar {
    gap: var(--wdc-space-2);
    padding: 0 var(--wdc-space-3-5);
  }

  .status-bar:not(.status-bar-simple) {
    gap: var(--wdc-space-1-5);
    overflow: hidden;
  }

  .status-bar:not(.status-bar-simple) .status-sep,
  .status-bar:not(.status-bar-simple) .status-right {
    display: none;
  }

  .status-bar-simple {
    justify-content: center;
  }

  .status-bar-simple .status-item-center {
    position: static;
    transform: none;
  }

  .status-bar-simple .status-right {
    display: none;
  }
}
</style>
