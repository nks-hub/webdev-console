<template>
  <div class="app-root">
    <AppHeader />

    <div class="app-body">
      <AppSidebar />
      <main class="content-area">
        <router-view />
      </main>
    </div>

    <AppStatusBar />
  </div>
</template>

<script setup lang="ts">
import { onMounted, onUnmounted } from 'vue'
import AppHeader from './components/layout/AppHeader.vue'
import AppSidebar from './components/layout/AppSidebar.vue'
import AppStatusBar from './components/layout/AppStatusBar.vue'
import { useDaemonStore } from './stores/daemon'
import { usePluginsStore } from './stores/plugins'
import { useThemeStore } from './stores/theme'

const daemonStore = useDaemonStore()
const pluginsStore = usePluginsStore()
// Initialize theme store - applies saved theme preference on startup
useThemeStore()

onMounted(() => {
  daemonStore.startPolling()
  void pluginsStore.loadAll()
})

onUnmounted(() => {
  daemonStore.stopPolling()
})
</script>

<style>
:root {
  --wdc-bg:      #0f1117;
  --wdc-surface: #1a1d27;
  --wdc-elevated:#242736;
  --wdc-text:    #e8eaf0;
  --wdc-status-running:  #22c55e;
  --wdc-status-stopped:  #64748b;
  --wdc-status-error:    #ef4444;
  --wdc-status-starting: #f59e0b;
  color-scheme: dark;
}

html:not(.dark) {
  --wdc-bg:      #f5f5f7;
  --wdc-surface: #ffffff;
  --wdc-elevated:#e8e8ec;
  --wdc-text:    #1a1a2e;
  --wdc-status-running:  #16a34a;
  --wdc-status-stopped:  #94a3b8;
  --wdc-status-error:    #dc2626;
  --wdc-status-starting: #d97706;
  color-scheme: light;
}

* { box-sizing: border-box; margin: 0; padding: 0; }
body { background: var(--wdc-bg); color: var(--wdc-text); font-family: system-ui, sans-serif; }

.app-root {
  display: flex;
  flex-direction: column;
  height: 100vh;
  overflow: hidden;
  min-width: 900px;
  min-height: 600px;
}

.app-body {
  display: flex;
  flex: 1;
  overflow: hidden;
}

.content-area {
  flex: 1;
  overflow-y: auto;
  background: var(--wdc-bg);
}

@media (prefers-reduced-motion: reduce) {
  *, *::before, *::after {
    animation-duration: 0.01ms !important;
    transition-duration: 0.01ms !important;
  }
}
</style>
