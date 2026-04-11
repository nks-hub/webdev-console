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
    <CommandPalette ref="commandPalette" />
    <OnboardingWizard />
  </div>
</template>

<script setup lang="ts">
import { onMounted, onUnmounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import AppHeader from './components/layout/AppHeader.vue'
import AppSidebar from './components/layout/AppSidebar.vue'
import AppStatusBar from './components/layout/AppStatusBar.vue'
import CommandPalette from './components/shared/CommandPalette.vue'
import OnboardingWizard from './components/shared/OnboardingWizard.vue'
import { useDaemonStore } from './stores/daemon'
import { usePluginsStore } from './stores/plugins'
import { useThemeStore } from './stores/theme'

const router = useRouter()
const daemonStore = useDaemonStore()
const pluginsStore = usePluginsStore()
useThemeStore()

const commandPalette = ref<InstanceType<typeof CommandPalette> | null>(null)

function handleKeydown(e: KeyboardEvent) {
  if ((e.ctrlKey || e.metaKey) && e.key === 'k') {
    e.preventDefault()
    commandPalette.value?.open()
  }
  if (e.key === 'F5') {
    e.preventDefault()
    daemonStore.poll()
  }
  if ((e.ctrlKey || e.metaKey) && e.key === 'n') {
    e.preventDefault()
    router.push({ path: '/sites', query: { create: '1' } })
  }
  // Ctrl+1-7 navigation
  if ((e.ctrlKey || e.metaKey) && e.key >= '1' && e.key <= '7') {
    e.preventDefault()
    const routes = ['/dashboard', '/sites', '/databases', '/ssl', '/php', '/binaries', '/settings']
    const idx = parseInt(e.key) - 1
    if (idx < routes.length) router.push(routes[idx])
  }
}

onMounted(() => {
  daemonStore.startPolling()
  void pluginsStore.loadAll()
  window.addEventListener('keydown', handleKeydown)
})

onUnmounted(() => {
  daemonStore.stopPolling()
  window.removeEventListener('keydown', handleKeydown)
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
