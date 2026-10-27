import { h } from 'vue'
import { ElMessage, ElNotification } from 'element-plus'
import { i18n } from '../i18n'
import { useServicesStore } from '../stores/services'
import type { ServiceInfo } from '../api/types'
import { errorMessage } from './errors'
import { shortServiceName } from './serviceState'

export type ServiceOp = 'start' | 'stop' | 'restart'

const { t } = i18n.global

/** The daemon's "HTTP 500 Internal Server Error: detail" reduced to the detail. */
function failureReason(err: unknown): string {
  return errorMessage(err).replace(/^HTTP \d+ [^:]*: /, '')
}

/** Runs one lifecycle action and always tells the user how it went. */
export async function runServiceAction(svc: ServiceInfo, op: ServiceOp): Promise<boolean> {
  const store = useServicesStore()
  const name = shortServiceName(svc)
  try {
    await store[op](svc.id)
    ElMessage.success(t(op === 'start' ? 'toast.serviceStarted' : op === 'stop' ? 'toast.serviceStopped' : 'toast.serviceRestarted', { name }))
    return true
  } catch (err) {
    ElNotification({
      title: t('overview.actionFailed', { name }),
      message: failureReason(err),
      type: 'error',
      duration: 8000,
    })
    return false
  }
}

/**
 * Runs start or stop on several services at once and reports the outcome in
 * one place: one error notification listing every service that failed and
 * why, one toast for the ones that worked.
 */
export async function runServiceBatch(op: 'start' | 'stop', targets: ServiceInfo[]): Promise<void> {
  if (!targets.length) return
  const store = useServicesStore()
  const settled = await Promise.allSettled(targets.map(s => store[op](s.id)))
  const failed = settled.flatMap((r, i) =>
    r.status === 'rejected' ? [{ name: shortServiceName(targets[i]), reason: failureReason(r.reason) }] : [])
  const done = targets.length - failed.length

  if (failed.length) {
    ElNotification({
      title: t(op === 'start' ? 'overview.batch.startFailed' : 'overview.batch.stopFailed', { n: failed.length }, failed.length),
      message: h('div', failed.map(f => h('div', `${f.name}: ${f.reason}`))),
      type: 'error',
      duration: 0,
    })
  }
  if (done > 0) {
    ElMessage.success(t(op === 'start' ? 'overview.batch.started' : 'overview.batch.stopped', { n: done }, done))
  }
}
