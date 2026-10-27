/**
 * OS notification contract shared by the Electron main process
 * (electron/notifications.ts) and the renderer facade
 * (services/osNotifications.ts). Pure TypeScript — no DOM, no Electron,
 * no Vue — so both bundles can import it and the channel list, the
 * preference shape and the route rules can never drift apart.
 */

/**
 * Notification channels. Each one has its own toggle in Settings →
 * Notifications and its own `osNotifications.channels.<id>` i18n entry.
 *   - services : a service crashed (HealthMonitor `service:state`)
 *   - sites    : a site apply finished with failed steps (`site:applied`)
 *   - deploy   : a deploy finished, success or failure (`deploy:complete`)
 *   - mcp      : an MCP operation waits for human approval
 *   - backup   : a scheduled or manual backup finished or failed
 *   - updates  : a new app version is available (once per version)
 *   - daemon   : the daemon went down or the supervisor restarted it
 *   - system   : everything else (test notification, misc. long tasks)
 */
export const NOTIFICATION_CHANNELS = [
  'services',
  'sites',
  'deploy',
  'mcp',
  'backup',
  'updates',
  'daemon',
  'system',
] as const

export type NotificationChannel = (typeof NOTIFICATION_CHANNELS)[number]
export type NotificationUrgency = 'low' | 'normal' | 'critical'

export function isNotificationChannel(value: unknown): value is NotificationChannel {
  return typeof value === 'string' && (NOTIFICATION_CHANNELS as readonly string[]).includes(value)
}

/** Operator preferences, persisted by main in userData/notification-prefs.json. */
export interface NotificationPrefs {
  /** Master switch — off silences every channel. */
  enabled: boolean
  /** Skip non-critical toasts while the main window is focused. */
  backgroundOnly: boolean
  channels: Record<NotificationChannel, boolean>
  /** Set once the old renderer-only `wdc.osnotify.*` localStorage values were imported. */
  legacyMigrated: boolean
  /** UI locale, mirrored from the renderer so main-fired toasts match the app language. */
  locale: 'en' | 'cs'
}

export function defaultNotificationPrefs(): NotificationPrefs {
  const channels = {} as Record<NotificationChannel, boolean>
  for (const ch of NOTIFICATION_CHANNELS) channels[ch] = true
  return { enabled: true, backgroundOnly: true, channels, legacyMigrated: false, locale: 'en' }
}

/**
 * Merges an untrusted patch (IPC payload or JSON file content) onto `base`,
 * keeping only known keys with the right type.
 */
export function mergeNotificationPrefs(base: NotificationPrefs, patch: unknown): NotificationPrefs {
  const next: NotificationPrefs = { ...base, channels: { ...base.channels } }
  if (!patch || typeof patch !== 'object') return next
  const p = patch as Record<string, unknown>
  if (typeof p.enabled === 'boolean') next.enabled = p.enabled
  if (typeof p.backgroundOnly === 'boolean') next.backgroundOnly = p.backgroundOnly
  if (typeof p.legacyMigrated === 'boolean') next.legacyMigrated = p.legacyMigrated
  if (p.locale === 'en' || p.locale === 'cs') next.locale = p.locale
  if (p.channels && typeof p.channels === 'object') {
    for (const [key, value] of Object.entries(p.channels as Record<string, unknown>)) {
      if (isNotificationChannel(key) && typeof value === 'boolean') next.channels[key] = value
    }
  }
  return next
}

/** What the renderer sends over the `os-notify` IPC channel. */
export interface OsNotifyPayload {
  title: string
  body?: string
  urgency?: NotificationUrgency
  silent?: boolean
  channel: NotificationChannel
  /** App hash route opened when the notification is clicked, e.g. `/services`. */
  route?: string
  /** Throttle/dedupe key — the same key fires at most once per 60 s. Defaults to channel + title. */
  key?: string
  /** Fire at most once ever for this key (persisted), e.g. one toast per update version. */
  once?: boolean
  /** Test notification from Settings: bypasses every preference gate and the throttle. */
  test?: boolean
}

export type OsNotifyResult =
  | 'shown'
  | 'disabled'
  | 'focused'
  | 'throttled'
  | 'duplicate'
  | 'unsupported'
  | 'invalid'
  | 'failed'

/**
 * A click route must be a relative in-app hash route: starts with a single
 * `/`, never `//` (protocol-relative URL), no backslashes, no control
 * characters and no `scheme:` anywhere (javascript:, file:, https:, …).
 * Main validates before navigating and the renderer re-checks before
 * `router.push`, so a compromised payload can't leave the app.
 */
export function isSafeAppRoute(route: unknown): route is string {
  if (typeof route !== 'string') return false
  if (route.length === 0 || route.length > 512) return false
  if (!route.startsWith('/')) return false
  if (route.includes('//')) return false
  if (route.includes('\\')) return false
  // eslint-disable-next-line no-control-regex
  if (/[\u0000-\u001f\u007f]/.test(route)) return false
  if (/[a-z][a-z0-9+.-]*:/i.test(route)) return false
  return true
}
