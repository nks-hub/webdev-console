import { defineStore } from 'pinia'
import { ref } from 'vue'
import { startService, stopService, restartService } from '../api/daemon'
import { useDaemonStore } from './daemon'

export const useServicesStore = defineStore('services', () => {
  const busy = ref<Set<string>>(new Set())

  function isBusy(id: string) {
    return busy.value.has(id)
  }

  // Wrap an action with the busy-id tracking + daemon re-poll that all
  // three lifecycle calls need. Previously start/stop/restart were three
  // copies of the same try/finally.
  async function runAction(id: string, action: (id: string) => Promise<unknown>) {
    busy.value.add(id)
    try {
      await action(id)
    } finally {
      busy.value.delete(id)
      await useDaemonStore().poll()
    }
  }

  const start = (id: string) => runAction(id, startService)
  const stop = (id: string) => runAction(id, stopService)
  const restart = (id: string) => runAction(id, restartService)

  return { busy, isBusy, start, stop, restart }
})
