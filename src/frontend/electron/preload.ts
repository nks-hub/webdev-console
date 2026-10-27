import { contextBridge, ipcRenderer } from 'electron'

// The renderer runs with `sandbox: true`, so this preload has no fs/os/path
// access. Main reads and validates the daemon port file (it trusts only a
// file this user owns with no group/other access; see 'daemon-connection-info'
// in main.ts) and hands back { port, token }. getPort()/getToken() stay
// synchronous for callers, so this uses sendSync, throttled so a burst of
// fetches costs one round trip. A daemon restart (new port/token) is picked
// up on the next call after the throttle window.
const REFRESH_MS = 250

// Base64 token shape check — the daemon emits a 32-byte random token encoded
// as base64 (44 chars ending in =). Validate the shape before caching so a
// corrupted or hand-edited port file can't inject arbitrary strings into
// the renderer's Authorization header.
const TOKEN_SHAPE = /^[A-Za-z0-9+/=]{16,512}$/

let cachedPort = 0
let cachedToken = ''
let lastRefresh = 0

function refreshConnectionInfo(): void {
  const now = Date.now()
  if (cachedPort > 0 && now - lastRefresh < REFRESH_MS) return
  lastRefresh = now
  try {
    const info = ipcRenderer.sendSync('daemon-connection-info') as { port?: unknown; token?: unknown } | null
    if (!info) return
    // Port must be a valid 16-bit TCP port (1..65535).
    if (typeof info.port === 'number' && Number.isInteger(info.port) && info.port > 0 && info.port <= 65535) {
      cachedPort = info.port
    }
    if (typeof info.token === 'string' && TOKEN_SHAPE.test(info.token)) {
      cachedToken = info.token
    }
  } catch {
    // main not ready or transient failure — keep the cached values
  }
}

// Initial read so the first getPort() call has a value
refreshConnectionInfo()

contextBridge.exposeInMainWorld('daemonApi', {
  getPort: () => {
    refreshConnectionInfo()
    return cachedPort
  },
  getToken: () => {
    refreshConnectionInfo()
    return cachedToken
  },
})

