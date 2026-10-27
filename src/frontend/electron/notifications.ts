import { app, BrowserWindow, Notification, type NotificationConstructorOptions } from 'electron'
import { existsSync, readFileSync, writeFileSync, renameSync } from 'fs'
import { join } from 'path'
import log from 'electron-log/main'
// Named imports so rollup tree-shakes the rest of the locale files out of
// dist-electron/main.js — main only needs the notification texts.
import { osNotifications as enMessages } from '../src/locales/en.json'
import { osNotifications as csMessages } from '../src/locales/cs.json'
import {
  defaultNotificationPrefs,
  isNotificationChannel,
  isSafeAppRoute,
  mergeNotificationPrefs,
  type NotificationPrefs,
  type NotificationUrgency,
  type OsNotifyPayload,
  type OsNotifyResult,
} from '../src/services/osNotificationShared'

/**
 * Native OS notifications for the main process: platform identity,
 * persisted preferences, gating (master switch, per-channel toggles,
 * background-only, throttle), click → focus window + navigate.
 *
 * Platform notes:
 *  - Windows: toasts are attributed to the AppUserModelID. The NSIS
 *    installer creates the Start Menu shortcut with AUMID = electron-builder
 *    `appId`, so packaged builds must use exactly that id or toasts are
 *    dropped / shown as "electron.exe". Dev runs have no such shortcut and
 *    use process.execPath, the id Electron's docs recommend for development.
 *  - macOS: the sender name and icon come from the app bundle
 *    (CFBundleName / CFBundleIdentifier = productName / appId). Unsigned dev
 *    builds run inside Electron.app, so their notifications show up as
 *    "Electron" with the Electron icon — expected, not a bug. macOS asks
 *    the user for permission on the first notification; denial makes
 *    show() a silent no-op.
 *  - Linux: libnotify needs an explicit icon path, and `urgency` is only
 *    honoured there.
 *  - `timeoutType: 'never'` (sticky toast) exists on Windows and Linux only.
 */

/** Must equal `appId` in electron-builder.yml. */
export const APP_ID = 'com.nks-hub.webdev-console'
/** Must equal `productName` in electron-builder.yml. */
export const PRODUCT_NAME = 'NKS WebDev Console'

const THROTTLE_MS = 60_000
const MAX_ONCE_KEYS = 50
const MAX_LIVE_NOTIFICATIONS = 50

/**
 * Sets the app identity the OS notification centres attribute toasts to.
 * Call at module load, before `app.requestSingleInstanceLock()` and before
 * the first window or notification exists.
 */
export function configureNotificationIdentity(): void {
  // package.json has no productName, so app.name is "@nks-hub/webdev-console",
  // which libnotify shows as the sender on Linux. app.setName() would also
  // move the default userData dir (it derives from the name) and with it the
  // renderer's localStorage, so pin the current userData path first.
  if (app.getName() !== PRODUCT_NAME) {
    try {
      app.setPath('userData', app.getPath('userData'))
      app.setName(PRODUCT_NAME)
    } catch (err) {
      log.warn('[notify] could not set app name:', err)
    }
  }
  if (process.platform === 'win32') {
    app.setAppUserModelId(app.isPackaged ? APP_ID : process.execPath)
  }
}

// ── Preferences ───────────────────────────────────────────────────────
// Stored as JSON in userData next to window-state.json so main can read
// them without the renderer (daemon-down toasts fire while no renderer is
// loaded). The renderer reads/writes them through the notify-prefs IPC.

interface StoredState extends NotificationPrefs {
  /** Keys of `once` notifications already shown (e.g. `update:0.5.0`). */
  onceKeys: string[]
}

let state: StoredState | null = null

function prefsFile(): string {
  return join(app.getPath('userData'), 'notification-prefs.json')
}

function defaultLocale(): 'en' | 'cs' {
  try { return app.getLocale().toLowerCase().startsWith('cs') ? 'cs' : 'en' } catch { return 'en' }
}

function loadState(): StoredState {
  if (state) return state
  let raw: unknown = null
  try {
    if (existsSync(prefsFile())) raw = JSON.parse(readFileSync(prefsFile(), 'utf-8'))
  } catch (err) {
    log.warn('[notify] prefs file unreadable, using defaults:', err)
  }
  const base = { ...defaultNotificationPrefs(), locale: defaultLocale() }
  const prefs = mergeNotificationPrefs(base, raw)
  const onceKeys = Array.isArray((raw as { onceKeys?: unknown })?.onceKeys)
    ? ((raw as { onceKeys: unknown[] }).onceKeys.filter(k => typeof k === 'string') as string[]).slice(-MAX_ONCE_KEYS)
    : []
  state = { ...prefs, onceKeys }
  return state
}

function saveState(): void {
  if (!state) return
  try {
    const file = prefsFile()
    const tmp = `${file}.tmp`
    writeFileSync(tmp, JSON.stringify(state, null, 2))
    renameSync(tmp, file)
  } catch (err) {
    log.warn('[notify] could not save prefs:', err)
  }
}

export function getNotificationPrefs(): NotificationPrefs {
  const { onceKeys: _onceKeys, ...prefs } = loadState()
  return { ...prefs, channels: { ...prefs.channels } }
}

export function updateNotificationPrefs(patch: unknown): NotificationPrefs {
  const current = loadState()
  state = { ...mergeNotificationPrefs(current, patch), onceKeys: current.onceKeys }
  saveState()
  return getNotificationPrefs()
}

// ── Main-side texts ───────────────────────────────────────────────────

export type MainNotificationText = keyof typeof enMessages.events

