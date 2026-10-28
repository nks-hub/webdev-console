<template>
  <el-config-provider :locale="elementLocale">
  <div class="app-root">
    <a class="skip-link" href="#main" @click.prevent="focusMain">{{ t('app.skipToContent') }}</a>
    <div class="app-body">
      <!-- Sidebar runs the full height on the left; at <960px it moves
           into a drawer opened from the header's menu button. -->
      <AppSidebar class="sidebar-desktop" @navigate="drawerOpen = false" />

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

      <div class="app-main">
      <AppHeader @open-menu="drawerOpen = true" />
      <main id="main" class="content-area" tabindex="-1">
        <!-- Simple mode leaves these pages out of the menu; opened by link, shortcut or palette they say so. -->
        <div v-if="uiMode.isSimple && route.meta.advancedNav" class="mode-note" role="note">
          <span>{{ t('app.advancedPageNote') }}</span>
          <el-button size="small" @click="uiMode.setUiMode('advanced')">{{ t('app.switchToAdvanced') }}</el-button>
        </div>
        <!-- keep-alive caches mounted page components so clicking a nav
             item doesn't re-mount the whole tree AND re-run onMounted's
             fetch cascade every time. This is the biggest win for the
             "clicking feels slow" complaint in dev mode, where Vite's
             module graph warm-up overlaps with SSE reconnect and store
             re-hydration. Excluding SiteEdit because it's keyed by
             :domain and needs fresh state per domain. `max` evicts the
             least recently used page, so its timers and subscriptions
             end instead of piling up across a long session. The boundary
             replaces a page that fails to render with a recoverable
             message instead of an empty area. -->
        <router-view v-slot="{ Component }">
          <PageErrorBoundary>
            <keep-alive :exclude="['SiteEdit']" :max="8">
              <component :is="Component" />
            </keep-alive>
          </PageErrorBoundary>
        </router-view>
      </main>
      </div>
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
          <!-- Terminal boot-failure state (F-stability). Replaces the
               infinite spinner once the first connection misses the boot
               deadline, so the user always gets an actionable error instead
               of polling forever. -->
          <template v-if="daemonStore.bootFailed">
            <div class="splash-error-icon" aria-hidden="true">⚠</div>
            <div class="splash-status splash-status--error">{{ t('splash.failedTitle') }}</div>
            <div class="splash-hint">{{ splashFailHint }}</div>
            <div class="splash-actions">
              <button class="splash-btn splash-btn--primary" @click="daemonStore.retryBoot()">{{ t('splash.retry') }}</button>
              <button class="splash-btn" @click="reloadApp">{{ t('splash.restartApp') }}</button>
            </div>
            <div class="splash-loghint">{{ t('splash.logHint', { path: '~/.wdc/logs/daemon' }) }}</div>
          </template>
          <template v-else>
            <div class="splash-spinner" :class="{ 'splash-spinner--warn': splashWarn }" aria-hidden="true"></div>
            <div class="splash-status">{{ splashStatus }}</div>
            <div class="splash-hint">
              <span class="splash-hint-label">{{ splashHint }}</span>
              <span class="splash-hint-sep">·</span>
              <span class="splash-hint-elapsed">{{ splashElapsed }}</span>
            </div>
          </template>
        </div>
      </div>
    </Transition>
  </div>
  </el-config-provider>
</template>

