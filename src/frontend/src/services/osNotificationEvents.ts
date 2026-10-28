/**
 * Maps daemon SSE events (and the update check) to OS notifications.
 * App.vue wires these into its single subscribeEventsMap call; each
 * handler builds localized text, picks a channel + urgency and the app
 * route a click should open. Gating (preferences, background-only,
 * throttle) happens in main, keyed by the `key` passed here.
 */
import { i18n } from '../i18n'
import { osNotify } from './osNotifications'
import { formatBytes } from '../utils/format'
import { backupContentLabel } from '../utils/backupContent'
import type { SiteApplyResultDto } from '../api/daemon'

const t = (key: string, params: Record<string, unknown> = {}): string =>
  i18n.global.t(`osNotifications.events.${key}`, params)

function shortId(id: string | undefined): string {
  return (id ?? '').slice(0, 8)
}

function formatSize(bytes: number): string {
  return formatBytes(Number.isFinite(bytes) ? Math.max(bytes, 0) : 0, i18n.global.locale.value)
}

/** `service:state` from the daemon HealthMonitor. Only crashes notify. */
export interface ServiceStateEvent {
  serviceId?: string
  state?: string
  previous?: string
  pid?: number | null
}

export function notifyServiceState(evt: ServiceStateEvent, displayName?: string): void {
  if (!evt?.serviceId || evt.state !== 'crashed') return
  void osNotify({
    channel: 'services',
    title: t('serviceCrashedTitle', { service: displayName || evt.serviceId }),
    body: t('serviceCrashedBody'),
    urgency: 'critical',
    route: '/services',
    key: `service:${evt.serviceId}:${evt.state}`,
  })
}

/** `site:applied` with failed steps → name the domain and the first failed step. */
export function notifySiteApplied(result: SiteApplyResultDto): void {
  if (!result?.domain || result.success !== false || !Array.isArray(result.steps)) return
  const failed = result.steps.filter(s => s.status === 'failed')
  if (failed.length === 0) return
  const first = failed[0]
  let body = first.detail
    ? t('siteApplyFailedBody', { step: first.name, detail: first.detail })
    : t('siteApplyFailedBodyNoDetail', { step: first.name })
  if (failed.length > 1) body += ` ${t('siteApplyFailedMore', { count: failed.length - 1 })}`
  void osNotify({
    channel: 'sites',
    title: t('siteApplyFailedTitle', { domain: result.domain }),
    body,
    urgency: 'normal',
    route: `/sites/${encodeURIComponent(result.domain)}/edit`,
    key: `site:${result.domain}:${failed.map(s => s.name).join(',')}`,
  })
}

/** `deploy:complete` (deploy, rollback, rollback-to, cancel). */
export interface DeployCompleteEvent {
  deployId?: string
  success?: boolean
  error?: string | null
  kind?: string
}

export function notifyDeployComplete(evt: DeployCompleteEvent, domain?: string | null): void {
  const id = shortId(evt.deployId)
  const site = domain && domain !== '?' ? domain : ''
  const route = site ? `/sites/${encodeURIComponent(site)}/edit?tab=deploy` : '/sites'
  const isRollback = evt.kind === 'rollback' || evt.kind === 'rollback-to'
  const error = evt.error || '—'
  let title: string
  let body: string
  let urgency: 'normal' | 'critical' = 'normal'
  if (isRollback) {
    title = evt.success ? t('rollbackSucceededTitle') : t('rollbackFailedTitle')
    body = evt.success ? t('rollbackSucceededBody') : t('rollbackFailedBody', { error })
    if (!evt.success) urgency = 'critical'
  } else if (evt.success) {
    title = t('deploySucceededTitle')
    body = site ? t('deploySucceededBodySite', { domain: site }) : t('deploySucceededBody', { id })
  } else if (evt.error === 'cancelled') {
    title = t('deployCancelledTitle')
    body = t('deployCancelledBody', { id })
  } else {
    title = t('deployFailedTitle')
    body = site ? t('deployFailedBodySite', { domain: site, error }) : t('deployFailedBody', { id, error })
    urgency = 'critical'
  }
  void osNotify({
    channel: 'deploy',
    title,
    body,
    urgency,
    route,
    key: `deploy:${evt.deployId ?? title}`,
  })
}

/** `mcp:confirm-request` — an AI client waits for human approval. */
export function notifyMcpConfirm(evt: { intentId: string; kind?: string; kindLabel?: string }): void {
  const kind = evt.kindLabel || evt.kind
  void osNotify({
    channel: 'mcp',
    title: t('mcpConfirmTitle'),
    body: kind ? t('mcpConfirmBodyKind', { kind }) : t('mcpConfirmBody'),
    urgency: 'critical',
    route: '/mcp/intents',
    key: `mcp:${evt.intentId}`,
  })
}

/** `backup:completed` / `backup:failed` from the daemon (scheduler or POST /api/backup). */
export interface BackupEvent {
  trigger?: 'scheduled' | 'manual' | string
  path?: string
  files?: number
  size?: number
  error?: string
  /** Asked-for content the daemon left out, e.g. Databases while MySQL is stopped. */
  skipped?: Array<{ content: string; detail: string }>
}

function backupTrigger(evt: BackupEvent): string {
  return evt.trigger === 'scheduled' ? t('backupTriggerScheduled') : t('backupTriggerManual')
}

export function notifyBackupCompleted(evt: BackupEvent): void {
  const missing = [...new Set((evt.skipped ?? []).map(s => backupContentLabel(s.content)))]
  void osNotify({
    channel: 'backup',
    title: t(missing.length ? 'backupIncompleteTitle' : 'backupCompletedTitle'),
    body: missing.length
      ? t('backupIncompleteBody', { trigger: backupTrigger(evt), missing: missing.join(', ') })
      : t('backupCompletedBody', {
        trigger: backupTrigger(evt),
        files: evt.files ?? 0,
        size: formatSize(evt.size ?? 0),
      }),
    urgency: missing.length ? 'normal' : 'low',
    route: '/backups',
    key: `backup:completed:${evt.path ?? ''}`,
  })
}

export function notifyBackupFailed(evt: BackupEvent): void {
  void osNotify({
    channel: 'backup',
    title: t('backupFailedTitle'),
    body: t('backupFailedBody', { trigger: backupTrigger(evt), error: evt.error || '—' }),
    urgency: 'critical',
    route: '/backups',
    key: `backup:failed:${evt.trigger ?? ''}`,
  })
}

/**
 * New app version found by the renderer's GitHub release check. `once`
 * makes main remember the key, so each version notifies a single time
 * (shared key with main's electron-updater `update-available`).
 */
export function notifyUpdateAvailable(version: string): void {
  if (!version) return
  void osNotify({
    channel: 'updates',
    title: t('updateAvailableTitle'),
    body: t('updateAvailableBody', { version }),
    urgency: 'low',
    route: '/settings?tab=update',
    key: `update:${version}`,
    once: true,
  })
}
