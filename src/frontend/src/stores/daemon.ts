import { defineStore } from 'pinia'
import { ref, computed } from 'vue'
import { fetchStatus, subscribeEvents } from '../api/daemon'
import type { StatusResponse, ServiceInfo } from '../api/types'

export const useDaemonStore = defineStore('daemon', () => {
  const status = ref<StatusResponse | null>(null)
  const connected = ref(false)
  let pollTimer: ReturnType<typeof setInterval> | null = null
  let sseCleanup: (() => void) | null = null

  const serviceMap = computed<Map<string, ServiceInfo>>(() => {
    const m = new Map<string, ServiceInfo>()
    status.value?.services.forEach(s => m.set(s.id, s))
    return m
  })

  const allRunning = computed(() =>
    status.value?.services.every(s => s.status === 'running') ?? false
  )

  async function poll() {
    try {
      status.value = await fetchStatus()
      connected.value = true
    } catch {
      connected.value = false
      status.value = null
    }
  }

  function startPolling() {
    void poll()
    pollTimer = setInterval(poll, 5000)

    sseCleanup = subscribeEvents(
      (service) => {
        if (!status.value) return
        const idx = status.value.services.findIndex(s => s.id === service.id)
        if (idx >= 0) status.value.services[idx] = service
        else status.value.services.push(service)
      },
      () => { /* progress handled by operation stores */ },
    )
  }

  function stopPolling() {
    if (pollTimer) { clearInterval(pollTimer); pollTimer = null }
    sseCleanup?.()
  }

  return { status, connected, serviceMap, allRunning, startPolling, stopPolling, poll }
})