<script setup lang="ts">
import { nextTick, onMounted, onUnmounted, ref, computed, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import AppHeader from './components/layout/AppHeader.vue'
import AppSidebar from './components/layout/AppSidebar.vue'
import AppStatusBar from './components/layout/AppStatusBar.vue'
import CommandPalette from './components/shared/CommandPalette.vue'
import PageErrorBoundary from './components/shared/PageErrorBoundary.vue'
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
import { useUiModeStore } from './stores/uiMode'
import { fetchSettings, subscribeEventsMap } from './api/daemon'
import { OPEN_COMMAND_PALETTE_EVENT } from './utils/commandPalette'
import { loadLastBackup } from './utils/lastBackup'
import { loadServicePorts } from './utils/servicePorts'
import { updateDocumentTitle } from './router'
import type { DeployEventDto } from './api/deploy'
import type { SiteApplyResultDto } from './api/daemon'
import { useSiteApplyStore } from './stores/siteApply'
import { i18n } from './i18n'
import { elementLocaleFor } from './utils/elementLocale'
import { isSafeAppRoute, loadNotificationPrefs, syncNotificationLocale } from './services/osNotifications'
import {
  notifyBackupCompleted, notifyBackupFailed, notifyDeployComplete, notifyMcpConfirm,
  notifyServiceState, notifySiteApplied, notifyUpdateAvailable,
  type BackupEvent, type ServiceStateEvent,
} from './services/osNotificationEvents'
import { showMcpConfirmToast, closeMcpConfirmToast } from './utils/mcpConfirmToast'
import { mcpKindLabel } from './utils/mcpKindLabel'

const { t, locale } = useI18n()
const elementLocale = computed(() => elementLocaleFor(locale.value))
const router = useRouter()
const route = useRoute()
const daemonStore = useDaemonStore()
const updatesStore = useUpdatesStore()
const drawerOpen = ref(false)

// Close the mobile drawer on route change. All pages share one scroll container,
// so without the reset a page opens at the previous page's offset, title hidden.
watch(() => route.path, () => {
  drawerOpen.value = false
  void nextTick(() => document.getElementById('main')?.scrollTo(0, 0))
})
const pluginsStore = usePluginsStore()
const sitesStore = useSitesStore()
const authStore = useAuthStore()
const deployStore = useDeployStore()
const mcpConfirmStore = useMcpConfirmStore()
const featureFlagsStore = useFeatureFlagsStore()
const siteApplyStore = useSiteApplyStore()
const uiMode = useUiModeStore()
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
    // The built-in local backend emits deploy:phase / deploy:complete instead
    // of deploy:event; deploy:complete is handled further down together with
    // the OS notification.
    // mcp.enabled / deploy.enabled toggled in Settings (or by another window).
    // HealthMonitor saw a service change state (crash, external stop/start).
    // A crash also raises an OS notification (services channel).
    'service:state': (data) => {
      void daemonStore.poll()
      const evt = data as ServiceStateEvent
      const svc = daemonStore.services.find(s => s.id === evt?.serviceId)
      notifyServiceState(evt, svc?.displayName || svc?.name)
    },
    // Per-step outcome of a site apply (SiteApplyPipeline on the site page).
    // Failed steps raise an OS notification (sites channel).
    'site:applied': (data) => {
      siteApplyStore.record(data as SiteApplyResultDto)
      notifySiteApplied(data as SiteApplyResultDto)
    },
    // Scheduled (BackupScheduler) or manual (POST /api/backup) backups.
    'backup:completed': (data) => {
      void loadLastBackup()
      notifyBackupCompleted(data as BackupEvent)
    },
    'backup:failed': (data) => notifyBackupFailed(data as BackupEvent),
    'mcp:settings-changed': () => { void featureFlagsStore.load() },
    'deploy:settings-changed': () => { void featureFlagsStore.load() },
    'deploy:phase': (data) =>
      deployStore.handlePhaseEvent(data as { deployId: string; phase: string; message?: string }),
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
      // even when the WDC window is in the background. Click → MCP intents.
      notifyMcpConfirm({ ...evt, kindLabel: mcpKindLabel(evt) })
    },
    // The intent was settled somewhere (banner, toast, Intents page, a
    // trust grant, the agent consuming it, expiry): close its toast and
    // drop its banner so nothing stale stays on screen.
    'mcp:intent-changed': (data) => {
      const evt = data as { intentId?: string; change?: string }
      if (!evt.intentId) return
      closeMcpConfirmToast(evt.intentId)
      mcpConfirmStore.handleIntentChanged(evt)
    },
    // #147 — surface deploy completion in OS notification center so a
    // long-running deploy doesn't require the operator to keep the
    // window foregrounded to know when it finished.
    'deploy:complete': (data) => {
      const evt = data as { deployId?: string; success?: boolean; error?: string; kind?: string }
      if (evt.deployId) {
        deployStore.handleCompleteEvent({
          deployId: evt.deployId, success: evt.success === true, error: evt.error, kind: evt.kind })
      }
      // Click → the site's deploy tab when the run's domain is known.
      const domain = evt.deployId ? deployStore.runs.get(evt.deployId)?.domain : null
      notifyDeployComplete(evt, domain)
    },
  })
}
function stopDeploySse(): void {
  if (unsubscribeDeploy) { unsubscribeDeploy(); unsubscribeDeploy = null }
}

