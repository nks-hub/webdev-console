import { defineStore } from 'pinia'
import { ref, computed } from 'vue'
import { fetchStatus, fetchServices, subscribeEvents } from '../api/daemon'
import type { StatusResponse, ServiceInfo } from '../api/types'

export const useDaemonStore = defineStore('daemon', () => {
  const status = ref<StatusResponse | null>(null)
  const services = ref<ServiceInfo[]>([])
  const connected = ref(false)
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

  async function poll() {
    try {
      status.value = await fetchStatus()
      services.value = await fetchServices()
      connected.value = true
      retryCount = 0

      // Track aggregate metrics
      const totalCpu = services.value.reduce((sum, s: any) => sum + (s.cpuPercent ?? 0), 0)
      const totalRam = services.value.reduce((sum, s: any) => sum + (s.memoryBytes ?? 0), 0) / 1024 / 1024
      cpuHistory.value.push(totalCpu)
      ramHistory.value.push(totalRam)
      if (cpuHistory.value.length > MAX_HISTORY) cpuHistory.value.shift()
      if (ramHistory.value.length > MAX_HISTORY) ramHistory.value.shift()
    } catch {
      // On first few failures, retry faster (daemon might be starting up)
      if (retryCount < 10) retryCount++
      connected.value = false
      status.value = null
      services.value = []
    }
  }

  function startPolling() {
    void poll()
    pollTimer = setInterval(poll, 5000)

    sseCleanup = subscribeEvents(
      (service) => {
        const idx = services.value.findIndex(s => s.id === service.id)
        if (idx >= 0) services.value[idx] = service
        else services.value.push(service)
      },
      () => {},
    )
  }

  function stopPolling() {
    if (pollTimer) { clearInterval(pollTimer); pollTimer = null }
    sseCleanup?.()
  }

  return { status, services, connected, runningServices, allRunning, cpuHistory, ramHistory, startPolling, stopPolling, poll }
})
