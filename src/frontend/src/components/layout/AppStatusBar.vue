<template>
  <footer class="status-bar">
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

    <template v-if="daemonStore.connected && totalRam > 0">
      <span class="status-sep" />
      <span class="status-item mono">CPU {{ totalCpu.toFixed(1) }}%</span>
      <span class="status-item mono">RAM {{ formatMem(totalRam) }}</span>
    </template>

    <span class="status-right mono">NKS WDC v{{ appVersion }}</span>
  </footer>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { useDaemonStore } from '../../stores/daemon'

const daemonStore = useDaemonStore()
const appVersion = import.meta.env.VITE_APP_VERSION as string | undefined ?? '0.1.0'

const services = computed(() => daemonStore.services as any[])
const totalCount = computed(() => services.value.length)
const runningCount = computed(() => services.value.filter(s => s.state === 2).length)
const crashedCount = computed(() => services.value.filter(s => s.state === 4).length)
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
  gap: 12px;
  height: 28px;
  padding: 0 16px;
  background: var(--wdc-surface);
  border-top: 1px solid var(--wdc-border);
  font-size: 0.75rem;
  color: var(--wdc-text-2);
  flex-shrink: 0;
}

.status-item {
  display: flex;
  align-items: center;
  gap: 5px;
  white-space: nowrap;
}

.status-alert { color: var(--wdc-status-error); font-weight: 600; }

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

.mono {
  font-family: 'JetBrains Mono', 'Cascadia Code', monospace;
  font-variant-numeric: tabular-nums;
}

.status-right {
  margin-left: auto;
  color: var(--wdc-text-3);
}
</style>
