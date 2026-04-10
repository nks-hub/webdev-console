<template>
  <header class="app-header" :style="{ WebkitAppRegion: 'drag' } as any">
    <div class="header-left" style="-webkit-app-region: no-drag">
      <span class="app-logo">NKS WDC</span>
    </div>

    <!-- Service dots in centre -->
    <div class="header-center" style="-webkit-app-region: no-drag">
      <el-tooltip
        v-for="service in topServices"
        :key="service.id"
        :content="`${service.name ?? service.displayName ?? service.id}: ${stateLabel(service.state)}`"
        placement="bottom"
      >
        <span class="service-dot" :class="[`dot-${stateLabel(service.state)}`]" />
      </el-tooltip>
    </div>

    <div class="header-right" style="-webkit-app-region: no-drag">
      <!-- Connection status -->
      <div class="conn-status" :class="daemonStore.connected ? 'conn-ok' : 'conn-err'">
        <span class="conn-dot" />
        <span class="conn-label">{{ daemonStore.connected ? 'Connected' : 'Offline' }}</span>
      </div>

      <!-- Theme toggle -->
      <el-button circle size="small" @click="toggleTheme" title="Toggle theme">
        <el-icon><Moon v-if="isDark" /><Sunny v-else /></el-icon>
      </el-button>
    </div>
  </header>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { Moon, Sunny } from '@element-plus/icons-vue'
import { useDaemonStore } from '../../stores/daemon'
import { useThemeStore } from '../../stores/theme'

const daemonStore = useDaemonStore()
const themeStore = useThemeStore()
const isDark = computed(() => themeStore.isDark)
function toggleTheme() { themeStore.toggle() }

const STATE_LABELS: Record<number, string> = {
  0: 'stopped', 1: 'starting', 2: 'running', 3: 'stopping', 4: 'crashed', 5: 'disabled',
}

function stateLabel(state: number | string | undefined): string {
  if (typeof state === 'number') return STATE_LABELS[state] ?? 'unknown'
  return (state as string) ?? 'unknown'
}

const topServices = computed(() =>
  (daemonStore.services as any[]).slice(0, 7)
)
</script>

<style scoped>
.app-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  height: 44px;
  padding: 0 16px;
  background: var(--wdc-surface);
  border-bottom: 1px solid var(--el-border-color);
  flex-shrink: 0;
}

.app-logo {
  font-size: 0.95rem;
  font-weight: 800;
  letter-spacing: 0.08em;
  background: linear-gradient(135deg, #6366f1, #8b5cf6);
  -webkit-background-clip: text;
  -webkit-text-fill-color: transparent;
  background-clip: text;
}

.header-center {
  display: flex;
  align-items: center;
  gap: 7px;
}

.header-right {
  display: flex;
  align-items: center;
  gap: 10px;
}

.service-dot {
  display: inline-block;
  width: 9px;
  height: 9px;
  border-radius: 50%;
  cursor: default;
}

.dot-running  { background: var(--wdc-status-running);  box-shadow: 0 0 5px color-mix(in srgb, var(--wdc-status-running) 60%, transparent); }
.dot-stopped  { background: var(--wdc-status-stopped); }
.dot-crashed  { background: var(--wdc-status-error);    box-shadow: 0 0 5px color-mix(in srgb, var(--wdc-status-error) 60%, transparent); }
.dot-starting { background: var(--wdc-status-starting); animation: blink 1s infinite; }
.dot-stopping { background: var(--wdc-status-starting); animation: blink 1s infinite; }
.dot-disabled { background: var(--el-border-color); }
.dot-unknown  { background: var(--el-border-color); }

@keyframes blink {
  0%, 100% { opacity: 1; }
  50% { opacity: 0.35; }
}

/* Connection status pill */
.conn-status {
  display: flex;
  align-items: center;
  gap: 5px;
  padding: 2px 9px;
  border-radius: 20px;
  font-size: 0.72rem;
  font-weight: 500;
  letter-spacing: 0.02em;
  border: 1px solid;
}

.conn-ok {
  color: var(--wdc-status-running);
  border-color: color-mix(in srgb, var(--wdc-status-running) 40%, transparent);
  background: color-mix(in srgb, var(--wdc-status-running) 8%, transparent);
}

.conn-err {
  color: var(--wdc-status-error);
  border-color: color-mix(in srgb, var(--wdc-status-error) 40%, transparent);
  background: color-mix(in srgb, var(--wdc-status-error) 8%, transparent);
}

.conn-dot {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: currentColor;
}

.conn-ok .conn-dot {
  animation: pulse-ok 2s infinite;
}

@keyframes pulse-ok {
  0%, 100% { opacity: 1; }
  50% { opacity: 0.5; }
}

.conn-label { line-height: 1; }
</style>