// OS notification click → main focuses the window and sends the
// notification's app route over the `navigate` IPC channel. Main already
// validated it; re-check here before handing it to the router.
let unsubscribeNavigate: (() => void) | null = null
function startNotificationNavigation(): void {
  if (unsubscribeNavigate || !window.electronAPI?.onNavigate) return
  unsubscribeNavigate = window.electronAPI.onNavigate((target) => {
    if (!isSafeAppRoute(target)) return
    router.push(target).catch(() => { /* duplicate navigation */ })
  })
}

// A newer app version found by the background GitHub check → one OS
// notification per version (main remembers the key).
watch(
  () => (updatesStore.hasUpdate ? updatesStore.latestVersion : ''),
  (version) => { if (version) notifyUpdateAvailable(version) },
)

// Main fires daemon/updater toasts itself; keep its locale in step with the UI.
watch(() => i18n.global.locale.value, (locale) => {
  syncNotificationLocale(locale)
  updateDocumentTitle()
})

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
  if (!daemonStore.bootStartedAt) return t('splash.status.mounting')
  const fails = daemonStore.consecutiveFailures
  const kind = daemonStore.lastErrorKind
  // After the daemon has bound HTTP at least once but shutters down (daemon
  // restart) we flip back to connecting state. Cover both flows here.
  if (fails === 0 && daemonStore.pollAttempts === 1) return t('splash.status.connecting')
  if (kind === 'network') {
    // Daemon process not listening yet — port file may still be missing
    // or the socket is not bound. Normal during first few seconds of boot.
    if (splashElapsedSec.value < 8) return t('splash.status.daemonStarting')
    if (splashElapsedSec.value < 20) return t('splash.status.pluginsStarting')
    if (splashElapsedSec.value < 45) return t('splash.status.slow')
    return t('splash.status.unresponsive')
  }
  if (kind === 'auth') return t('splash.status.auth')
  if (kind === 'server') return t('splash.status.server')
  if (kind === 'unknown' && fails > 0) return t('splash.status.waiting')
  return t('splash.status.connecting')
})

const splashHint = computed(() => {
  const kind = daemonStore.lastErrorKind
  const n = daemonStore.pollAttempts
  if (!daemonStore.bootStartedAt) return t('splash.hint.mount')
  if (kind === 'network') return t('splash.hint.network', { n })
  if (kind === 'auth') return t('splash.hint.auth', { n })
  if (kind === 'server') return t('splash.hint.server', { n })
  if (n <= 1) return t('splash.hint.first')
  return t('splash.hint.attempt', { n })
})

// Over ~15s of network errors we visually escalate the spinner (amber tint)
// so the user knows something is genuinely taking longer than expected.
const splashWarn = computed(() =>
  daemonStore.lastErrorKind !== null && splashElapsedSec.value > 15
)

// Plain-language explanation shown on the terminal boot-failure card so the
// user knows what to try (vs an opaque spinner).
const splashFailHint = computed(() => {
  const kind = daemonStore.lastErrorKind
  const n = daemonStore.pollAttempts
  const s = splashElapsedSec.value
  const attempts = t('splash.attempts', { n }, n)
  if (kind === 'network') return t('splash.fail.network', { attempts, s })
  if (kind === 'auth') return t('splash.fail.auth', { attempts })
  if (kind === 'server') return t('splash.fail.server', { attempts, s })
  return t('splash.fail.unknown', { attempts, s })
})

