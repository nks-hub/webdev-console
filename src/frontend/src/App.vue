<template>
  <div class="app-root">
    <div class="app-backdrop" />
    <AppHeader />

    <div class="app-body">
      <!-- Sidebar: always visible at >=960px; hidden below that breakpoint -->
      <AppSidebar class="sidebar-desktop" @navigate="drawerOpen = false" />

      <!-- Hamburger button — only rendered at <960px -->
      <button class="hamburger-btn" aria-label="Open menu" @click="drawerOpen = true">
        <span /><span /><span />
      </button>

      <!-- Mobile drawer sidebar -->
      <el-drawer
        v-model="drawerOpen"
        direction="ltr"
        size="256px"
        :with-header="false"
        class="sidebar-drawer"
      >
        <AppSidebar @navigate="drawerOpen = false" />
      </el-drawer>

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

    <!-- Persistent right-side deploy drawer. Mounted at the app shell so a
         long-running deploy survives navigation between sites — clicking
         away to the Sites list keeps the drawer visible. Bound to the
         deploy store's activeRun: the drawer auto-opens when a deploy
         starts and the user can close it manually. -->
    <DeployRunDrawer />

    <!-- MCP destructive-operation approval banners (Mode A). When an AI
         issues a deploy intent the daemon broadcasts mcp:confirm-request;
         the SSE handler below pushes the payload into useMcpConfirmStore.
         The banner stack is fixed top-right so it doesn't compete with
         the deploy drawer for screen real-estate. -->
    <McpConfirmBanner v-if="featureFlagsStore.showMcpSurface" />

    <!-- Splash overlay shown until the daemon answers for the first time.
         Distinguishes "backend still booting" from "runtime offline".
         Phase + elapsed-time + error-kind come from daemonStore telemetry
         (F70) so the status line actually reflects what is happening
         instead of rotating through canned hints. -->
    <Transition name="splash-fade">
      <div v-if="!daemonStore.hasEverConnected" class="splash-overlay">
        <div class="splash-card">
          <div class="splash-logo">NKS</div>
          <div class="splash-title">WebDev Console</div>
          <div class="splash-spinner" :class="{ 'splash-spinner--warn': splashWarn }" aria-hidden="true"></div>
          <div class="splash-status">{{ splashStatus }}</div>
          <div class="splash-hint">
            <span class="splash-hint-label">{{ splashHint }}</span>
            <span class="splash-hint-sep">·</span>
            <span class="splash-hint-elapsed">{{ splashElapsed }}</span>
          </div>
        </div>
      </div>
    </Transition>
  </div>
</template>

