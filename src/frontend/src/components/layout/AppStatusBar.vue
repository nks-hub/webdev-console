<template>
  <footer class="status-bar">
    <!-- Daemon connection -->
    <span class="status-item">
      <span class="dot" :class="daemonStore.connected ? 'dot-ok' : 'dot-err'" />
      <span>Daemon</span>
    </span>

    <span class="status-sep">|</span>

    <!-- Services running -->
    <span class="status-item">
      <span class="dot dot-ok" v-if="runningCount > 0" />
      <span class="dot dot-err" v-else />
      <span>{{ runningCount }}/{{ totalCount }} running</span>
    </span>

    <!-- Crashed indicator -->
    <span class="status-item status-alert" v-if="crashedCount > 0">
      <span class="dot dot-err" />
      <span>{{ crashedCount }} crashed</span>
    </span>

    <!-- CPU/RAM totals when connected -->
    <template v-if="daemonStore.connected && totalCpu > 0">
      <span class="status-sep">|</span>
      <span class="status-item">
        <span class="mono">CPU {{ totalCpu.toFixed(1) }}%</span>
      </span>
      <span class="status-item" v-if="totalRam > 0">
        <span class="mono">RAM {{ formatMem(totalRam) }}</span>
      </span>
    </template>

    <!-- Right side: version -->
    <span class="status-item" style="margin-left: auto">
      <span class="mono">v{{ appVersion }}</span>
    </span>
  </footer>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { useDaemonStore } from '../../stores/daemon'

const daemonStore = useDaemonStore()
const appVersion = import.meta.env.VITE_APP_VERSION as string | undefined ?? '0.1.0'

const services = computed(() => daemonStore.services as any[])
const totalCount = computed(() => services.value.length)
const runningCount = computed(() =>
  services.value.filter(s => s.state === 2 || s.status === 'running').length
)
const crashedCount = computed(() =>
  services.value.filter(s => s.state === 4).length
)
const totalCpu = computed(() =>
  services.value.reduce((sum: number, s: any) => sum + (s.cpuPercent ?? 0), 0)
)
const totalRam = computed(() =>
  services.value.reduce((sum: number, s: any) => sum + (s.memoryBytes ?? 0), 0)
)

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
  gap: 10px;
  height: 26px;
  padding: 0 14px;
  background: var(--wdc-surface);
  border-top: 1px solid var(--el-border-color);
  font-size: 0.72rem;
  color: var(--el-text-color-secondary);
  flex-shrink: 0;
  letter-spacing: 0.01em;
}

.status-item {
  display: flex;
  align-items: center;
  gap: 4px;
  white-space: nowrap;
}

.status-alert { color: var(--wdc-status-error); }

.status-sep {
  color: var(--el-border-color-darker, #444);
  user-select: none;
}

.dot {
  display: inline-block;
  width: 5px;
  height: 5px;
  border-radius: 50%;
  flex-shrink: 0;
}

.dot-ok  { background: var(--wdc-status-running); }
.dot-err { background: var(--wdc-status-error); }

.mono { font-family: monospace; }
</style>
