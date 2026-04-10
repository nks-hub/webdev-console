<template>
  <div class="app-layout">
    <header class="app-header">
      <span class="app-title">DevForge POC</span>
      <el-tag :type="store.connected ? 'success' : 'danger'" effect="dark" size="small">
        {{ store.connected ? 'Daemon Online' : 'Daemon Offline' }}
      </el-tag>
    </header>

    <main class="app-main">
      <ServiceCard />
    </main>
  </div>
</template>

<script setup lang="ts">
import { onMounted, onUnmounted } from 'vue'
import { useDaemonStore } from './stores/daemon'
import ServiceCard from './components/ServiceCard.vue'

const store = useDaemonStore()
onMounted(() => store.startPolling())
onUnmounted(() => store.stopPolling())
</script>

<style>
:root {
  color-scheme: dark;
}
* { box-sizing: border-box; margin: 0; padding: 0; }
body { background: #0f0f1a; color: #e0e0e0; font-family: system-ui, sans-serif; }
.app-layout { display: flex; flex-direction: column; height: 100vh; }
.app-header {
  display: flex; align-items: center; justify-content: space-between;
  padding: 12px 24px;
  background: #1a1a2e;
  border-bottom: 1px solid #2a2a4a;
  -webkit-app-region: drag;
}
.app-title { font-size: 1.1rem; font-weight: 700; letter-spacing: 0.05em; }
.app-main {
  flex: 1; display: flex; align-items: center; justify-content: center;
  padding: 32px;
}
</style>