function reloadApp() {
  // A renderer reload does nothing under app:// in the packaged build; relaunch instead.
  const restart = window.electronAPI?.restartApp
  if (restart) { void restart(); return }
  window.location.reload()
}

function focusMain() {
  document.getElementById('main')?.focus()
}

function openCommandPalette() {
  commandPalette.value?.open()
}

// Shortcut targets for Ctrl+1..7, in the same order the help page lists them.
const NUMBER_SHORTCUT_ROUTES = ['/dashboard', '/sites', '/databases', '/ssl', '/php', '/binaries', '/settings']

function isEditableTarget(target: EventTarget | null): boolean {
  return target instanceof HTMLElement
    && (target.isContentEditable || target.closest('input, textarea, select, [contenteditable="true"], .monaco-editor') !== null)
}

// The letter the user means, independent of layout and Caps Lock: the typed
// Latin letter when there is one, otherwise the physical key (Cyrillic etc.).
function shortcutLetter(e: KeyboardEvent): string {
  if (/^[a-z]$/i.test(e.key)) return e.key.toLowerCase()
  return e.code.startsWith('Key') ? e.code.slice(3).toLowerCase() : ''
}

function handleKeydown(e: KeyboardEvent) {
  if (e.key === 'F5') {
    e.preventDefault()
    daemonStore.retryBoot()
    return
  }
  // Ctrl+Alt is AltGr on Windows keyboards: it types characters, not shortcuts.
  if (!(e.ctrlKey || e.metaKey) || e.altKey) return
  const target = e.target

  const letter = shortcutLetter(e)
  // In Monaco, Ctrl+K starts a chord (Ctrl+K Ctrl+C); leave it to the editor.
  if (letter === 'k' && !(target instanceof HTMLElement && target.closest('.monaco-editor'))) {
    e.preventDefault()
    commandPalette.value?.open()
    return
  }
  if (isEditableTarget(target)) return

  if (letter === 'n') {
    e.preventDefault()
    void router.push({ path: '/sites', query: { create: '1' } })
    return
  }
  // The digit row is read by physical key: on a Czech layout it types "+ěščřžý"
  // without Shift, so e.key never says "1".
  const digit = /^Digit([1-7])$/.exec(e.code)
  if (digit && !e.shiftKey) {
    e.preventDefault()
    // Simple mode hides these pages from the menu, not from the shortcuts.
    void router.push(NUMBER_SHORTCUT_ROUTES[Number(digit[1]) - 1])
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
    // Everything that talks to the daemon starts here, after the first
    // successful poll: earlier calls only collected 401s while the token
    // was still being handed over.
    startDeploySse()
    // Reload MCP requests still waiting for approval — the SSE event for
    // one that arrived while WDC was closed or the daemon was restarting
    // was never seen, and without this it could not be approved at all.
    // Skipped while MCP is off: the endpoint answers 404 then.
    void featureFlagsStore.load().then(() => {
      if (featureFlagsStore.showMcpSurface) void mcpConfirmStore.loadPending()
    })
    void pluginsStore.loadAll()
    void sitesStore.load()
    void loadServicePorts()
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

  // OS notifications: import legacy toggles once, mirror the UI locale to
  // main, and route notification clicks into the router.
  void loadNotificationPrefs().then(() => syncNotificationLocale(i18n.global.locale.value))
  startNotificationNavigation()

  window.addEventListener('keydown', handleKeydown)
  window.addEventListener(OPEN_COMMAND_PALETTE_EVENT, openCommandPalette)
})

onUnmounted(() => {
  daemonStore.stopPolling()
  unsubscribeConnect?.()
  stopDeploySse()
  unsubscribeNavigate?.()
  unsubscribeNavigate = null
  if (splashTimer) { clearInterval(splashTimer); splashTimer = null }
  window.removeEventListener('keydown', handleKeydown)
  window.removeEventListener(OPEN_COMMAND_PALETTE_EVENT, openCommandPalette)
})
</script>

<style>
/* Shell: full-height sidebar | (header over content), status bar below. */
.app-root {
  display: flex;
  flex-direction: column;
  position: relative;
  height: 100vh;
  overflow: hidden;
  min-height: 580px;
  background: var(--wdc-bg);
}

