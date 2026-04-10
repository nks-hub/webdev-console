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
.app-root {
  display: flex;
  flex-direction: column;
  height: 100vh;
  overflow: hidden;
  min-width: 860px;
  min-height: 580px;
  background: var(--wdc-bg);
}

.app-body {
  display: flex;
  flex: 1;
  overflow: hidden;
}

.content-area {
  flex: 1;
  overflow-y: auto;
  overflow-x: hidden;
  background: var(--wdc-bg);
  scroll-behavior: smooth;
}
</style>
