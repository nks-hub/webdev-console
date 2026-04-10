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

const daemonStore = useDaemonStore()
const pluginsStore = usePluginsStore()

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
  --df-bg:      #0f1117;
  --df-surface: #1a1d27;
  --df-elevated:#242736;
  color-scheme: dark;
}

* { box-sizing: border-box; margin: 0; padding: 0; }
body { background: var(--df-bg); color: #e8eaf0; font-family: system-ui, sans-serif; }

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
  background: var(--df-bg);
}
</style>