.app-body {
  display: flex;
  flex: 1;
  min-height: 0;
  overflow: hidden;
}

.app-main {
  display: flex;
  flex-direction: column;
  flex: 1;
  min-width: 0;
}

.content-area {
  flex: 1;
  overflow-y: auto;
  overflow-x: hidden;
  scrollbar-gutter: stable; /* a page that starts to scroll must not shift sideways */
  background: var(--wdc-bg);
  padding: 0;
}
.content-area:focus { outline: none; }
.skip-link {
  position: absolute;
  left: -9999px;
  top: 0;
}
.skip-link:focus {
  left: var(--wdc-space-3);
  top: var(--wdc-space-2);
  z-index: 10001;
  padding: var(--wdc-space-2) var(--wdc-space-3);
  background: var(--wdc-surface);
  color: var(--wdc-text);
  border: 2px solid var(--wdc-accent);
  border-radius: var(--wdc-radius);
}
.mode-note {
  position: sticky;
  top: 0;
  z-index: 5;
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--wdc-space-3);
  padding: var(--wdc-space-2) var(--wdc-space-5);
  background: var(--wdc-accent-dim);
  border-bottom: 1px solid var(--wdc-border);
  color: var(--wdc-accent-ink);
  font-size: 0.87rem;
}

/* ═══ Splash overlay — shown until backend first connects ════════════════ */
.splash-overlay {
  position: fixed;
  inset: 0;
  z-index: 10000;
  display: flex;
  align-items: center;
  justify-content: center;
  background: var(--wdc-bg);
  backdrop-filter: blur(4px);
}

.splash-card {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: var(--wdc-space-4);
  padding: var(--wdc-space-7) var(--wdc-space-8);
  background: var(--wdc-surface);
  border: 1px solid var(--wdc-border-strong);
  border-radius: var(--wdc-radius-md);
  box-shadow: var(--wdc-shadow-pop);
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
  margin: var(--wdc-space-1) 0;
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

.splash-error-icon {
  font-size: 2.4rem;
  line-height: 1;
  color: var(--wdc-warning, #f5a623);
  margin: var(--wdc-space-0-5) 0;
}
.splash-status--error {
  color: var(--wdc-text);
  font-size: 0.98rem;
  font-weight: 700;
}
.splash-loghint {
  font-size: 0.72rem;
  font-family: 'JetBrains Mono', monospace;
  color: var(--wdc-text-3);
}
.splash-actions {
  display: flex;
  gap: var(--wdc-space-2-5);
  margin-top: var(--wdc-space-1);
}
.splash-btn {
  padding: var(--wdc-space-2) var(--wdc-space-4);
  border-radius: var(--wdc-radius-sm);
  border: 1px solid var(--wdc-border-strong);
  background: var(--wdc-surface-2, var(--wdc-surface));
  color: var(--wdc-text);
  font-size: 0.84rem;
  font-weight: 600;
  cursor: pointer;
  transition: border-color 0.2s ease, background 0.2s ease;
}
.splash-btn:hover { border-color: var(--wdc-accent); }
.splash-btn--primary {
  background: var(--wdc-accent);
  border-color: var(--wdc-accent);
  color: var(--wdc-on-accent);
}
.splash-btn--primary:hover { filter: brightness(1.08); }

.splash-fade-enter-active,
.splash-fade-leave-active {
  transition: opacity 0.35s ease;
}
.splash-fade-enter-from,
.splash-fade-leave-to {
  opacity: 0;
}

/* ═══ Mobile sidebar drawer ═══════════════════════════════════════════════ */
.sidebar-drawer .el-drawer__body {
  padding: 0 !important;
  overflow: hidden;
}
.sidebar-drawer.el-drawer {
  background: var(--wdc-chrome) !important;
}
.sidebar-drawer .sidebar {
  width: 100%;
  min-width: 0;
  border-right: 0;
}

@media (max-width: 959px) {
  .app-body > .sidebar-desktop {
    display: none !important;
  }
}
</style>
