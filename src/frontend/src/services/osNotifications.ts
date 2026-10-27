/**
 * #147 — OS notifications service. Single facade that any feature can
 * import and call without knowing whether Electron's preload bridge is
 * available (renderer in browser dev mode falls back to `console.info`
 * so dev still works without the desktop shell).
 *
 * Channels, preference shape and route rules live in
 * osNotificationShared.ts (shared with the main process). Every gate —
 * master switch, per-channel toggle, "only in background", 60 s throttle,
 * once-per-key — is applied by main, which also owns the preferences
 * (userData/notification-prefs.json) so its own daemon/updater toasts
 * honour the same settings. This module only mirrors the preferences for
 * the Settings UI and imports the old localStorage toggles once.
 */
import { ref, readonly } from 'vue'
import {
  NOTIFICATION_CHANNELS,
  defaultNotificationPrefs,
  mergeNotificationPrefs,
  type NotificationChannel,
  type NotificationPrefs,
  type OsNotifyPayload,
  type OsNotifyResult,
} from './osNotificationShared'

export { NOTIFICATION_CHANNELS, isSafeAppRoute } from './osNotificationShared'
export type { NotificationChannel, NotificationPrefs, NotificationUrgency, OsNotifyPayload, OsNotifyResult } from './osNotificationShared'

// `electronAPI` shape (incl. osNotify / notifyPrefs*) lives in env.d.ts —
// single source of truth so vue-tsc doesn't see conflicting declarations.

/** Pre-2026 renderer-only toggles (`wdc.osnotify.<channel>` = 'true' | 'false'). */
const LEGACY_PREF_PREFIX = 'wdc.osnotify.'
const LEGACY_CHANNELS = ['deploy', 'mcp', 'system'] as const
/** Browser dev mode has no main process; keep the prefs in localStorage there. */
const BROWSER_PREFS_KEY = 'wdc.osnotify.prefs'

const prefsState = ref<NotificationPrefs>(defaultNotificationPrefs())
const loaded = ref(false)
let loadPromise: Promise<NotificationPrefs> | null = null

/** Reactive, read-only view of the current preferences (for the Settings UI). */
export const notificationPrefs = readonly(prefsState)
export const notificationPrefsLoaded = readonly(loaded)

/** True when the desktop shell's notification bridge is available. */
export function hasNativeNotifications(): boolean {
  return typeof window !== 'undefined' && typeof window.electronAPI?.osNotify === 'function'
}

function readLegacyToggles(): Partial<Record<NotificationChannel, boolean>> | null {
  const found: Partial<Record<NotificationChannel, boolean>> = {}
  let any = false
  try {
    for (const ch of LEGACY_CHANNELS) {
      const raw = localStorage.getItem(`${LEGACY_PREF_PREFIX}${ch}`)
      if (raw === null) continue
      found[ch] = raw === 'true'
      any = true
    }
  } catch { /* storage blocked */ }
  return any ? found : null
}

function clearLegacyToggles(): void {
  try {
    for (const ch of LEGACY_CHANNELS) localStorage.removeItem(`${LEGACY_PREF_PREFIX}${ch}`)
  } catch { /* storage blocked */ }
}

function readBrowserPrefs(): NotificationPrefs {
  try {
    const raw = localStorage.getItem(BROWSER_PREFS_KEY)
    return mergeNotificationPrefs(defaultNotificationPrefs(), raw ? JSON.parse(raw) : null)
  } catch {
    return defaultNotificationPrefs()
  }
}

function writeBrowserPrefs(prefs: NotificationPrefs): void {
  try { localStorage.setItem(BROWSER_PREFS_KEY, JSON.stringify(prefs)) } catch { /* quota */ }
}

async function persist(patch: Partial<NotificationPrefs>): Promise<NotificationPrefs> {
  const api = window.electronAPI
  if (api?.notifyPrefsSet) return api.notifyPrefsSet(patch)
  const next = mergeNotificationPrefs(prefsState.value, patch)
  writeBrowserPrefs(next)
  return next
}

async function doLoad(): Promise<NotificationPrefs> {
  const api = window.electronAPI
  let prefs = api?.notifyPrefsGet ? await api.notifyPrefsGet() : readBrowserPrefs()
  // One-time import of the old per-channel localStorage toggles. Only the
  // three channels that existed back then; new channels keep their default.
  if (!prefs.legacyMigrated) {
    const legacy = readLegacyToggles()
    const patch: Partial<NotificationPrefs> = { legacyMigrated: true }
    if (legacy) patch.channels = { ...prefs.channels, ...legacy }
    prefs = await persist(patch)
    clearLegacyToggles()
  }
  prefsState.value = mergeNotificationPrefs(defaultNotificationPrefs(), prefs)
  loaded.value = true
  return prefsState.value
}

/** Loads the preferences from main (once per session; pass force to re-read). */
export function loadNotificationPrefs(force = false): Promise<NotificationPrefs> {
  if (!loadPromise || force) {
    loadPromise = doLoad().catch((e) => {
      console.warn('[osNotify] could not load preferences', e)
      loadPromise = null
      return prefsState.value
    })
  }
  return loadPromise
}

/** Saves a partial preference change; the reactive view updates on success. */
export async function updateNotificationPrefs(patch: Partial<NotificationPrefs>): Promise<NotificationPrefs> {
  const next = await persist(patch)
  prefsState.value = mergeNotificationPrefs(defaultNotificationPrefs(), next)
  return prefsState.value
}

export function isChannelEnabled(channel: NotificationChannel): boolean {
  return prefsState.value.enabled && prefsState.value.channels[channel] !== false
}

export function setChannelEnabled(channel: NotificationChannel, enabled: boolean): Promise<NotificationPrefs> {
  return updateNotificationPrefs({ channels: { ...prefsState.value.channels, [channel]: enabled } })
}

/** Tells main which UI language to use for the toasts it fires itself. */
export function syncNotificationLocale(locale: string): void {
  if (locale !== 'en' && locale !== 'cs') return
  if (loaded.value && prefsState.value.locale === locale) return
  void updateNotificationPrefs({ locale }).catch(() => { /* main not ready — next change retries */ })
}

async function send(payload: OsNotifyPayload): Promise<OsNotifyResult> {
  const api = window.electronAPI
  if (!api?.osNotify) {
    // Dev fallback so devs see *something* when running in plain Vite
    console.info('[osNotify dev fallback]', payload)
    return 'unsupported'
  }
  try {
    return await api.osNotify(payload)
  } catch (e) {
    console.warn('[osNotify] failed', e)
    return 'failed'
  }
}

/**
 * Fire an OS notification. Returns true when the toast was shown; false
 * when a preference gate, the throttle or the platform suppressed it.
 */
export async function osNotify(payload: OsNotifyPayload): Promise<boolean> {
  return (await send(payload)) === 'shown'
}

/** Settings → "Send test notification": bypasses the preference gates. */
export function sendTestNotification(title: string, body: string): Promise<OsNotifyResult> {
  return send({ title, body, channel: 'system', urgency: 'normal', route: '/settings?tab=notifications', test: true })
}

/** Channels in display order, for the Settings UI. */
export const notificationChannels: readonly NotificationChannel[] = NOTIFICATION_CHANNELS
