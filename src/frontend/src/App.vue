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

    <!-- Splash overlay shown until the daemon answers for the first time.
         Distinguishes "backend still booting" from "runtime offline". -->
    <Transition name="splash-fade">
      <div v-if="!daemonStore.hasEverConnected" class="splash-overlay">
        <div class="splash-card">
          <div class="splash-logo">NKS</div>
          <div class="splash-title">WebDev Console</div>
          <div class="splash-spinner" aria-hidden="true"></div>
          <div class="splash-status">Waiting for backend…</div>
          <div class="splash-hint">{{ splashHint }}</div>
        </div>
      </div>
    </Transition>
  </div>
</template>

<script setup lang="ts">
import { onMounted, onUnmounted, ref, computed } from 'vue'
import { useRouter } from 'vue-router'
import AppHeader from './components/layout/AppHeader.vue'
import AppSidebar from './components/layout/AppSidebar.vue'
import AppStatusBar from './components/layout/AppStatusBar.vue'
import CommandPalette from './components/shared/CommandPalette.vue'
import OnboardingWizard from './components/shared/OnboardingWizard.vue'
import { useDaemonStore } from './stores/daemon'
import { usePluginsStore } from './stores/plugins'
import { useSitesStore } from './stores/sites'
import { useThemeStore } from './stores/theme'

const router = useRouter()
const daemonStore = useDaemonStore()
const pluginsStore = usePluginsStore()
const sitesStore = useSitesStore()
useThemeStore()

const commandPalette = ref<InstanceType<typeof CommandPalette> | null>(null)

// Splash hint rotates every 2s to reassure the user something is happening
// during the longest-case boot (daemon compile + plugin init ≈ 8s in dev).
const splashHints = [
  'Starting C# daemon…',
  'Loading plugins…',
  'Reading config…',
  'Ready soon…',
]
const splashHintIdx = ref(0)
const splashHint = computed(() => splashHints[splashHintIdx.value % splashHints.length])
let splashTimer: ReturnType<typeof setInterval> | null = null

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

let unsubscribeConnect: (() => void) | null = null

onMounted(() => {
  daemonStore.startPolling()

  // Refetch the stores that fed the cold boot once the daemon comes online.
  // Fires on FIRST successful poll and every time the daemon reconnects
  // (dev-mode restart, token rotation, etc.) — so if the user was looking at
  // /sites while the daemon was still compiling, the list auto-populates
  // the moment the backend is up instead of staying empty until manual reload.
  unsubscribeConnect = daemonStore.onConnect(() => {
    void pluginsStore.loadAll()
    void sitesStore.load()
  })

  // Rotate splash hint so the "Waiting for backend" spinner never feels
  // frozen. Cleared automatically once hasEverConnected flips to true —
  // but the timer itself runs for the full session (cheap, 2s tick).
  splashTimer = setInterval(() => {
    if (!daemonStore.hasEverConnected) splashHintIdx.value++
  }, 2000)

  window.addEventListener('keydown', handleKeydown)
})

onUnmounted(() => {
  daemonStore.stopPolling()
  unsubscribeConnect?.()
  if (splashTimer) { clearInterval(splashTimer); splashTimer = null }
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

/* ═══ Splash overlay — shown until backend first connects ════════════════ */
.splash-overlay {
  position: fixed;
  inset: 0;
  z-index: 10000;
  display: flex;
  align-items: center;
  justify-content: center;
  background: radial-gradient(circle at 50% 40%, rgba(86, 194, 255, 0.08), var(--wdc-bg) 60%);
  backdrop-filter: blur(4px);
}

.splash-card {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 16px;
  padding: 44px 64px;
  background: var(--wdc-surface);
  border: 1px solid var(--wdc-border-strong);
  border-radius: 14px;
  box-shadow: 0 20px 60px rgba(0, 0, 0, 0.55);
  min-width: 360px;
}

.splash-logo {
  font-family: 'JetBrains Mono', monospace;
  font-size: 2rem;
  font-weight: 800;
  color: var(--wdc-accent);
  letter-spacing: 0.1em;
  line-height: 1;
}

.splash-title {
  font-size: 1.1rem;
  font-weight: 700;
  color: var(--wdc-text);
  letter-spacing: -0.01em;
}

.splash-spinner {
  width: 48px;
  height: 48px;
  border: 3px solid var(--wdc-border);
  border-top-color: var(--wdc-accent);
  border-radius: 50%;
  animation: wdc-spin 0.9s linear infinite;
  margin: 4px 0;
}

.splash-status {
  font-size: 0.88rem;
  font-weight: 600;
  color: var(--wdc-text-2);
}

.splash-hint {
  font-size: 0.78rem;
  font-family: 'JetBrains Mono', monospace;
  color: var(--wdc-text-3);
  font-weight: 500;
  min-height: 1.2em;
}

.splash-fade-enter-active,
.splash-fade-leave-active {
  transition: opacity 0.35s ease;
}
.splash-fade-enter-from,
.splash-fade-leave-to {
  opacity: 0;
}
</style>
