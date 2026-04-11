<template>
  <div class="app-root">
    <div class="app-backdrop" />
    <AppHeader />

    <div class="app-body">
      <AppSidebar />
      <main class="content-area">
        <!-- keep-alive caches mounted page components so clicking a nav
             item doesn't re-mount the whole tree AND re-run onMounted's
             fetch cascade every time. This is the biggest win for the
             "clicking feels slow" complaint in dev mode, where Vite's
             module graph warm-up overlaps with SSE reconnect and store
             re-hydration. Excluding SiteEdit because it's keyed by
             :domain and needs fresh state per domain. -->
        <router-view v-slot="{ Component }">
          <keep-alive :exclude="['SiteEdit']">
            <component :is="Component" />
          </keep-alive>
        </router-view>
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
  position: relative;
  height: 100vh;
  overflow: hidden;
  min-width: 860px;
  min-height: 580px;
  background: var(--wdc-bg);
}

.app-backdrop {
  position: absolute;
  inset: 0;
  pointer-events: none;
  background:
    radial-gradient(circle at top left, rgba(86, 194, 255, 0.13), transparent 24%),
    radial-gradient(circle at 80% 10%, rgba(124, 255, 165, 0.06), transparent 20%),
    linear-gradient(180deg, rgba(255, 255, 255, 0.02), transparent 18%);
}

.app-body {
  display: flex;
  flex: 1;
  overflow: hidden;
  position: relative;
  z-index: 1;
}

.content-area {
  flex: 1;
  overflow-y: auto;
  overflow-x: hidden;
  background: transparent;
  scroll-behavior: smooth;
  padding: 0 0 18px;
}
</style>