// F72 + F79: explicit electronAPI surface the renderer can call to
// reach out to the system — every method is a thin pass-through to an
// ipcMain.handle in main.ts so the renderer never holds the shell /
// dialog refs directly. Keeping this list tight preserves the
// least-privilege posture of the sandboxed renderer.
contextBridge.exposeInMainWorld('electronAPI', {
  // Open a URL in the user's DEFAULT system browser, bypassing
  // Electron's new-window handler. Main allowlists the URL scheme.
  openExternal: (url: string) => ipcRenderer.invoke('open-external', url),
  // Relaunch the whole app (and with it the daemon) from the boot-failure splash.
  restartApp: () => ipcRenderer.invoke('restart-app'),

  // Show a native file/directory picker. Returns { canceled, filePaths }.
  // Pass { properties: ['openDirectory'] } for folder picker (F79 path
  // inputs in Settings/Cesty), ['openFile'] for a single file, etc.
  showOpenDialog: (options?: Electron.OpenDialogOptions) =>
    ipcRenderer.invoke('show-open-dialog', options),

  // Reveal a path in the native file manager (Explorer / Finder). Used
  // by the sites list "Show in folder" action — previously the renderer
  // called this method without preload exposing it, so the fallback
  // `window.open('file://…')` ran and silently failed under Electron's
  // sandboxed renderer. Returns true on success, false on rejection.
  revealInFolder: (targetPath: string) =>
    ipcRenderer.invoke('reveal-in-folder', targetPath),

  // F83 SSO — subscribe to `wdc://auth-callback?token=...` deep-link
  // events forwarded from main. Handler receives { token, error }.
  // Returns an unsubscribe function so components can wire this up in
  // onMounted and release the listener in onBeforeUnmount.
  onSsoCallback: (handler: (payload: { token: string; error: string }) => void) => {
    const listener = (_evt: unknown, payload: { token: string; error: string }) => handler(payload)
    ipcRenderer.on('sso-callback', listener)
    return () => ipcRenderer.removeListener('sso-callback', listener)
  },

  // Hard-reload the renderer from the main process. `window.location.reload()`
  // is unreliable in Electron apps served from the `app://` scheme —
  // sometimes it keeps the existing Pinia store instances alive + bypasses
  // router cleanup. Going through webContents.reloadIgnoringCache() forces
  // a fresh page load that wipes every in-memory state. Used after a
  // factory / settings reset so the UI actually reflects the empty DB.
  restartRenderer: () => ipcRenderer.invoke('restart-renderer'),

  // Forward a renderer-side log line into electron-log's main sink so
  // every Vue console.error / unhandledrejection / fetch failure lands
  // in ~/.wdc/logs/electron/main.log alongside main-process events.
  // Keeps the timeline in one file for support/debug instead of
  // scattered across DevTools consoles that disappear on reload.
  rendererLog: (level: 'info' | 'warn' | 'error' | 'debug', args: unknown[]) =>
    ipcRenderer.invoke('renderer-log', { level, args }),

  // #147 — cross-platform OS notifications. Renderer side fires this
  // on important events (deploy completed, MCP confirm requested, etc.)
  // and main hands off to Electron's Notification API which uses the
  // platform-native mechanism (Win toast, macOS notification centre,
  // Linux libnotify). Main applies the preference gates and resolves to
  // a status string ('shown', 'disabled', 'focused', 'throttled', …).
  osNotify: (payload: {
    title: string
    body?: string
    urgency?: 'low' | 'normal' | 'critical'
    silent?: boolean
    channel: string
    route?: string
    key?: string
    once?: boolean
    test?: boolean
  }) => ipcRenderer.invoke('os-notify', payload),

  // Notification preferences (master switch, background-only, per-channel
  // toggles) persisted by main in userData so main-fired toasts honour them.
  notifyPrefsGet: () => ipcRenderer.invoke('notify-prefs-get'),
  notifyPrefsSet: (patch: Record<string, unknown>) => ipcRenderer.invoke('notify-prefs-set', patch),

  // Clicking an OS notification focuses the window and main sends the
  // notification's app route here; the renderer does router.push. Returns
  // an unsubscribe function, same pattern as onSsoCallback.
  onNavigate: (handler: (route: string) => void) => {
    const listener = (_evt: unknown, route: string) => handler(route)
    ipcRenderer.on('navigate', listener)
    return () => ipcRenderer.removeListener('navigate', listener)
  },

  // Runtime version probe — reads from Electron's app.getVersion() (which
  // resolves the bundled package.json at process start). Decouples the
  // status-bar version stamp from build-time `import.meta.env.VITE_APP_VERSION`,
  // so a `package.json` bump is reflected after a single Electron restart
  // without a renderer rebuild. The build-time gitShortSha is still baked
  // in (it's tied to the artifact identity) and returned alongside.
  getAppVersion: (): Promise<{ version: string; gitSha: string; full: string }> =>
    ipcRenderer.invoke('app-get-version'),

  // OS login item ("Run at startup"). supported=false in portable/dev builds.
  autostartGet: (): Promise<{ supported: boolean; enabled: boolean }> =>
    ipcRenderer.invoke('autostart-get'),
  autostartSet: (enabled: boolean): Promise<{ supported: boolean; enabled: boolean }> =>
    ipcRenderer.invoke('autostart-set', enabled),

  // Crash-report consent: whether main started Sentry, and a nudge after the
  // Settings privacy switch wrote a new consent so main re-reads it.
  crashReportsState: (): Promise<{ active: boolean; consent: boolean }> =>
    ipcRenderer.invoke('crash-reports-state'),
  telemetryConsentChanged: (): Promise<{ active: boolean; consent: boolean }> =>
    ipcRenderer.invoke('telemetry-consent-changed'),

  // Self-elevation: kill the running daemon and re-spawn it under UAC.
  // Wired to the "Restart as admin" button shown when an admin-required
  // daemon endpoint (hosts apply, hosts restore, etc.) returns 403.
  // Returns { ok: true } once the elevated child has started; the renderer
  // is responsible for waiting on the new daemon port file to appear.
  elevateDaemon: (): Promise<{ ok: boolean; error?: string }> =>
    ipcRenderer.invoke('elevate-daemon'),

  // Resolved path of the bundled MCP server (resources/mcp-server/index.js
  // when packaged) plus whether the daemon's MCP token file exists. Feeds
  // the copy-paste commands on the MCP hub's "Connect an AI client" card.
  getMcpServerInfo: (): Promise<{
    serverPath: string
    serverExists: boolean
    packaged: boolean
    portFilePresent: boolean
  }> => ipcRenderer.invoke('mcp-server-info'),
})
