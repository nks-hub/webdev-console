import { defineStore } from 'pinia'
import { ref } from 'vue'
import { ElMessage } from 'element-plus'
import { startService, stopService, restartService } from '../api/daemon'
import { i18n } from '../i18n'
import { cleanError } from '../utils/services'
import { useDaemonStore } from './daemon'

export type ServiceOp = 'start' | 'stop' | 'restart'

export const useServicesStore = defineStore('services', () => {
  const busy = ref<Set<string>>(new Set())
  // Last failed start/stop/restart per service, cleared by the next success,
  // so the error stays readable after its toast is gone.
  const lastError = ref<Record<string, string>>({})

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

  /** Runs the action and reports the outcome in Czech/English toasts; false when it failed. */
  async function act(id: string, op: ServiceOp, name: string): Promise<boolean> {
    const { t } = i18n.global
    try {
      await (op === 'start' ? start : op === 'stop' ? stop : restart)(id)
      if (lastError.value[id]) {
        const next = { ...lastError.value }
        delete next[id]
        lastError.value = next
      }
      ElMessage.success(t(`servicesPage.toast.${op}`, { name }))
      return true
    } catch (e) {
      const err = cleanError(e)
      lastError.value = { ...lastError.value, [id]: err }
      ElMessage.error(t(`servicesPage.toast.${op}Failed`, { name }))
      return false
    }
  }

  return { busy, lastError, isBusy, start, stop, restart, act }
})
