<template>
  <footer class="status-bar" :class="{ 'status-bar-simple': uiMode.isSimple }">
    <!-- Simple mode: centered daemon status only -->
    <template v-if="uiMode.isSimple">
      <span class="status-item status-item-center">
        <span class="dot" :class="daemonStore.connected ? 'dot-ok dot-ok-pulse' : 'dot-err'" />
        <span>{{ daemonStore.connected ? 'Daemon běží' : 'Daemon offline' }}</span>
      </span>
      <span class="status-right mono">NKS WDC v{{ appVersion }}</span>
    </template>

    <!-- Advanced mode: full status bar -->
    <template v-else>
      <span class="status-item">
        <span class="dot" :class="daemonStore.connected ? 'dot-ok' : 'dot-err'" />
        <span>Daemon</span>
      </span>

      <span class="status-sep" />

      <span class="status-item">
        {{ runningCount }}/{{ totalCount }} services running
      </span>

      <span class="status-item status-alert" v-if="crashedCount > 0">
        {{ crashedCount }} crashed
      </span>

      <span
        v-if="tunnelRunning"
        class="status-item status-tunnel"
        title="Cloudflare Tunnel is running — exposed sites are publicly reachable"
      >
        <svg viewBox="0 0 20 14" fill="currentColor" width="13" height="13" style="vertical-align: middle; margin-right: 3px"><path d="M16 6a4 4 0 0 0-7.74-1.32A3.5 3.5 0 1 0 3.5 11H16a3 3 0 0 0 0-6z"/></svg>Tunnel
      </span>

      <template v-if="daemonStore.connected && totalRam > 0">
        <span class="status-sep" />
        <span class="status-item mono">CPU {{ totalCpu.toFixed(1) }}%</span>
        <span class="status-item mono">RAM {{ formatMem(totalRam) }}</span>
      </template>

      <span class="status-right mono">NKS WDC v{{ appVersion }}</span>
    </template>
  </footer>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { useDaemonStore } from '../../stores/daemon'
import { useUiModeStore } from '../../stores/uiMode'

const daemonStore = useDaemonStore()
const uiMode = useUiModeStore()
const appVersion = import.meta.env.VITE_APP_VERSION as string | undefined ?? '0.1.0'

const services = computed(() => daemonStore.services as any[])
const totalCount = computed(() => services.value.length)
const runningCount = computed(() => services.value.filter(s => s.state === 2).length)
const crashedCount = computed(() => services.value.filter(s => s.state === 4).length)
const tunnelRunning = computed(() =>
  services.value.some(s => s.id === 'cloudflare' && (s.state === 2 || s.status === 'running'))
)
const totalCpu = computed(() => services.value.reduce((sum: number, s: any) => sum + (s.cpuPercent ?? 0), 0))
const totalRam = computed(() => services.value.reduce((sum: number, s: any) => sum + (s.memoryBytes ?? 0), 0))

function formatMem(bytes: number): string {
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(0)} KB`
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / 1024 / 1024).toFixed(0)} MB`
  return `${(bytes / 1024 / 1024 / 1024).toFixed(1)} GB`
}
</script>

<style scoped>
.status-bar {
  display: flex;
  align-items: center;
  gap: 14px;
  height: 30px;
  padding: 0 16px;
  background: var(--wdc-surface);
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
  gap: 5px;
  white-space: nowrap;
}

.status-alert { color: var(--wdc-status-error); font-weight: 600; }
.status-tunnel { color: #f38020; font-weight: 600; }

.status-sep {
  width: 1px;
  height: 12px;
  background: rgba(255, 255, 255, 0.15);
}

.dot {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  flex-shrink: 0;
}
.dot-ok  { background: var(--wdc-status-running); }
.dot-err { background: var(--wdc-status-error); }

.dot-ok-pulse {
  animation: dot-pulse 2s ease-in-out infinite;
}

@keyframes dot-pulse {
  0%, 100% { box-shadow: 0 0 0 0 rgba(34, 197, 94, 0.55); }
  50%       { box-shadow: 0 0 0 4px rgba(34, 197, 94, 0); }
}

.mono {
  font-family: 'JetBrains Mono', 'Cascadia Code', monospace;
  font-variant-numeric: tabular-nums;
}

.status-right {
  margin-left: auto;
  color: var(--wdc-text-3);
}
</style>