<script setup lang="ts">
import { onMounted, onUnmounted, ref, computed, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import AppHeader from './components/layout/AppHeader.vue'
import AppSidebar from './components/layout/AppSidebar.vue'
import AppStatusBar from './components/layout/AppStatusBar.vue'
import CommandPalette from './components/shared/CommandPalette.vue'
import OnboardingWizard from './components/shared/OnboardingWizard.vue'
import DeployRunDrawer from './components/deploy/DeployRunDrawer.vue'
import McpConfirmBanner from './components/mcp/McpConfirmBanner.vue'
import { useDaemonStore } from './stores/daemon'
import { useUpdatesStore } from './stores/updates'
import { usePluginsStore } from './stores/plugins'
import { useSitesStore } from './stores/sites'
import { useThemeStore } from './stores/theme'
import { useAuthStore } from './stores/auth'
import { useDeployStore } from './stores/deploy'
import { useMcpConfirmStore } from './stores/mcpConfirm'
import { useFeatureFlagsStore } from './stores/featureFlags'
import { fetchSettings, subscribeEventsMap } from './api/daemon'
import type { DeployEventDto } from './api/deploy'
import { osNotify } from './services/osNotifications'
import { showMcpConfirmToast, closeMcpConfirmToast } from './utils/mcpConfirmToast'

const router = useRouter()
const route = useRoute()
const daemonStore = useDaemonStore()
const updatesStore = useUpdatesStore()
const drawerOpen = ref(false)

// Close mobile drawer on route change
watch(() => route.path, () => { drawerOpen.value = false })
const pluginsStore = usePluginsStore()
const sitesStore = useSitesStore()
const authStore = useAuthStore()
const deployStore = useDeployStore()
const mcpConfirmStore = useMcpConfirmStore()
const featureFlagsStore = useFeatureFlagsStore()
useThemeStore()

// Single SSE subscription that fans the daemon's "deploy:event" channel
// into the deploy store. Reuses the multiplex refactor (subscribeEventsMap)
// added earlier on this branch — adding new event types here is the
// supported extension point for future plugin-emitted events.
let unsubscribeDeploy: (() => void) | null = null
function startDeploySse(): void {
  if (unsubscribeDeploy) return
  unsubscribeDeploy = subscribeEventsMap({
    'deploy:event': (data) => deployStore.handleSseEvent(data as DeployEventDto),
    // Phase 7.5+++ — hook execution feedback. RunHooksAsync emits one
    // event per hook fire so the drawer can show shell/http/php results.
    'deploy:hook': (data) =>
      deployStore.handleHookEvent(data as { deployId: string; evt: string;
        type: string; label: string; ok: boolean; durationMs: number; error?: string }),
    'mcp:confirm-request': (data) => {
      const evt = data as {
        intentId: string; prompt?: string; kind?: string; kindLabel?: string;
        domain?: string; host?: string
      }
      mcpConfirmStore.addPending(evt)
      // Phase 8 (3a) — inline approve toast in addition to the banner.
      // The banner stays authoritative for the queue + expiry; the toast
      // gives one-click Approve/Reject right where the operator's eyes are.
      void showMcpConfirmToast(evt)
      // #147 — fire OS notification so the operator sees the request
      // even when the WDC window is in the background.
      void osNotify({
        title: 'NKS WDC — MCP confirmation needed',
        body: evt.kind
          ? `Claude wants to perform ${evt.kind} — open WDC to approve.`
          : 'Claude wants to run a destructive operation — open WDC to approve.',
        urgency: 'critical',
        channel: 'mcp',
      })
    },
    // Auto-dismiss the toast when the intent is resolved through the
    // banner or another surface (used_at / revoked / matched grant).
    'mcp:intent-changed': (data) => {
      const evt = data as { intentId?: string }
      if (evt.intentId) closeMcpConfirmToast(evt.intentId)
    },
    // #147 — surface deploy completion in OS notification center so a
    // long-running deploy doesn't require the operator to keep the
    // window foregrounded to know when it finished.
    'deploy:complete': (data) => {
      const evt = data as { deployId?: string; success?: boolean; error?: string }
      void osNotify({
        title: evt.success
          ? 'NKS WDC — Deploy succeeded'
          : 'NKS WDC — Deploy failed',
        body: evt.success
          ? `Deploy ${evt.deployId?.slice(0, 8) ?? ''} completed.`
          : (evt.error ?? `Deploy ${evt.deployId?.slice(0, 8) ?? ''} failed.`),
        urgency: evt.success ? 'normal' : 'critical',
        channel: 'deploy',
      })
    },
  })
}
function stopDeploySse(): void {
  if (unsubscribeDeploy) { unsubscribeDeploy(); unsubscribeDeploy = null }
}

// F91.11: pull the authoritative SSO profile the moment the daemon is
// reachable. Without this the "Signed in as {email}" label only appeared
// after the user navigated to Settings → Account (because the refresh
// call lived in that page's script setup). We read `daemon.catalogUrl`
// from the same settings snapshot Settings.vue uses — one source of truth.
async function hydrateSsoProfile() {
  if (!authStore.isAuthenticated) return
  let catalogUrl = 'https://wdc.nks-hub.cz'
  try {
    const s = await fetchSettings() as Record<string, unknown>
    const configured = s['daemon.catalogUrl']
    if (typeof configured === 'string' && configured) catalogUrl = configured
  } catch { /* daemon flaky — fallback URL is fine */ }
  await authStore.refreshProfile(catalogUrl)
}

const commandPalette = ref<InstanceType<typeof CommandPalette> | null>(null)

// F70 splash telemetry — derive phase + elapsed time from daemonStore state
// rather than rotating through canned hints. The elapsed counter ticks once
// per second so the user can see progress even during quiet poll intervals.
const nowTs = ref(Date.now())
let splashTimer: ReturnType<typeof setInterval> | null = null

const splashElapsedSec = computed(() => {
  const start = daemonStore.bootStartedAt ?? nowTs.value
  return Math.max(0, Math.floor((nowTs.value - start) / 1000))
})
const splashElapsed = computed(() => `${splashElapsedSec.value}s`)

const splashStatus = computed(() => {
  if (!daemonStore.bootStartedAt) return 'Spouštím frontend…'
  const fails = daemonStore.consecutiveFailures
  const kind = daemonStore.lastErrorKind
  // After the daemon has bound HTTP at least once but shutters down (daemon
  // restart) we flip back to connecting state. Cover both flows here.
  if (fails === 0 && daemonStore.pollAttempts === 1) return 'Connecting to backend…'
  if (kind === 'network') {
    // Daemon process not listening yet — port file may still be missing
    // or the socket is not bound. Normal during first few seconds of boot.
    if (splashElapsedSec.value < 8) return 'Daemon se spouští…'
    if (splashElapsedSec.value < 20) return 'Daemon startuje pluginy…'
    return 'Daemon stále odpovídá pomalu…'
  }
  if (kind === 'auth') return 'Daemon běží, synchronizuji token…'
  if (kind === 'server') return 'Daemon hlásí chybu — opakuji…'
  if (kind === 'unknown' && fails > 0) return 'Čekám na backend…'
  return 'Connecting to backend…'
})

const splashHint = computed(() => {
  const kind = daemonStore.lastErrorKind
  const n = daemonStore.pollAttempts
  if (!daemonStore.bootStartedAt) return 'mount fáze'
  if (kind === 'network') return `pokus ${n} · port file / socket`
  if (kind === 'auth') return `pokus ${n} · rotace tokenu`
  if (kind === 'server') return `pokus ${n} · 5xx odpověď`
  if (n <= 1) return 'první pokus'
  return `pokus ${n}`
})

// Over ~15s of network errors we visually escalate the spinner (amber tint)
// so the user knows something is genuinely taking longer than expected.
const splashWarn = computed(() =>
  daemonStore.lastErrorKind !== null && splashElapsedSec.value > 15
)

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

// Mirror accountToken from localStorage → daemon SettingsStore on app
// boot so CatalogHeartbeatService can read it on its first tick. Before
// this, the mirror only happened when the user opened Settings.vue, so
// users who launched the app and stayed on Dashboard never pushed to
// the catalog and appeared "offline" in the cloud admin UI.
async function mirrorAuthTokenToDaemon() {
  if (!authStore.token) return
  try {
    const { saveSettings } = await import('./api/daemon')
    const email = authStore.profile?.email || authStore.identity?.email || ''
    await saveSettings({
      'sync.accountToken': authStore.token,
      ...(email ? { 'sync.accountEmail': email } : {}),
    })
  } catch { /* daemon still warming up — next poll/tick will retry */ }
}

onMounted(() => {
  daemonStore.startPolling()
  void mirrorAuthTokenToDaemon()
  // Phase 6.23 — hydrate feature flags so guards (sidebar entries, MCP
  // banner mount, etc.) flip to their real values once daemon answers.
  void featureFlagsStore.ensureLoaded()
  // F96: kick off background update check once per session. Store guards
  // against flooding the public GitHub API — refresh() honors a 6h cached
  // window, so the hourly setInterval inside startAutoCheck is idempotent.
  updatesStore.startAutoCheck()

  // Refetch the stores that fed the cold boot once the daemon comes online.
  // Fires on FIRST successful poll and every time the daemon reconnects
  // (dev-mode restart, token rotation, etc.) — so if the user was looking at
  // /sites while the daemon was still compiling, the list auto-populates
  // the moment the backend is up instead of staying empty until manual reload.
  unsubscribeConnect = daemonStore.onConnect(() => {
    void pluginsStore.loadAll()
    void sitesStore.load()
    // Retry token mirror on every daemon (re)connect — covers the case
    // where the daemon was still starting when we fired in onMounted.
    void mirrorAuthTokenToDaemon()
    // F91.11: fetch catalog profile as soon as daemon is reachable (it
    // holds the `daemon.catalogUrl` setting). Previous code only did this
    // inside Settings.vue script-setup, so the "Signed in as {email}"
    // label never appeared until the user visited the Account tab.
    void hydrateSsoProfile()
  })

  // F70: tick nowTs every second so the splash elapsed counter and
  // phase-based status update live even during quiet poll intervals.
  // Cheap — 1s setInterval, stopped on unmount.
  splashTimer = setInterval(() => { nowTs.value = Date.now() }, 1000)

  // Subscribe to deploy SSE channel — single subscription for the whole app
  // life, drives the persistent <DeployRunDrawer>.
  startDeploySse()

  window.addEventListener('keydown', handleKeydown)
})

onUnmounted(() => {
  daemonStore.stopPolling()
  unsubscribeConnect?.()
  stopDeploySse()
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
  /* 24px top — breathing room between AppHeader nav and page content.
     User feedback: previous padding-top 0 felt cramped. */
  padding: 24px 0 18px;
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
  width: 52px;
  height: 52px;
  border: 3px solid var(--wdc-border);
  border-top-color: var(--wdc-accent);
  border-radius: 50%;
  /* F70: slower (1.5s) + ease-in-out so the ring glides instead of racing.
     Previous 0.9s linear read as "splašený" during multi-second boots. */
  animation: wdc-spin 1.5s cubic-bezier(0.5, 0, 0.5, 1) infinite;
  margin: 4px 0;
  transition: border-top-color 0.4s ease;
}

.splash-spinner--warn {
  border-top-color: var(--wdc-warning, #f5a623);
}

.splash-hint-label { color: var(--wdc-text-3); }
.splash-hint-sep { margin: 0 6px; color: var(--wdc-text-3); opacity: 0.5; }
.splash-hint-elapsed { color: var(--wdc-text-2); font-variant-numeric: tabular-nums; }

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

/* ═══ Mobile sidebar drawer ═══════════════════════════════════════════════ */
.hamburger-btn {
  display: none;
  position: fixed;
  top: 12px;
  left: 12px;
  z-index: 200;
  flex-direction: column;
  gap: 5px;
  padding: 8px;
  background: var(--wdc-surface-2);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius-sm);
  cursor: pointer;
}
.hamburger-btn span {
  display: block;
  width: 20px;
  height: 2px;
  background: var(--wdc-text);
  border-radius: 2px;
}

.sidebar-drawer :deep(.el-drawer__body) {
  padding: 0;
  overflow: hidden;
}
.sidebar-drawer :deep(.el-drawer) {
  background: var(--wdc-surface);
}

@media (max-width: 959px) {
  .sidebar-desktop {
    display: none;
  }
  .hamburger-btn {
    display: flex;
  }
  .content-area {
    padding-left: 0;
  }
}
</style>
