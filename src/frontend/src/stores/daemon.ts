import { defineStore } from 'pinia'
import { ref, computed } from 'vue'
import { fetchStatus, fetchServices, subscribeEvents } from '../api/daemon'
import type { ValidationUpdate } from '../api/daemon'
import type { StatusResponse, ServiceInfo } from '../api/types'

export const useDaemonStore = defineStore('daemon', () => {
  const status = ref<StatusResponse | null>(null)
  const services = ref<ServiceInfo[]>([])
  const connected = ref(false)
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
  let fastRetryTimer: ReturnType<typeof setTimeout> | null = null

  async function poll() {
    try {
      status.value = await fetchStatus()
      services.value = await fetchServices()
      connected.value = true
      retryCount = 0
      // Clear any pending fast retry — we're back online
      if (fastRetryTimer) { clearTimeout(fastRetryTimer); fastRetryTimer = null }

      // Track aggregate metrics
      const totalCpu = services.value.reduce((sum, s: any) => sum + (s.cpuPercent ?? 0), 0)
      const totalRam = services.value.reduce((sum, s: any) => sum + (s.memoryBytes ?? 0), 0) / 1024 / 1024
      cpuHistory.value.push(totalCpu)
      ramHistory.value.push(totalRam)
      if (cpuHistory.value.length > MAX_HISTORY) cpuHistory.value.shift()
      if (ramHistory.value.length > MAX_HISTORY) ramHistory.value.shift()
    } catch {
      connected.value = false
      status.value = null
      services.value = []
      // Fast-retry cascade after a failure so the UI recovers quickly from a
      // daemon restart (when the token changed). Previous version only retried
      // on the 5-second interval tick, leaving the "Offline" pill visible for
      // up to 5s even though the new token was already in the port file.
      if (retryCount < 5 && !fastRetryTimer) {
        retryCount++
        const delay = Math.min(300 * retryCount, 1500)
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

  return { status, services, connected, validation, runningServices, allRunning, cpuHistory, ramHistory, startPolling, stopPolling, poll }
})
