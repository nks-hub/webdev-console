<template>
  <header class="app-header" :style="{ WebkitAppRegion: 'drag' } as any">
    <div class="header-left" style="-webkit-app-region: no-drag">
      <span class="app-logo">NKS WDC</span>
    </div>

    <div class="header-center service-dots" style="-webkit-app-region: no-drag">
      <el-tooltip
        v-for="service in topServices"
        :key="service.id"
        :content="`${service.name}: ${service.status}`"
        placement="bottom"
      >
        <span class="service-dot" :class="[`dot-${service.status}`]" />
      </el-tooltip>
    </div>

    <div class="header-right" style="-webkit-app-region: no-drag">
      <el-tag :type="daemonStore.connected ? 'success' : 'danger'" size="small" effect="dark">
        {{ daemonStore.connected ? 'Connected' : 'Offline' }}
      </el-tag>

      <el-button circle size="small" @click="toggleTheme">
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

const topServices = computed(() =>
  (daemonStore.status?.services ?? []).slice(0, 5)
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
.app-logo { font-size: 1rem; font-weight: 700; letter-spacing: 0.04em; }
.header-center { display: flex; align-items: center; gap: 8px; }
.header-right { display: flex; align-items: center; gap: 10px; }
.service-dot {
  display: inline-block;
  width: 9px;
  height: 9px;
  border-radius: 50%;
}
.dot-running  { background: #22c55e; box-shadow: 0 0 4px #22c55e80; }
.dot-stopped  { background: #64748b; }
.dot-error    { background: #ef4444; box-shadow: 0 0 4px #ef444480; }
.dot-starting { background: #f59e0b; }
</style>
