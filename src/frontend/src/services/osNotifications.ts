/**
 * #147 — OS notifications service. Single facade that any feature can
 * import and call without knowing whether Electron's preload bridge is
 * available (renderer in browser dev mode falls back to `console.info`
 * so dev still works without the desktop shell).
 *
 * Channels:
 *   - 'deploy'   : deploy:complete events (success / failure)
 *   - 'mcp'      : mcp:confirm-request events (Claude needs human approval)
 *   - 'system'   : long-running operations (snapshot done, restore done)
 *
 * Operator preference is honored at the call site — each subscriber
 * checks the per-channel toggle in localStorage before firing so the
 * facade itself doesn't need to know about settings.
 */
export type NotificationChannel = 'deploy' | 'mcp' | 'system'
export type NotificationUrgency = 'low' | 'normal' | 'critical'

export interface OsNotifyPayload {
  title: string
  body?: string
  urgency?: NotificationUrgency
  silent?: boolean
  channel: NotificationChannel
}

// `electronAPI` shape (incl. osNotify) lives in env.d.ts — single source
// of truth so vue-tsc doesn't see conflicting parallel declarations.

const PREF_PREFIX = 'wdc.osnotify.'

/**
 * Per-channel enable flag — defaults to TRUE (operator must
 * explicitly disable). Stored in localStorage so it survives
 * across sessions without round-tripping through the daemon.
 */
export function isChannelEnabled(channel: NotificationChannel): boolean {
  try {
    const raw = localStorage.getItem(`${PREF_PREFIX}${channel}`)
    if (raw === null) return true
    return raw === 'true'
  } catch { return true }
}

export function setChannelEnabled(channel: NotificationChannel, enabled: boolean): void {
  try { localStorage.setItem(`${PREF_PREFIX}${channel}`, String(enabled)) } catch { /* quota */ }
}

/**
 * Fire an OS notification. Returns the result of the underlying
 * Electron call (true on success). Silently no-ops when:
 *   - the per-channel flag is off
 *   - electronAPI.osNotify isn't available (browser dev mode)
 *   - the underlying call throws
 */
export async function osNotify(payload: OsNotifyPayload): Promise<boolean> {
  if (!isChannelEnabled(payload.channel)) return false
  const api = window.electronAPI
  if (!api?.osNotify) {
    // Dev fallback so devs see *something* when running in plain Vite
    console.info('[osNotify dev fallback]', payload)
    return false
  }
  try {
    return await api.osNotify(payload)
  } catch (e) {
    console.warn('[osNotify] failed', e)
    return false
  }
}
