import { defineStore } from 'pinia'
import { ref, computed } from 'vue'
import { fetchStatus, fetchServices, subscribeEvents } from '../api/daemon'
import type { StatusResponse, ServiceInfo, ValidationUpdate } from '../api/types'

type OnConnectListener = () => void | Promise<void>
const onConnectListeners = new Set<OnConnectListener>()

export const useDaemonStore = defineStore('daemon', () => {
  const status = ref<StatusResponse | null>(null)
  const services = ref<ServiceInfo[]>([])
  const connected = ref(false)
  // True only after the very first successful /api/status. Distinguishes
  // "boot — backend still starting" (show splash spinner) from "runtime
  // offline" (show inline offline badge). Never flips back to false.
  const hasEverConnected = ref(false)
  // Boot-phase telemetry consumed by the splash overlay so it can show
  // WHAT is being waited on instead of a generic "Waiting for backend…"
  // label. bootStartedAt is wall-clock ms of the first poll attempt,
  // pollAttempts counts total /api/status calls (successful or not),
  // consecutiveFailuresPublic mirrors the private counter for UI.
  const bootStartedAt = ref<number | null>(null)
  const pollAttempts = ref(0)
  const consecutiveFailuresPublic = ref(0)
  const lastErrorKind = ref<'network' | 'auth' | 'server' | 'unknown' | null>(null)
  // Per-service validation state broadcast by the daemon on /api/config/validate
  // so ValidationBadge can show Validating/Passed/Failed without the parent
  // component needing to imperatively call startValidation()/setResult().
  const validation = ref<Record<string, ValidationUpdate>>({})
  let pollTimer: ReturnType<typeof setInterval> | null = null
  let sseCleanup: (() => void) | null = null

  // Rolling metrics history (60 samples = 5 min at 5s interval)
  const MAX_HISTORY = 60
  const cpuHistory = ref<number[]>([])
  const ramHistory = ref<number[]>([])

  const runningServices = computed(() =>
    services.value.filter(s => s.state === 2 || s.status === 'running')
  )

  const allRunning = computed(() =>
    services.value.length > 0 && services.value.every(s => s.state === 2 || s.status === 'running')
  )

  let retryCount = 0
  let consecutiveFailures = 0
  let fastRetryTimer: ReturnType<typeof setTimeout> | null = null
  // How many consecutive poll failures before flipping the badge to Offline.
  // A single transient failure (network blip, EventSource reconnection,
  // Vite HMR pause) must NOT flicker the badge — the flicker reported by the
  // user came from the previous code setting connected=false on the first
  // failure, then the fast-retry cascade setting it back to true ~300ms
  // later, producing a visible Offline flash on every heartbeat blip.
  const OFFLINE_THRESHOLD = 3

  async function poll() {
    if (bootStartedAt.value === null) bootStartedAt.value = Date.now()
    pollAttempts.value++
    try {
      status.value = await fetchStatus()
      services.value = await fetchServices()
      const wasConnected = connected.value
      connected.value = true
      retryCount = 0
      consecutiveFailures = 0
      consecutiveFailuresPublic.value = 0
      lastErrorKind.value = null
      // Fire onConnect listeners: either on the very first successful poll
      // OR on any transition from offline→online (daemon restart scenario).
      if (!hasEverConnected.value || !wasConnected) {
        hasEverConnected.value = true
        for (const l of onConnectListeners) {
          try { void l() } catch { /* ignore listener failures */ }
        }
      }
      // Clear any pending fast retry — we're back online
      if (fastRetryTimer) { clearTimeout(fastRetryTimer); fastRetryTimer = null }

      // Track aggregate metrics
      const totalCpu = services.value.reduce((sum, s) => sum + (s.cpuPercent ?? 0), 0)
      const totalRam = services.value.reduce((sum, s) => sum + (s.memoryBytes ?? 0), 0) / 1024 / 1024
      cpuHistory.value.push(totalCpu)
      ramHistory.value.push(totalRam)
      if (cpuHistory.value.length > MAX_HISTORY) cpuHistory.value.shift()
      if (ramHistory.value.length > MAX_HISTORY) ramHistory.value.shift()
    } catch (err) {
      consecutiveFailures++
      consecutiveFailuresPublic.value = consecutiveFailures
      // Classify the failure so the splash overlay can show a specific
      // label (network refused vs auth vs server error) instead of a
      // generic spinner. Best-effort — errors from fetch() often only
      // expose a message string.
      const msg = String(err instanceof Error ? err.message : err ?? '').toLowerCase()
      if (msg.includes('failed to fetch') || msg.includes('econnrefused') || msg.includes('network')) {
        lastErrorKind.value = 'network'
      } else if (msg.includes('401') || msg.includes('403') || msg.includes('unauthor')) {
        lastErrorKind.value = 'auth'
      } else if (msg.includes('500') || msg.includes('502') || msg.includes('503')) {
        lastErrorKind.value = 'server'
      } else {
        lastErrorKind.value = 'unknown'
      }
      // Only flip to Offline after the retry cascade actually exhausts itself.
      // Until then the badge STAYS in its last state (almost always
      // connected=true) so brief failures don't flash the header.
      if (consecutiveFailures >= OFFLINE_THRESHOLD) {
        connected.value = false
        status.value = null
        services.value = []
      }
      // Fast-retry cascade after a failure so the UI recovers quickly from a
      // daemon restart (when the token changed). Previous version only retried
      // on the 5-second interval tick, leaving the "Offline" pill visible for
      // up to 5s even though the new token was already in the port file.
      if (retryCount < 5 && !fastRetryTimer) {
        retryCount++
        // Base delay scales linearly; ±20% jitter so multiple renderer
        // windows don't synchronise their fast-retry probes on the same
        // instant after a daemon restart (same rationale as subscribeEvents
        // jitter added in e4efb49).
        const base = Math.min(300 * retryCount, 1500)
        const delay = base * (0.8 + Math.random() * 0.4)
        fastRetryTimer = setTimeout(() => {
          fastRetryTimer = null
          void poll()
        }, delay)
      }
    }
  }

  function startPolling() {
    void poll()
    // The 5-second interval always resets the fast-retry counter BEFORE polling
    // so that successive daemon restarts (each of which may need a fresh retry
    // cascade) keep getting rescued. Without this, once retryCount hits the cap
    // the UI stayed Offline until a manual window reload.
    pollTimer = setInterval(() => {
      retryCount = 0
      if (fastRetryTimer) { clearTimeout(fastRetryTimer); fastRetryTimer = null }
      void poll()
    }, 5000)

    sseCleanup = subscribeEvents(
      (service) => {
        const idx = services.value.findIndex(s => s.id === service.id)
        if (idx >= 0) services.value[idx] = service
        else services.value.push(service)
      },
      () => {},
      undefined, // onMetrics
      undefined, // onLog
      (update) => {
        // Record latest validation phase per serviceId. Components bind to
        // `daemonStore.validation[serviceId]` for live updates.
        validation.value = { ...validation.value, [update.serviceId]: update }
      },
    )
  }

  function stopPolling() {
    if (pollTimer) { clearInterval(pollTimer); pollTimer = null }
    if (fastRetryTimer) { clearTimeout(fastRetryTimer); fastRetryTimer = null }
    sseCleanup?.()
  }

  /** Register a callback fired on first connect + every reconnect.
   *  Returns an unsubscribe function. Used by other stores (sites, plugins,
   *  services) to refetch whenever the daemon comes online, so Electron's
   *  startup window or a mid-session daemon restart doesn't leave the
   *  UI showing stale / empty data. */
  function onConnect(listener: OnConnectListener): () => void {
    onConnectListeners.add(listener)
    // If we are already connected at registration time, fire immediately so
    // callers that mount AFTER the first connection don't miss the event.
    if (hasEverConnected.value && connected.value) {
      try { void listener() } catch { /* ignore */ }
    }
    return () => { onConnectListeners.delete(listener) }
  }

  return {
    status, services, connected, hasEverConnected, validation,
    runningServices, allRunning, cpuHistory, ramHistory,
    bootStartedAt, pollAttempts,
    consecutiveFailures: consecutiveFailuresPublic, lastErrorKind,
    startPolling, stopPolling, poll, onConnect,
  }
})