/** Looks up `osNotifications.events.<key>` in the locale the renderer last reported. */
export function notificationText(key: MainNotificationText, params: Record<string, string | number> = {}): string {
  const table = loadState().locale === 'cs' ? csMessages.events : enMessages.events
  const template = (table as Record<string, string>)[key] ?? enMessages.events[key] ?? key
  return template.replace(/\{(\w+)\}/g, (match, name: string) =>
    name in params ? String(params[name]) : match)
}

// ── Showing notifications ─────────────────────────────────────────────

let getMainWindow: () => BrowserWindow | null = () => null

/** Main window accessor, used for the focus check and click navigation. */
export function setNotificationWindowProvider(provider: () => BrowserWindow | null): void {
  getMainWindow = provider
}

// Strong references to visible notifications. Without them the JS object
// can be garbage-collected while the toast is still on screen, and on
// Windows and macOS its click handler then never runs.
const liveNotifications = new Set<Notification>()
const lastShownAt = new Map<string, number>()

function notificationIconPath(): string | undefined {
  const candidates = [
    join(process.resourcesPath || '', 'build', 'icon.png'),
    join(__dirname, '..', 'build', 'icon.png'),
    join(app.getAppPath(), 'build', 'icon.png'),
  ]
  return candidates.find(p => { try { return existsSync(p) } catch { return false } })
}

function isMainWindowFocused(): boolean {
  const w = getMainWindow()
  return !!w && !w.isDestroyed() && w.isVisible() && !w.isMinimized() && w.isFocused()
}

/** Shows, restores and focuses the main window, then routes the renderer. */
export function focusMainWindowAndNavigate(route?: string): void {
  const w = getMainWindow() ?? BrowserWindow.getAllWindows()[0] ?? null
  if (!w || w.isDestroyed()) return
  if (w.isMinimized()) w.restore()
  w.show()
  w.focus()
  if (!isSafeAppRoute(route)) return
  const send = () => { if (!w.isDestroyed()) w.webContents.send('navigate', route) }
  if (w.webContents.isLoading()) w.webContents.once('did-finish-load', send)
  else send()
}

function isThrottled(key: string, now: number): boolean {
  const last = lastShownAt.get(key)
  if (last !== undefined && now - last < THROTTLE_MS) return true
  // Keep the map small: forget keys whose window has passed.
  if (lastShownAt.size > 200) {
    for (const [k, t] of lastShownAt) if (now - t >= THROTTLE_MS) lastShownAt.delete(k)
  }
  return false
}

/**
 * Single entry point for every OS notification — renderer IPC and main
 * (daemon supervisor, updater) alike — so all of them honour the same
 * preferences, throttle and click routing.
 */
export function showOsNotification(payload: OsNotifyPayload): OsNotifyResult {
  if (!payload || typeof payload.title !== 'string' || !isNotificationChannel(payload.channel)) {
    return 'invalid'
  }
  if (!Notification.isSupported()) {
    log.warn('[notify] not supported on this platform', { channel: payload.channel })
    return 'unsupported'
  }
  const urgency: NotificationUrgency =
    payload.urgency === 'low' || payload.urgency === 'critical' ? payload.urgency : 'normal'
  const critical = urgency === 'critical'
  const route = isSafeAppRoute(payload.route) ? payload.route : undefined
  if (payload.route !== undefined && !route) {
    log.warn('[notify] dropping unsafe click route', { channel: payload.channel })
  }
  const key = (typeof payload.key === 'string' && payload.key ? payload.key : `${payload.channel}:${payload.title}`).slice(0, 200)
  const now = Date.now()
  const prefs = loadState()

  if (!payload.test) {
    if (!prefs.enabled || !prefs.channels[payload.channel]) return 'disabled'
    if (prefs.backgroundOnly && !critical && isMainWindowFocused()) return 'focused'
    if (payload.once && prefs.onceKeys.includes(key)) return 'duplicate'
    if (isThrottled(key, now)) return 'throttled'
  }

  const options: NotificationConstructorOptions = {
    title: payload.title.slice(0, 200) || PRODUCT_NAME,
    body: (typeof payload.body === 'string' ? payload.body : '').slice(0, 1000),
    silent: typeof payload.silent === 'boolean' ? payload.silent : urgency === 'low',
  }
  if (process.platform === 'linux') {
    options.urgency = urgency
    const icon = notificationIconPath()
    if (icon) options.icon = icon
  } else if (process.platform === 'win32') {
    // Packaged toasts take the shortcut icon; dev runs have none, so pass ours.
    const icon = notificationIconPath()
    if (icon) options.icon = icon
  }
  if (critical && (process.platform === 'win32' || process.platform === 'linux')) {
    options.timeoutType = 'never'
  }

  try {
    const n = new Notification(options)
    const release = () => { liveNotifications.delete(n) }
    n.on('click', () => {
      release()
      focusMainWindowAndNavigate(route)
    })
    n.on('close', release)
    n.on('failed', (_evt, error) => {
      release()
      log.warn('[notify] failed to show', { channel: payload.channel, error })
    })
    liveNotifications.add(n)
    // Some notification servers never report close; cap the set so a
    // long session doesn't keep every toast alive forever.
    if (liveNotifications.size > MAX_LIVE_NOTIFICATIONS) {
      const oldest = liveNotifications.values().next().value
      if (oldest) liveNotifications.delete(oldest)
    }
    n.show()
    lastShownAt.set(key, now)
    if (payload.once && !payload.test) {
      prefs.onceKeys = [...prefs.onceKeys.filter(k => k !== key), key].slice(-MAX_ONCE_KEYS)
      saveState()
    }
    log.info('[notify] shown', { channel: payload.channel, urgency, route })
    return 'shown'
  } catch (err) {
    log.error('[notify] failed:', err)
    return 'failed'
  }
}
