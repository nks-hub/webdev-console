import { defineStore } from 'pinia'
import { ref } from 'vue'
import { startService, stopService, restartService } from '../api/daemon'
import { useDaemonStore } from './daemon'

export const useServicesStore = defineStore('services', () => {
  const busy = ref<Set<string>>(new Set())

  function isBusy(id: string) {
    return busy.value.has(id)
  }

  async function start(id: string) {
    busy.value.add(id)
    try {
      await startService(id)
    } finally {
      busy.value.delete(id)
      await useDaemonStore().poll()
    }
  }

  async function stop(id: string) {
    busy.value.add(id)
    try {
      await stopService(id)
    } finally {
      busy.value.delete(id)
      await useDaemonStore().poll()
    }
  }

  async function restart(id: string) {
    busy.value.add(id)
    try {
      await restartService(id)
    } finally {
      busy.value.delete(id)
      await useDaemonStore().poll()
    }
  }

  return { busy, isBusy, start, stop, restart }
})
