<template>
  <header class="app-header" :style="{ WebkitAppRegion: 'drag' } as any">
    <div class="header-left" style="-webkit-app-region: no-drag">
      <span class="app-logo">NKS WDC</span>
    </div>

    <div class="header-right" style="-webkit-app-region: no-drag">
      <div class="conn-pill" :class="daemonStore.connected ? 'conn-ok' : 'conn-err'">
        <span class="conn-dot" />
        {{ daemonStore.connected ? 'Connected' : 'Offline' }}
      </div>

      <el-button circle size="small" @click="toggleTheme" :title="isDark ? 'Light mode' : 'Dark mode'">
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
</script>

<style scoped>
.app-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  height: 40px;
  padding: 0 16px;
  background: var(--wdc-surface);
  border-bottom: 1px solid var(--wdc-border);
  flex-shrink: 0;
}

.app-logo {
  font-size: 0.9rem;
  font-weight: 800;
  letter-spacing: 0.06em;
  color: var(--wdc-accent);
}

.header-right {
  display: flex;
  align-items: center;
  gap: 10px;
}

.conn-pill {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 3px 10px;
  border-radius: 20px;
  font-size: 0.75rem;
  font-weight: 500;
  border: 1px solid;
}

.conn-ok {
  color: var(--wdc-status-running);
  border-color: rgba(34, 197, 94, 0.3);
  background: rgba(34, 197, 94, 0.06);
}

.conn-err {
  color: var(--wdc-status-error);
  border-color: rgba(239, 68, 68, 0.3);
  background: rgba(239, 68, 68, 0.06);
}

.conn-dot {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: currentColor;
}

.conn-ok .conn-dot {
  animation: glow 2s ease-in-out infinite;
}

@keyframes glow {
  0%, 100% { opacity: 1; }
  50% { opacity: 0.4; }
}
</style>
