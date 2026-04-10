<template>
  <footer class="status-bar">
    <span class="status-item">
      <span class="dot" :class="daemonStore.connected ? 'dot-ok' : 'dot-err'" />
      Daemon
    </span>
    <span class="status-item">
      {{ runningCount }} / {{ totalCount }} services running
    </span>
    <span class="status-item" style="margin-left: auto">
      NKS WDC {{ appVersion }}
    </span>
  </footer>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { useDaemonStore } from '../../stores/daemon'

const daemonStore = useDaemonStore()
const appVersion = import.meta.env.VITE_APP_VERSION as string | undefined ?? '0.1.0'

const totalCount = computed(() => daemonStore.status?.services.length ?? 0)
const runningCount = computed(() =>
  daemonStore.status?.services.filter(s => s.status === 'running').length ?? 0
)
</script>

<style scoped>
.status-bar {
  display: flex;
  align-items: center;
  gap: 16px;
  height: 24px;
  padding: 0 12px;
  background: var(--wdc-surface);
  border-top: 1px solid var(--el-border-color);
  font-size: 0.75rem;
  color: var(--el-text-color-secondary);
  flex-shrink: 0;
}
.status-item { display: flex; align-items: center; gap: 5px; }
.dot { display: inline-block; width: 6px; height: 6px; border-radius: 50%; }
.dot-ok  { background: var(--wdc-status-running); }
.dot-err { background: var(--wdc-status-error); }
</style>
