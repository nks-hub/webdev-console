import { app, BrowserWindow, Tray, Menu, nativeImage, shell, dialog, protocol, net, ipcMain, Notification } from 'electron'
import { dirname, join, resolve, sep } from 'path'
import { pathToFileURL } from 'node:url'
import { spawn, ChildProcess } from 'child_process'
import { readFileSync, writeFileSync, existsSync, unlinkSync, rmSync, readdirSync, lstatSync, readlinkSync } from 'fs'
import { tmpdir, homedir } from 'os'
import http from 'http'
import * as Sentry from '@sentry/electron/main'
import log from 'electron-log/main'
// Static import of electron-updater so vite/rollup bundles the actual
// updater classes (MacUpdater, NsisUpdater, AppImageUpdater, …) into
// dist-electron/main.js. Previously loaded via `await import(...)` on
// demand, which the vite externalize plugin treated as external and
// left unresolved — packaged asar then threw ERR_MODULE_NOT_FOUND on
// every update probe and users never saw an update notification.
// Using a lazy getter (Proxy) keeps the graceful-degradation behaviour:
// the guarded call sites that check whether updater exists still work
// because the import itself is always available now.
import { autoUpdater as electronAutoUpdater } from 'electron-updater'

// Wire `console.log`/`console.warn`/`console.error` (and daemon stdout,
// which we currently forward via `console.log('[daemon]', ...)` below)
// into a rotating file under ~/.wdc/logs/electron/main.log — same tree
// as the daemon's own logs so factory reset wipes both in one shot.
// Before this, every `console.log` from main + the relayed `[daemon]`
// output disappeared the moment Electron was launched from Finder /
// tray — Sentry only captures exceptions + warn+ breadcrumbs, not the
// routine stdout that users need to debug "I saw X in the tray and
// then nothing happened". electron-log.initialize() hooks `console.*`
// and also exposes an explicit `log.info/warn/error` API.
log.initialize()
log.transports.file.level = 'info'
log.transports.file.format = '[{y}-{m}-{d} {h}:{i}:{s}.{ms}] [{level}] {text}'
// Route Electron logs into ~/.wdc/logs/electron/ so every log the app
// writes (daemon and main) sits under the same tree. Factory reset
// wipes ~/.wdc/ and logs go with it. Without this electron-log
// defaults to ~/Library/Logs/@nks-hub/webdev-console/ which survives
// factory reset and makes the log location inconsistent with the
// daemon side.
const wdcLogsDir = join(process.env.WDC_DATA_DIR || join(homedir(), '.wdc'), 'logs', 'electron')
try {
  require('fs').mkdirSync(wdcLogsDir, { recursive: true })
  log.transports.file.resolvePathFn = () => join(wdcLogsDir, 'main.log')
} catch { /* fall back to default path */ }
// Rotation + retention:
//   • 5 MB per file; when main.log crosses that, electron-log renames it
//     to main.old.log (keep the previous one) and starts a fresh main.log.
//   • archiveLogFn customised to keep the last 5 rotations instead of
//     the default 1 — gives users ~25 MB of recent history when
//     debugging without blowing up disk on long-running installs.
log.transports.file.maxSize = 5 * 1024 * 1024
log.transports.file.archiveLogFn = (oldLogFile) => {
  try {
    // electron-log v5 passes a LogFile object (v4 passed a string).
    // Normalize to the underlying path string either way.
    const oldLogPath = typeof oldLogFile === 'string' ? oldLogFile : (oldLogFile as { path: string }).path
    const base = oldLogPath.replace(/\.log$/, '')
    const fs = require('fs') as typeof import('fs')
    // Shift existing .old-N.log files up by one and drop the oldest.
    for (let i = 5; i >= 1; i--) {
      const from = i === 1 ? `${base}.old.log` : `${base}.old-${i - 1}.log`
      const to = `${base}.old-${i}.log`
      try { if (fs.existsSync(from)) {
        if (i === 5 && fs.existsSync(to)) fs.unlinkSync(to)   // drop oldest
        fs.renameSync(from, to)
      } } catch { /* ignore — next rotation tidies up */ }
    }
    fs.renameSync(oldLogPath, `${base}.old.log`)
  } catch (err) {
    // Fall back to electron-log's default archiver (single .old.log) so
    // logging itself doesn't crash on rotation failure.
    console.warn('[log-rotate] custom archiver failed:', err)
  }
}
Object.assign(console, log.functions) // forwards console.log/warn/error/info/debug

// Sentry crash reporting — resolution order:
//   1. Runtime env SENTRY_DSN / NKS_WDC_SENTRY_DSN (self-hosters, dev)
//   2. Build-time default from GitHub Secret SENTRY_DSN_FRONTEND baked
//      in via Vite's define config (process.env.SENTRY_DSN_DEFAULT)
// No DSN literal in git. Empty at every layer = SDK not initialised.
const sentryDsn = (
  process.env.SENTRY_DSN
  || process.env.NKS_WDC_SENTRY_DSN
  || process.env.SENTRY_DSN_DEFAULT  // Vite-injected CI secret fallback
  || ''
).trim()
// Backend DSN is passed to the daemon child process so the .NET app
// doesn't need its own env config.
const daemonSentryDsn = (
  process.env.SENTRY_DSN_BACKEND
  || process.env.SENTRY_DSN_BACKEND_DEFAULT
  || ''
).trim()
const sentryEnvironment = (
  process.env.SENTRY_ENVIRONMENT
  || process.env.SENTRY_ENVIRONMENT_DEFAULT
  || (app.isPackaged ? 'production' : 'development')
)
if (sentryDsn) {
  Sentry.init({
    dsn: sentryDsn,
    // Release tag populated from CFBundleShortVersionString (macOS) /
    // FileVersion (Windows) — same source as the status bar version.
    release: `nks-wdc-electron@${app.getVersion()}`,
    environment: sentryEnvironment,
    tracesSampleRate: parseFloat(process.env.SENTRY_TRACES_SAMPLE_RATE || '0.1'),
    debug: process.env.SENTRY_DEBUG === '1',
    maxBreadcrumbs: 100,
    // Strip anything identifying. Runtime env can still override per-call
    // via Sentry.setUser if the user explicitly opts in.
    beforeSend(event) {
      if (event.user) event.user = { id: undefined, email: undefined, username: undefined, ip_address: undefined }
      if (event.server_name) event.server_name = ''
      return event
    },
    // Electron-specific extras: bundle native crash reports (minidump)
    // when the main process is killed by the OS, and preload renderer
    // crash handler via IPC so uncaught renderer errors reach us even
    // when devtools are closed.
    integrations: (defaultIntegrations) => defaultIntegrations,
    // Breadcrumb on every app lifecycle event for triage context.
    initialScope: {
      tags: {
        platform: process.platform,
        arch: process.arch,
        electron: process.versions.electron,
        node: process.versions.node,
      },
    },
  })
  // Breadcrumb on window lifecycle so crashes tell us whether the user
  // had the app focused, backgrounded, or was about to quit.
  app.on('ready', () => Sentry.addBreadcrumb({ category: 'app', message: 'ready', level: 'info' }))
  app.on('before-quit', () => Sentry.addBreadcrumb({ category: 'app', message: 'before-quit', level: 'info' }))
  app.on('window-all-closed', () => Sentry.addBreadcrumb({ category: 'app', message: 'window-all-closed', level: 'info' }))
}

let win: BrowserWindow | null = null
let tray: Tray | null = null
let daemon: ChildProcess | null = null
let daemonConnected = false
let isQuitting = false
let updaterStatus = 'Idle'
let updateDownloaded = false
let checkingForUpdates = false

// Catalog source: the public NKS catalog at https://wdc.nks-hub.cz (baked
// into SettingsStore.CatalogUrl as the built-in default). The Electron
// shell no longer spawns a local catalog-api sidecar — production users
// must never see a 127.0.0.1:8765 dev stub showing up in their Binaries
// page. Developers who genuinely need a local catalog can run
// `services/catalog-api/run.cmd` manually and override via Settings.

function getUpdateFeedOverride(): string | null {
  const raw = process.env.NKS_WDC_UPDATE_FEED_URL?.trim()
  if (!raw) return null
  return raw.endsWith('/') ? raw : `${raw}/`
}

// Portable mode: if portable.txt exists next to the app, redirect both
// Electron's userData AND the C# daemon's ~/.wdc tree to a local subfolder
// so a USB-stick install leaves no trace on the host machine.
// The daemon honors the WDC_DATA_DIR env var via Core/Services/WdcPaths.cs,
// which every service (SiteManager, BackupManager, TelemetryConsent,
// PluginState, PhpExtensionOverrides, BinaryManager, all plugins) consults.
const appInstallDir = app.isPackaged ? dirname(process.execPath) : app.getAppPath()
const isPortable = existsSync(join(appInstallDir, 'portable.txt'))
  || existsSync(join(process.cwd(), 'portable.txt'))

let portableWdcDir: string | null = null
if (isPortable) {
  // Store all user data next to the app binary instead of %APPDATA%
  const portableDir = join(appInstallDir, 'data')
  app.setPath('userData', portableDir)
  portableWdcDir = join(portableDir, 'wdc')
  process.env.WDC_DATA_DIR = portableWdcDir
  console.log('[portable] mode enabled')
  console.log('[portable]   electron userData:', portableDir)
  console.log('[portable]   daemon WDC_DATA_DIR:', portableWdcDir)
}

const PORT_FILE = join(tmpdir(), 'nks-wdc-daemon.port')

function readPortFile(): { port: number; token: string } | null {
  try {
    if (!existsSync(PORT_FILE)) return null
    const lines = readFileSync(PORT_FILE, 'utf-8').split('\n').filter(Boolean)
    if (lines.length >= 2) return { port: parseInt(lines[0], 10), token: lines[1] }
  } catch {}
  return null
}

function daemonGet<T>(path: string): Promise<T> {
  const info = readPortFile()
  if (!info) return Promise.reject(new Error('No port file'))
  return new Promise((resolve, reject) => {
    const req = http.get(
      `http://localhost:${info.port}${path}`,
      { headers: info.token ? { Authorization: `Bearer ${info.token}` } : {} },
      (res) => {
        let body = ''
        res.on('data', (chunk: Buffer) => (body += chunk.toString()))
        res.on('end', () => {
          try { resolve(JSON.parse(body) as T) } catch (e) { reject(e) }
        })
      }
    )
    req.on('error', reject)
    req.setTimeout(3000, () => { req.destroy(); reject(new Error('timeout')) })
  })
}

function daemonPost(path: string): Promise<void> {
  const info = readPortFile()
  if (!info) return Promise.reject(new Error('No port file'))
  return new Promise((resolve, reject) => {
    const req = http.request(
      `http://localhost:${info.port}${path}`,
      { method: 'POST', headers: info.token ? { Authorization: `Bearer ${info.token}` } : {} },
      (res) => {
        res.on('data', () => {})
        res.on('end', () => resolve())
      }
    )
    req.on('error', reject)
    req.setTimeout(5000, () => { req.destroy(); reject(new Error('timeout')) })
    req.end()
  })
}
function findPackagedDaemonExecutable(): string {
  const exeName = process.platform === 'win32'
    ? 'NKS.WebDevConsole.Daemon.exe'
    : 'NKS.WebDevConsole.Daemon'
  const candidates = [
    join(process.resourcesPath, 'daemon', exeName),
    join(appInstallDir, 'daemon', exeName),
    join(__dirname, '../../daemon/bin', exeName),
  ]

  for (const candidate of candidates) {
    if (existsSync(candidate)) return candidate
  }

  return candidates[0]
}

function findDaemonProject(): string {
  // electron-vite dev: __dirname = src/frontend/dist-electron/
  // Go up to repo root and find the daemon project
  const candidates = [
    join(__dirname, '../../daemon/NKS.WebDevConsole.Daemon'),
    join(__dirname, '../../../src/daemon/NKS.WebDevConsole.Daemon'),
    join(__dirname, '../../../../src/daemon/NKS.WebDevConsole.Daemon'),
  ]
  for (const c of candidates) {
    if (existsSync(join(c, 'NKS.WebDevConsole.Daemon.csproj'))) return c
  }
  return candidates[0] // fallback
}

async function isDaemonAlive(): Promise<boolean> {
  const info = readPortFile()
  if (!info) return false
  try {
    // Must match the `service` marker in our /healthz response. Before
    // this check we treated *any* HTTP 200 as "daemon up" — which meant
    // a stale port file pointing at e.g. macOS Control Center's AirPlay
    // receiver (port 5000) made us skip spawning the real daemon, leaving
    // the app with no backend for its entire lifetime. We verify the
    // marker and treat a mismatch as "port file is stale, spawn a new
    // daemon and let it pick a fresh port".
    const body = await new Promise<string>((resolve, reject) => {
      const req = http.get(`http://localhost:${info.port}/healthz`, (res) => {
        let buf = ''
        res.on('data', (chunk: Buffer) => { buf += chunk.toString() })
        res.on('end', () => resolve(buf))
      })
      req.on('error', reject)
      req.setTimeout(2000, () => { req.destroy(); reject(new Error('timeout')) })
    })
    try {
      const parsed = JSON.parse(body) as { service?: string; ok?: boolean; version?: string }
      if (parsed.service !== 'nks-wdc-daemon' || parsed.ok !== true) {
        console.warn('[daemon] /healthz signature mismatch, treating port file as stale:', body.slice(0, 120))
        return false
      }
      // Version skew check — auto-updated frontends used to attach to a
      // leftover older daemon process and show "frontend 0.2.18 + daemon
      // 0.2.2" mismatches in Settings > About. The daemon's binary can't
      // be hot-swapped while it's running, so the only correctness-preserving
      // option is to kill it and spawn the one bundled with this app.
      const appVersion = app.getVersion()
      const daemonVersion = (parsed.version || '').split('+')[0].trim()
      if (daemonVersion && daemonVersion !== appVersion) {
        console.warn(`[daemon] version skew — app=${appVersion} daemon=${daemonVersion} — stopping stale daemon`)
        try {
          await new Promise<void>((resolve) => {
            const req = http.request(
              `http://localhost:${info.port}/api/admin/shutdown`,
              {
                method: 'POST',
                headers: info.token ? { Authorization: `Bearer ${info.token}` } : {},
              },
              (res) => { res.on('data', () => {}); res.on('end', () => resolve()) },
            )
            req.on('error', () => resolve())
            req.setTimeout(2000, () => { req.destroy(); resolve() })
            req.end()
          })
          // Give the old daemon a moment to release the port file
          await new Promise(r => setTimeout(r, 1500))
        } catch { /* fall through — the spawn loop will pick a new port anyway */ }
        return false
      }
      return true
    } catch {
      console.warn('[daemon] /healthz returned non-JSON, treating port file as stale:', body.slice(0, 120))
      return false
    }
  } catch {
    return false
  }
}

async function spawnDaemon() {
  // If a daemon is already running (e.g. started from CLI), reuse it
  if (await isDaemonAlive()) {
    console.log('[daemon] already running (port file exists and responds), reusing')
    daemonConnected = true
    updateTray()
    return
  }

  const isDev = !app.isPackaged

  // Pass the portable data directory through explicitly so the daemon's
  // WdcPaths helper redirects ~/.wdc/* to the USB-stick-local folder.
  // Catalog URL intentionally NOT forced via env — daemon reads
  // SettingsStore (user pref) first, then env, then defaults to
  // https://wdc.nks-hub.cz (public production catalog). Shipped builds
  // must never auto-point at a local 127.0.0.1 dev catalog.
  const daemonEnv: NodeJS.ProcessEnv = { ...process.env }
  if (portableWdcDir) daemonEnv.WDC_DATA_DIR = portableWdcDir
  // Forward the backend Sentry DSN to the daemon. Runtime SENTRY_DSN from
  // user env always wins; only set the fallback from the Vite-baked CI
  // secret when nothing else is present. Backend gets its own DSN because
  // it's a separate Sentry project from the Electron one (27 vs 28).
  if (!daemonEnv.SENTRY_DSN && daemonSentryDsn) {
    daemonEnv.SENTRY_DSN = daemonSentryDsn
  }
  if (!daemonEnv.SENTRY_ENVIRONMENT) {
    daemonEnv.SENTRY_ENVIRONMENT = sentryEnvironment
  }

  if (isDev) {
    const projectDir = findDaemonProject()
    // Dev mode: pass CiBuild=true so the daemon builds with app.ci.manifest
    // (asInvoker, no requireAdministrator). Without this, `dotnet run`
    // fails on Windows with "Požadovaná operace vyžaduje zvýšená oprávnění"
    // because the prod manifest forces UAC and a non-elevated parent
    // (`npm run dev`) can't satisfy that. Side effect: dev daemon cannot
    // edit hosts/Apache, but UI iteration doesn't need that.
    const dotnetArgs = ['run', '--project', projectDir, '-p:CiBuild=true']
    log.info('[daemon] spawn (dev): dotnet', dotnetArgs.join(' '))
    daemon = spawn('dotnet', dotnetArgs, {
      stdio: 'pipe',
      detached: false,
      env: daemonEnv,
    })
  } else {
    const daemonExe = findPackagedDaemonExecutable()
    if (!existsSync(daemonExe)) {
      throw new Error(`Packaged daemon executable not found: ${daemonExe}`)
    }
    log.info('[daemon] spawn (packaged):', daemonExe)
    daemon = spawn(daemonExe, [], { stdio: 'pipe', detached: false, env: daemonEnv })
  }
  log.info(`[daemon] spawned pid=${daemon.pid}`)

  daemon.stdout?.on('data', (d) => {
    try { console.log('[daemon]', d.toString().trim()) } catch {}
  })
  daemon.stderr?.on('data', (d) => {
    try { console.error('[daemon err]', d.toString().trim()) } catch {}
  })
  daemon.on('error', (err) => {
    console.error('[daemon] process error:', err.message)
  })
  daemon.on('exit', (code) => {
    console.log(`[daemon] exited code=${code}`)
    daemonConnected = false
    daemon = null
    updateTray()
    // F91.7: exit code 99 = "restart me" contract from the daemon's
    // /api/admin/restart endpoint. Don't respawn while the whole app is
    // quitting — we'd keep the process alive past Electron's own exit.
    if (code === 99 && !isQuitting) {
      console.log('[daemon] restart requested (exit 99) — respawning…')
      // Short delay to let the port file disappear so the new daemon can
      // claim a fresh one cleanly.
      setTimeout(() => { void spawnDaemon() }, 400)
    }
    // Exit 98 = TRUE factory reset. Daemon has stopped all services and
    // wiped the DB before exiting. Electron must now nuke every piece of
    // persistent state — ~/.wdc/, Electron userData (localStorage token,
    // cookies, caches), macOS plist, PHP shim symlinks — before spawning
    // a fresh daemon. The daemon can't do this itself because it can't
    // delete its own running binary + its open SQLite handle. Cross-
    // platform: paths resolved via os.homedir() + app.getPath('userData')
    // so the same codepath works on Win/macOS/Linux.
    if (code === 98 && !isQuitting) {
      console.log('[daemon] FACTORY RESET signal (exit 98) — wiping all state')
      void performFactoryWipe().finally(() => {
        setTimeout(() => { void spawnDaemon() }, 500)
      })
    }
  })

  // Poll until port file appears
  let attempts = 0
  const check = setInterval(() => {
    if (existsSync(PORT_FILE)) {
      daemonConnected = true
      updateTray()
      clearInterval(check)
    }
    if (++attempts > 30) clearInterval(check)
  }, 500)
}

async function waitForPortFile(maxWaitMs = 15000): Promise<{ port: number; token: string } | null> {
  const start = Date.now()
  while (Date.now() - start < maxWaitMs) {
    const info = readPortFile()
    if (info && info.token) return info
    await new Promise(r => setTimeout(r, 300))
  }
  return readPortFile()
}

// Window state persistence — per plan Phase 4 "remember size/position".
// Stored as a tiny JSON blob in app.getPath('userData')/window-state.json so
// it travels with the user profile (or with the portable.txt override).
// Defaults are used when the file is missing, corrupted, or off-screen.
interface WindowState { width: number; height: number; x?: number; y?: number; maximized?: boolean }

function windowStateFile(): string {
  return join(app.getPath('userData'), 'window-state.json')
}

function loadWindowState(): WindowState {
  const defaults: WindowState = { width: 960, height: 640 }
  try {
    const raw = readFileSync(windowStateFile(), 'utf-8')
    const parsed = JSON.parse(raw) as Partial<WindowState>
    if (typeof parsed.width === 'number' && typeof parsed.height === 'number'
      && parsed.width >= 400 && parsed.height >= 300
      && parsed.width <= 10000 && parsed.height <= 10000) {
      return {
        width: parsed.width,
        height: parsed.height,
        x: typeof parsed.x === 'number' ? parsed.x : undefined,
        y: typeof parsed.y === 'number' ? parsed.y : undefined,
        maximized: !!parsed.maximized,
      }
    }
  } catch { /* missing or malformed — use defaults */ }
  return defaults
}

function saveWindowState(state: WindowState): void {
  try {
    const dir = app.getPath('userData')
    if (!existsSync(dir)) return
    writeFileSync(windowStateFile(), JSON.stringify(state, null, 2))
  } catch { /* non-fatal — we'd rather lose window state than crash the app */ }
}

async function createWindow() {
  log.info('[window] creating main BrowserWindow')
  const saved = loadWindowState()
  win = new BrowserWindow({
    width: saved.width,
    height: saved.height,
    x: saved.x,
    y: saved.y,
    backgroundColor: '#141620',
    show: false,
    webPreferences: {
      preload: join(__dirname, 'preload.js'),
      contextIsolation: true,
      sandbox: false
    }
  })

  if (saved.maximized) win.maximize()

  // Wait for daemon port file before loading renderer
  const info = await waitForPortFile()

  if (process.env.ELECTRON_RENDERER_URL) {
    const params = new URLSearchParams()
    if (info?.port) params.set('port', String(info.port))
    if (info?.token) params.set('token', info.token)
    const qs = params.toString()
    win.loadURL(`${process.env.ELECTRON_RENDERER_URL}${qs ? '?' + qs : ''}`)
  } else {
    // Production: load the bundled renderer via custom app:// protocol
    // so localStorage gets a stable origin instead of an opaque file:// origin.
    const params = new URLSearchParams()
    if (info?.port) params.set('port', String(info.port))
    if (info?.token) params.set('token', info.token)
    const qs = params.toString()
    win.loadURL(`app://nks-wdc/index.html${qs ? '?' + qs : ''}`)
  }

  // Remove default menu bar (File/Edit/View/Window/Help)
  win.setMenuBarVisibility(false)
  win.setAutoHideMenuBar(true)

  // Hard-deny `window.open` and target=_blank links — the renderer never
  // legitimately opens new BrowserWindows. Anything that asks goes through
  // the explicit electronAPI.openExternal IPC (allowlisted to https/mailto)
  // instead. Without this an XSS in the renderer could spawn a child window
  // pointing at an attacker URL with the same preload bridge attached.
  win.webContents.setWindowOpenHandler(() => ({ action: 'deny' }))

  // Lock the renderer to its own origin. The legitimate URL is either
  // app://nks-wdc/index.html (packaged) or the dev server (ELECTRON_RENDERER_URL).
  // Any will-navigate event to anything else — http(s) redirects from a
  // fetched page, javascript: links, file://, attacker domains via XSS — is
  // cancelled. The renderer keeps using router.push for in-app navigation
  // (no will-navigate fires for hash/HTML5 history changes).
  const allowedOrigins = new Set<string>(['app://nks-wdc'])
  if (process.env.ELECTRON_RENDERER_URL) {
    try { allowedOrigins.add(new URL(process.env.ELECTRON_RENDERER_URL).origin) } catch { /* ignore */ }
  }
  win.webContents.on('will-navigate', (evt, url) => {
    try {
      const target = new URL(url)
      if (!allowedOrigins.has(target.origin)) {
        console.warn('[security] blocked will-navigate to', target.origin)
        evt.preventDefault()
      }
    } catch {
      evt.preventDefault()
    }
  })

  win.once('ready-to-show', () => win?.show())

  // Minimize to tray on close instead of quitting
  win.on('close', (e) => {
    if (!isQuitting) {
      e.preventDefault()
      win?.hide()
    }
  })

  // Persist window state on resize/move/close so next launch restores the
  // exact same footprint. Debounced via a trailing timer so we don't thrash
  // the disk while the user is actively dragging / resizing.
  let saveTimer: NodeJS.Timeout | null = null
  const schedulePersist = () => {
    if (saveTimer) clearTimeout(saveTimer)
    saveTimer = setTimeout(() => {
      if (!win || win.isDestroyed()) return
      const bounds = win.getBounds()
      saveWindowState({
        width: bounds.width,
        height: bounds.height,
        x: bounds.x,
        y: bounds.y,
        maximized: win.isMaximized(),
      })
    }, 500)
  }
  win.on('resize', schedulePersist)
  win.on('move', schedulePersist)
  win.on('maximize', schedulePersist)
  win.on('unmaximize', schedulePersist)
}

interface ServiceEntry {
  id: string
  name: string
  status: string
}

async function buildServiceSubmenu(): Promise<Electron.MenuItemConstructorOptions[]> {
  try {
    const services = await daemonGet<ServiceEntry[]>('/api/services')
    if (!services || services.length === 0) {
      return [{ label: 'No services', enabled: false }]
    }
    const items: Electron.MenuItemConstructorOptions[] = []
    for (const svc of services) {
      const running = svc.status === 'running'
      items.push({
        label: `${svc.name} (${svc.status})`,
        submenu: [
          {
            label: 'Start',
            enabled: !running,
            click: () => { daemonPost(`/api/services/${svc.id}/start`).then(() => updateTray()) }
          },
          {
            label: 'Stop',
            enabled: running,
            click: () => { daemonPost(`/api/services/${svc.id}/stop`).then(() => updateTray()) }
          }
        ]
      })
    }
    items.push({ type: 'separator' })
    items.push({ label: 'Manage...', click: () => { win?.show(); win?.focus() } })
    return items
  } catch {
    return [{ label: 'Daemon offline', enabled: false }]
  }
}

async function updateTray() {
  // Guard against calls during teardown. Previously factory-reset +
  // webContents.reloadIgnoringCache() sequence could schedule an async
  // updateTray (after /api/services await) that resolved after the
  // Tray had already been destroyed → "TypeError: Object has been
  // destroyed" on `tray.setImage(...)`. isDestroyed() is false for a
  // freshly-created tray that isn't yet showing; we only short-circuit
  // when the native object is genuinely gone.
  if (!tray || tray.isDestroyed()) return

  // Dynamic icon color based on state
  let iconColor: 'green' | 'red' | 'yellow' | 'gray' = 'gray'
  if (daemonConnected) {
    try {
      // Minimal shape: tray only needs `state` to tint its icon. Full
      // ServiceInfo isn't imported here because electron/main.ts is the
      // backend side of the bridge and wants zero coupling to the src/api.
      const services = await daemonGet<Array<{ state: number }>>('/api/services')
      const crashed = services.filter(s => s.state === 4).length
      const running = services.filter(s => s.state === 2).length
      if (crashed > 0) iconColor = 'red'
      else if (running > 0) iconColor = 'green'
      else iconColor = 'yellow'
    } catch {
      iconColor = 'green' // connected but can't query — assume OK
    }
  }
  tray.setImage(createTrayIcon(iconColor))

  const label = daemonConnected ? 'NKS WebDev Console (connected)' : 'NKS WebDev Console (disconnected)'
  tray.setToolTip(`NKS WDC — ${daemonConnected ? 'Connected' : 'Offline'}`)

  const serviceItems = await buildServiceSubmenu()
  const updaterItems: Electron.MenuItemConstructorOptions[] = app.isPackaged && !isPortable
    ? [
        {
          label: checkingForUpdates ? 'Checking for updates...' : `Updates: ${updaterStatus}`,
          enabled: false,
        },
        {
          label: updateDownloaded ? 'Install Update and Restart' : 'Check for Updates',
          enabled: !checkingForUpdates,
          click: async () => {
            if (updateDownloaded) {
              isQuitting = true
              try {
                const autoUpdater = electronAutoUpdater
                autoUpdater.quitAndInstall(false, true)
              } catch (error) {
                const message = error instanceof Error ? error.message : String(error)
                void dialog.showMessageBox({
                  type: 'error',
                  title: 'Update failed',
                  message: `Unable to install the downloaded update: ${message}`,
                })
              }
              return
            }

            void checkForAppUpdates(true)
          }
        }
      ]
    : [{ label: 'Updates disabled in portable/dev mode', enabled: false }]

  const menu = Menu.buildFromTemplate([
    { label, enabled: false },
    { type: 'separator' },
    {
      label: win?.isVisible() ? 'Hide Window' : 'Show Window',
      click: () => {
        if (win?.isVisible()) {
          win.hide()
        } else {
          win?.show()
          win?.focus()
        }
        updateTray()
      }
    },
    { type: 'separator' },
    {
      label: 'Services',
      submenu: serviceItems
    },
    { type: 'separator' },
    {
      label: 'Updates',
      submenu: updaterItems
    },
    { type: 'separator' },
    {
      label: 'Quit',
      click: async () => {
        isQuitting = true
        await shutdownDaemon()
        app.quit()
      }
    }
  ])
  tray.setContextMenu(menu)
}

// Task 16: tray icon now uses pre-rendered NKS logo + state-color corner
// dot (green/red/yellow/gray). The old procedural colored-circle fallback
// is kept as an in-process failsafe if the asset files can't be located
// (e.g. custom build layouts).
function createTrayIcon(color: 'green' | 'red' | 'yellow' | 'gray'): Electron.NativeImage {
  // Resolve icon path — dev runs from src/frontend/electron, packaged
  // app places build/ under resources/. We try both so the same binary
  // works in both modes.
  const candidates = [
    join(__dirname, '..', 'build', `icon-tray-${color}.png`),
    join(__dirname, '..', '..', 'build', `icon-tray-${color}.png`),
    join(process.resourcesPath || '', 'build', `icon-tray-${color}.png`),
  ]
  for (const p of candidates) {
    try {
      const img = nativeImage.createFromPath(p)
      if (!img.isEmpty()) return img
    } catch { /* try next */ }
  }
  // Procedural fallback — original behavior, used when asset files
  // aren't found on disk (e.g. headless test harness).
  const size = 16
  const buf = Buffer.alloc(size * size * 4)
  const colors: Record<string, [number, number, number]> = {
    green:  [0x22, 0xc5, 0x5e],
    red:    [0xef, 0x44, 0x44],
    yellow: [0xf5, 0x9e, 0x0b],
    gray:   [0x64, 0x74, 0x8b],
  }
  const [r, g, b] = colors[color]
  const cx = size / 2, cy = size / 2, radius = 6
  for (let y = 0; y < size; y++) {
    for (let x = 0; x < size; x++) {
      const dist = Math.sqrt((x - cx) ** 2 + (y - cy) ** 2)
      const i = (y * size + x) * 4
      if (dist <= radius) {
        buf[i] = r; buf[i + 1] = g; buf[i + 2] = b; buf[i + 3] = 0xff
      }
    }
  }
  return nativeImage.createFromBuffer(buf, { width: size, height: size })
}

function createTray() {
  const icon = createTrayIcon(daemonConnected ? 'green' : 'gray')
  tray = new Tray(icon)
  log.info('[tray] created (daemonConnected=' + daemonConnected + ')')
  updateTray()
  tray.on('click', () => {
    if (win?.isVisible()) {
      log.info('[tray] click → hide window')
      win.hide()
    } else {
      log.info('[tray] click → show window')
      win?.show()
      win?.focus()
    }
    updateTray()
  })
}

async function checkForAppUpdates(showNoUpdateDialog = false) {
  if (!app.isPackaged || isPortable || checkingForUpdates) return

  checkingForUpdates = true
  updaterStatus = 'Checking'
  updateTray()

  try {
    const autoUpdater = electronAutoUpdater
    const result = await autoUpdater.checkForUpdates()
    if (!result?.updateInfo?.version) {
      updaterStatus = 'No update available'
      if (showNoUpdateDialog) {
        await dialog.showMessageBox({
          type: 'info',
          title: 'No updates available',
          message: 'NKS WebDev Console is already up to date.',
        })
      }
    }
  } catch (error) {
    updaterStatus = 'Update check failed'
    const message = error instanceof Error ? error.message : String(error)
    console.error('[updater] check failed:', message)
    if (showNoUpdateDialog) {
      await dialog.showMessageBox({
        type: 'error',
        title: 'Update check failed',
        message,
      })
    }
  } finally {
    checkingForUpdates = false
    updateTray()
  }
}

async function setupAutoUpdater() {
  if (!app.isPackaged || isPortable) {
    updaterStatus = 'Disabled'
    updateTray()
    return
  }

  try {
    const autoUpdater = electronAutoUpdater
    const feedOverride = getUpdateFeedOverride()
    if (feedOverride) {
      autoUpdater.setFeedURL({ provider: 'generic', url: feedOverride })
      console.log('[updater] using generic feed override:', feedOverride)
    }
    autoUpdater.autoDownload = true
    autoUpdater.autoInstallOnAppQuit = true

    autoUpdater.on('checking-for-update', () => {
      checkingForUpdates = true
      updaterStatus = 'Checking'
      updateTray()
    })
    autoUpdater.on('update-available', (info) => {
      updaterStatus = `Downloading ${info.version}`
      updateTray()
      console.log('[updater] Update available:', info.version)
    })
    autoUpdater.on('update-not-available', () => {
      checkingForUpdates = false
      updaterStatus = 'Up to date'
      updateTray()
      console.log('[updater] No update available')
    })
    autoUpdater.on('download-progress', (progress) => {
      updaterStatus = `Downloading ${Math.round(progress.percent)}%`
      updateTray()
    })
    autoUpdater.on('update-downloaded', async (info) => {
      checkingForUpdates = false
      updateDownloaded = true
      updaterStatus = `Ready to install ${info.version}`
      updateTray()
      console.log('[updater] Update downloaded:', info.version)

      const result = await dialog.showMessageBox({
        type: 'info',
        title: 'Update ready',
        message: `Version ${info.version} has been downloaded.`,
        detail: 'Restart now to install the update, or quit later and it will be installed automatically on exit.',
        buttons: ['Restart and Install', 'Later'],
        defaultId: 0,
        cancelId: 1,
      })

      if (result.response === 0) {
        isQuitting = true
        autoUpdater.quitAndInstall(false, true)
      }
    })
    autoUpdater.on('error', (error) => {
      checkingForUpdates = false
      updaterStatus = 'Update error'
      updateTray()
      console.error('[updater] error:', error?.message ?? error)
    })

    void checkForAppUpdates(false)
  } catch (error) {
    updaterStatus = 'Unavailable'
    updateTray()
    console.warn('[updater] electron-updater unavailable:', error)
  }
}

app.on('before-quit', async (event) => {
  if (!isQuitting) {
    // First quit trigger — pause the default quit so we can gracefully
    // stop the daemon process first, then re-emit quit. Without this,
    // macOS Cmd+Q from the menu bar would leave an orphan .NET process
    // that'd still hold ports + files; next launch would attach to it
    // and inherit its state instead of starting clean.
    isQuitting = true
    event.preventDefault()
    await shutdownDaemon()
    app.quit()
  }
})

/**
 * Stop the daemon regardless of whether we spawned it or adopted an
 * existing one via isDaemonAlive.
 *
 *  - HTTP shutdown reaches adopted daemons too (we only have `daemon` as
 *    a child_process handle when WE spawned it; for reused daemons it's
 *    null and `daemon?.kill()` would no-op).
 *  - The child_process.kill() afterwards catches cases where the HTTP
 *    call returned fast but the process is still finishing its own exit
 *    sequence — belt and suspenders.
 *
 * User requirement: "when quit from tray, kill backend". Before this the
 * tray-Quit only called `daemon?.kill()` which did nothing on an adopted
 * daemon, leaving a .NET process running after the user explicitly quit.
 */
/**
 * True factory reset — wipe every piece of WDC state the user accumulated.
 * Runs between a daemon exit(98) and the next spawnDaemon(). Cross-platform:
 * the same function resolves the right paths on Win/macOS/Linux via
 * os.homedir() + app.getPath('userData'). What we nuke, and why:
 *
 * 1. **~/.wdc/** (daemon data root — same path on all three OSes unless the
 *    user set WDC_DATA_DIR) — state.db, sites/, binaries/, ssl/, logs/,
 *    generated/, plugins/, backups/, cloudflare/. Without this the daemon
 *    re-reads site TOMLs on next boot and "factory reset did nothing".
 *
 * 2. **Electron userData** (`app.getPath('userData')`) — Local Storage
 *    levelDB where authStore stashes `nks-wdc-sso-token` (user reported
 *    "stayed logged in" after reset). Also Cookies, Cache, Preferences,
 *    Session Storage, window-state.json. Crashpad dir is preserved so
 *    diagnostics survive the wipe.
 *
 * 3. **macOS plist** `~/Library/Preferences/com.nks-hub.webdev-console.plist`
 *    — Sparkle auto-update state, SUUpdateGroupIdentifier, window frames.
 *    Windows / Linux don't have an equivalent outside Electron userData
 *    which #2 already handles.
 *
 * 4. **PHP CLI shim symlinks** in `~/.local/bin/php<MM>` (macOS+Linux) —
 *    we created these; they point into the app bundle and would dangle
 *    after uninstall. Only delete ones whose `realpath` lands inside our
 *    managed shim dir so we don't clobber user-owned `php85` symlinks.
 *
 * 5. **Temp port file** — `$TMPDIR/nks-wdc-daemon.port` so the next spawn
 *    doesn't briefly reuse stale port + auth.
 *
 * We deliberately DO NOT touch:
 *   • mkcert's root CA (~/Library/Application Support/mkcert, etc.) — it's
 *     shared with other dev tools and re-installing it requires admin.
 *   • Windows/macOS Keychain entries — we don't write any from the daemon.
 *     (catalog SSO token is only in Electron userData, #2.)
 */
async function performFactoryWipe() {
  function safeRm(path: string, label: string) {
    try {
      if (existsSync(path)) {
        rmSync(path, { recursive: true, force: true })
        console.log(`[factory-reset] wiped ${label}: ${path}`)
      }
    } catch (err) {
      console.warn(`[factory-reset] could not wipe ${label}:`, err)
    }
  }

  // 1. Daemon data root — WDC_DATA_DIR takes precedence (portable mode),
  //    else default ~/.wdc which works the same on Win/macOS/Linux.
  const wdcRoot = process.env.WDC_DATA_DIR || join(homedir(), '.wdc')
  safeRm(wdcRoot, 'daemon data root')

  // 2. Electron userData — wipe the whole dir so the `@nks-hub/` parent
  //    wrapper also goes (user complained "v application support je stale
  //    @nks-hub"). We nuke the parent one level up, which gets @nks-hub/
  //    AND any sibling WDC-related trees Electron might have created.
  //    Crashpad sat under userData/ before — Electron auto-recreates it
  //    on next launch, so preserving it across a factory reset isn't
  //    worth keeping a partial artifact lying around.
  try {
    const userData = app.getPath('userData')           // .../Application Support/@nks-hub/webdev-console
    safeRm(userData, 'Electron userData')
    // Also nuke the @nks-hub parent wrapper if it's now empty so the user
    // doesn't see a stray `@nks-hub/` folder in Application Support. We
    // resolve the parent dynamically — on macOS it's the @org wrapper;
    // on Win/Linux the userData path convention has no such wrapper so
    // the unlink becomes a harmless no-op.
    const parent = join(userData, '..')
    try {
      if (existsSync(parent) && readdirSync(parent).length === 0) {
        safeRm(parent, 'Electron userData parent (@nks-hub)')
      }
    } catch { /* harmless */ }
  } catch (err) {
    console.warn('[factory-reset] userData wipe failed:', err)
  }

  // 3. Platform-specific prefs & caches.
  if (process.platform === 'darwin') {
    const bundleId = 'com.nks-hub.webdev-console'
    const lib = join(homedir(), 'Library')
    safeRm(join(lib, 'Preferences', `${bundleId}.plist`),                 'macOS plist (Preferences)')
    safeRm(join(lib, 'Caches', bundleId),                                 'macOS Caches')
    safeRm(join(lib, 'WebKit', bundleId),                                 'macOS WebKit data')
    safeRm(join(lib, 'Saved Application State', `${bundleId}.savedState`),'macOS Saved Application State')
    safeRm(join(lib, 'HTTPStorages', `${bundleId}.binarycookies`),        'macOS HTTPStorages cookies')
    // ByHost per-machine plists (UUID-prefixed) — enumerate + delete any
    // that match our bundle id.
    try {
      const byHost = join(lib, 'Preferences', 'ByHost')
      if (existsSync(byHost)) {
        for (const name of readdirSync(byHost)) {
          if (name.startsWith(bundleId)) {
            safeRm(join(byHost, name), `macOS ByHost/${name}`)
          }
        }
      }
    } catch { /* harmless */ }
    // Defaults domain (in case something slipped past the plist delete
    // — `defaults delete` clears the in-memory registered copy too).
    try {
      const { spawnSync } = require('child_process') as typeof import('child_process')
      spawnSync('defaults', ['delete', bundleId], { stdio: 'ignore' })
      console.log(`[factory-reset] defaults domain ${bundleId} cleared`)
    } catch { /* harmless */ }
  } else if (process.platform === 'linux') {
    // Linux Electron userData is under ~/.config/@nks-hub/webdev-console,
    // already covered by step 2. Additionally clean the cache dir Electron
    // writes at ~/.cache/@nks-hub/webdev-console/.
    const xdgCache = process.env.XDG_CACHE_HOME || join(homedir(), '.cache')
    safeRm(join(xdgCache, '@nks-hub', 'webdev-console'), 'Linux XDG cache')
    safeRm(join(xdgCache, '@nks-hub'),                   'Linux XDG cache parent')
  }
  // Windows: Electron's userData already covers %APPDATA%\@nks-hub\webdev-console.
  // %LOCALAPPDATA% also has a copy for cache/GPU data — wipe that too.
  else if (process.platform === 'win32') {
    const local = process.env.LOCALAPPDATA
    if (local) {
      safeRm(join(local, '@nks-hub', 'webdev-console'), 'Windows LocalAppData')
      safeRm(join(local, '@nks-hub'),                   'Windows LocalAppData parent')
    }
  }

  // 4. PHP CLI shim symlinks from our managed shim dir (~/.local/bin on Unix).
  if (process.platform !== 'win32') {
    const localBin = join(homedir(), '.local', 'bin')
    if (existsSync(localBin)) {
      for (const name of readdirSync(localBin)) {
        if (!/^php\d+$/.test(name)) continue
        const linkPath = join(localBin, name)
        try {
          const st = lstatSync(linkPath)
          if (!st.isSymbolicLink()) continue
          const target = readlinkSync(linkPath)
          // Only remove symlinks we made — target must reference our app
          // bundle or the dev-build daemon/bin path. Don't clobber an
          // unrelated user-owned phpXX alias.
          if (target.includes('NKS WebDev Console') || target.includes('NKS.WebDevConsole') || target.includes('/.wdc/')) {
            unlinkSync(linkPath)
            console.log(`[factory-reset] removed shim symlink: ${linkPath}`)
          }
        } catch { /* not readable — skip */ }
      }
    }
  }

  // 5. Temp port file.
  try { if (existsSync(PORT_FILE)) unlinkSync(PORT_FILE) } catch { /* harmless */ }

  // Tell the renderer its session is done — blank it so the user sees a
  // fresh boot, not lingering Pinia state pointing at the dead daemon.
  try {
    const w = BrowserWindow.getAllWindows()[0]
    if (w) w.webContents.reloadIgnoringCache()
  } catch { /* window may have already closed */ }
}

async function shutdownDaemon() {
  try { await daemonPost('/api/admin/shutdown') } catch { /* already dead or unreachable */ }
  try { daemon?.kill() } catch { /* already dead */ }
  daemon = null
  // Best-effort port file cleanup so the next app launch sees a clean
  // slate instead of having to wait for its own isDaemonAlive probe.
  try {
    if (existsSync(PORT_FILE)) unlinkSync(PORT_FILE)
  } catch { /* harmless */ }
}

// Enable Chrome DevTools Protocol in dev mode so tooling/tests can read renderer console
protocol.registerSchemesAsPrivileged([
  { scheme: 'app', privileges: { standard: true, secure: true, supportFetchAPI: true, stream: true } }
])

// F83 SSO — register `wdc://` as the deep-link scheme used by catalog-api
// to hand a session token back to the desktop app after the OIDC
// redirect dance completes. `setAsDefaultProtocolClient` is idempotent
// on Windows (just writes registry on first call) and a no-op on Linux
// when the app is not yet installed via the desktop file. We also claim
// single-instance so a second `wdc://...` launch forwards the URL to
// the already-running window instead of opening a ghost process.
const gotSingleInstance = app.requestSingleInstanceLock()
if (!gotSingleInstance) {
  // app.quit() is asynchronous — emits before-quit and waits for the
  // app lifecycle, which leaves time for app.whenReady() handlers below
  // to fire (spawning a redundant daemon, creating a duplicate window,
  // and clobbering the original daemon's port file). app.exit(0) bails
  // out synchronously so the second-instance event in the original
  // process is the only thing the OS-level wdc:// launch triggers.
  app.exit(0)
} else {
  if (process.defaultApp) {
    // F91.5: dev-mode Electron is invoked as `electron.exe <app-path> …`,
    // where <app-path> is frequently `.` (relative to the spawning CWD).
    // When Windows' protocol handler later launches electron.exe from
    // system32, that relative `.` resolves to `C:\Windows\system32` and
    // we crash with "Cannot find module 'C:\Windows\system32'". Passing
    // path.resolve() freezes an absolute path into the registry entry so
    // the handler launch always finds our main.js regardless of CWD.
    if (process.argv.length >= 2) {
      const appPath = resolve(process.argv[1])
      app.setAsDefaultProtocolClient('wdc', process.execPath, [appPath])
    }
  } else {
    app.setAsDefaultProtocolClient('wdc')
  }
}

/** Extracts a `wdc://auth-callback?token=...` URL from command-line args
 * (Windows second-instance) or a macOS 'open-url' event payload, and
 * forwards it to the renderer as a structured IPC event. Empty/unknown
 * inputs are ignored — we never trust the payload beyond shape. */
function forwardSsoCallback(rawUrl: string) {
  // F91.14: trace what the OS handed us — but NEVER log the URL search
  // string or rawUrl (token sits in plaintext in `?token=...`). Earlier
  // versions logged `rawUrl` and `u.search` directly into
  // ~/.wdc/logs/electron/main.log; on a multi-user box or a shipped log
  // bundle that token leaks for the JWT's full TTL. Log only structural
  // metadata (protocol/host/path/length) so support can still triage
  // "callback was malformed" vs "token missing" without exposing creds.
  console.log('[sso] forwardSsoCallback received deep-link (length=%d)', rawUrl.length)
  try {
    const u = new URL(rawUrl)
    console.log('[sso] parsed: protocol=%s hostname=%s pathname=%s hasQuery=%s',
      u.protocol, u.hostname, u.pathname, u.search.length > 0)
    if (u.protocol !== 'wdc:') {
      console.warn('[sso] protocol mismatch — ignoring')
      return
    }
    // STRICT match on BOTH hostname AND a normalized empty/root path.
    // Earlier `host !== X || path !== Y` (de-Morganed: `host !== X && path !== Y`)
    // was an OR-guard — `wdc://attacker//auth-callback?token=...` parses to
    // hostname='attacker' (FAIL first) and pathname='//auth-callback' (PASS
    // second), so the OR pulled it through. Demonstrated exploit forwarded
    // an attacker-chosen JWT to the renderer (session-fixation: victim's
    // localStorage gets attacker's token, victim's config-sync uploads land
    // on attacker's catalog account).
    const normalizedHost = u.hostname.toLowerCase()
    const normalizedPath = u.pathname === '' ? '/' : u.pathname
    if (normalizedHost !== 'auth-callback' || normalizedPath !== '/') {
      console.warn('[sso] host/path mismatch — ignoring (host=%s path=%s)',
        normalizedHost, normalizedPath)
      return
    }
    const token = u.searchParams.get('token') ?? ''
    const error = u.searchParams.get('error') ?? ''
    console.log('[sso] extracted token.length=%d error=%s hasWindow=%s',
      token.length, error || '—', Boolean(win))
    if (!win) {
      console.warn('[sso] no main window yet — dropping payload')
      return
    }
    win.webContents.send('sso-callback', { token, error })
    if (win.isMinimized()) win.restore()
    win.focus()
  } catch (e) {
    console.warn('[sso] URL parse failed:', e instanceof Error ? e.message : String(e))
  }
}

app.on('second-instance', (_evt, argv) => {
  // Don't log argv directly — on Windows the OS appends the full
  // `wdc://auth-callback?token=...` URL as an argv element, which means
  // the bare token would land in main.log. Log only argv length + flag
  // whether a wdc:// element was present.
  const deepLink = argv.find(a => a.startsWith('wdc://'))
  console.log('[sso] second-instance argv (len=%d, wdc=%s)', argv.length, Boolean(deepLink))
  if (deepLink) forwardSsoCallback(deepLink)
  if (win) {
    if (win.isMinimized()) win.restore()
    win.focus()
  }
})

// macOS delivers custom-scheme launches via 'open-url'; Windows/Linux
// use process argv and second-instance. Both feed the same forwarder.
app.on('open-url', (evt, url) => {
  evt.preventDefault()
  forwardSsoCallback(url)
})

if (!app.isPackaged) {
  app.commandLine.appendSwitch('remote-debugging-port', '9222')
}

// F79 + F72 IPC surface — exposed to the renderer via preload's
// contextBridge 'electronAPI'. Registered BEFORE whenReady runs handlers
// so any renderer call that lands while the window is still mounting
// doesn't miss the handler. Keep the list minimal; each handler is a
// capability the renderer is explicitly trusted to use.
ipcMain.handle('open-external', async (_evt, url: string) => {
  if (typeof url !== 'string') return false
  // Allowlist scheme to safe ones only. `file:` is explicitly EXCLUDED
  // here even though shell.openExternal accepts it: on Windows
  // shell.openExternal('file:///C:/Windows/System32/calc.exe') hands the
  // path to ShellExecute which honours .exe/.lnk/.url/.bat/.scr/.msi
  // associations and launches the binary with no prompt. UNC paths like
  // file://attacker.tld/share/payload.lnk also work. A renderer XSS that
  // reaches this IPC channel would otherwise have one-call RCE.
  // For "open in folder" use the dedicated revealInFolder handler.
  const allowed = /^(https?|mailto):/i
  if (!allowed.test(url)) return false
  await shell.openExternal(url)
  return true
})

ipcMain.handle('show-open-dialog', async (_evt, options: Electron.OpenDialogOptions | undefined) => {
  const safeOptions: Electron.OpenDialogOptions = {
    properties: options?.properties ?? ['openDirectory'],
    title: options?.title,
    defaultPath: options?.defaultPath,
    buttonLabel: options?.buttonLabel,
  }
  const result = await dialog.showOpenDialog(safeOptions)
  return { canceled: result.canceled, filePaths: result.filePaths }
})

// Reveal a file or directory in the user's native file manager (Explorer
// / Finder / Nautilus). Renderer calls this to open the "Show in folder"
// action from the sites list. Previously the renderer checked for
// `electronAPI.revealInFolder` which was never exposed, so the else
// branch `window.open('file://…')` always ran — which silently fails in
// packaged Electron because of the sandboxed renderer's file: policy.
ipcMain.handle('reveal-in-folder', async (_evt, targetPath: string) => {
  if (typeof targetPath !== 'string' || targetPath.length === 0) return false
  try {
    shell.showItemInFolder(targetPath)
    return true
  } catch {
    return false
  }
})

// Hard-reload the renderer from the main process. `window.location.reload()`
// called inside the renderer under the `app://` scheme was leaving Pinia
// stores intact — users clicked Factory Reset, daemon wiped the DB, toast
// flashed, but the UI kept the stale sites/settings/etc. webContents
// reload bypasses the cached module state and gives the component tree
// a fresh boot, which is what the user expects from a "reset + restart"
// action.
// Renderer → main log bridge. The renderer has its own electron-log
// instance writing to ~/.wdc/logs/electron/renderer.log, but for
// "user emailed screenshot of a Vue error" flows we want every
// frontend-side log line in the SAME main.log timeline too. Renderer
// posts `{ level, args }` via this channel; we fan it into the main
// logger at the matching level. Dropped messages fail-closed (return
// false) so the renderer doesn't block on unresponsive main.
// Cap on the serialized payload size — renderer XSS could otherwise dump
// the entire localStorage (including the daemon JWT) into main.log via a
// single rendererLog() call. 4 KiB is enough for a normal stack trace +
// a few breadcrumb objects but cuts off attacker dumps long before any
// useful credential material lands on disk.
const RENDERER_LOG_MAX_BYTES = 4096
// Substrings whose presence in a log argument signals likely credential
// material. Matched against each argument's JSON-stringified form; if a
// match hits we drop that argument entirely and replace with a redaction
// marker so the surrounding context still lands in the log.
const RENDERER_LOG_REDACT_PATTERNS = [
  /authorization\s*[:=]/i,
  /\btoken\s*[=:]/i,
  /bearer\s+[A-Za-z0-9._\-+/=]{16,}/i,
  /\bjwt\b/i,
  /password\s*[:=]/i,
]

function safeRendererLogArg(arg: unknown): unknown {
  let serialized: string
  try {
    serialized = typeof arg === 'string' ? arg : JSON.stringify(arg)
  } catch {
    return '[unserializable]'
  }
  if (serialized.length > RENDERER_LOG_MAX_BYTES) {
    return `${serialized.slice(0, RENDERER_LOG_MAX_BYTES)}…[truncated ${serialized.length - RENDERER_LOG_MAX_BYTES}B]`
  }
  for (const pat of RENDERER_LOG_REDACT_PATTERNS) {
    if (pat.test(serialized)) return '[redacted: contained credential-like pattern]'
  }
  return arg
}

ipcMain.handle('renderer-log', (_evt, payload: { level?: string; args?: unknown[] }) => {
  try {
    const level = (payload?.level ?? 'info').toLowerCase()
    const rawArgs = Array.isArray(payload?.args) ? payload.args : []
    const args = rawArgs.map(safeRendererLogArg)
    const fn = (log as unknown as Record<string, (...a: unknown[]) => void>)[level] ?? log.info
    fn('[renderer]', ...args)
    return true
  } catch {
    return false
  }
})

// #147 — cross-platform OS notifications via Electron's Notification class.
// Uses native Win10/11 toast on Windows, NSUserNotificationCenter on macOS,
// libnotify on Linux. Renderer hands us {title, body, urgency, channel} —
// we map urgency to the platform-relevant flag (silent=info, normal=success,
// critical=error). The optional `channel` is recorded for telemetry only;
// platform notification grouping happens via the AppUserModelID on Windows
// and the bundle id on macOS, both already set elsewhere.
ipcMain.handle('os-notify', (_evt, payload: {
  title?: string
  body?: string
  urgency?: 'low' | 'normal' | 'critical'
  silent?: boolean
  channel?: string
}) => {
  try {
    if (!Notification.isSupported()) {
      log.warn('[os-notify] not supported on this platform — skipping', { channel: payload?.channel })
      return false
    }
    const n = new Notification({
      title: (payload?.title ?? 'NKS WebDev Console').slice(0, 200),
      body: (payload?.body ?? '').slice(0, 1000),
      silent: payload?.silent ?? (payload?.urgency === 'low'),
      urgency: payload?.urgency ?? 'normal',
    })
    // Clicking a notification should bring the main window forward so
    // the operator can act on what triggered it. Best-effort — if no
    // window exists (rare), the notification still fires fine.
    n.on('click', () => {
      const w = BrowserWindow.getAllWindows()[0]
      if (w) {
        if (w.isMinimized()) w.restore()
        w.show()
        w.focus()
      }
    })
    n.show()
    log.info('[os-notify] fired', { channel: payload?.channel, urgency: payload?.urgency })
    return true
  } catch (err) {
    log.error('[os-notify] failed:', err)
    return false
  }
})

ipcMain.handle('restart-renderer', async (_evt) => {
  try {
    const w = BrowserWindow.getFocusedWindow() ?? BrowserWindow.getAllWindows()[0]
    if (!w) return false
    // reloadIgnoringCache so the asar-packed index.html + bundled JS
    // definitely re-evaluates (shouldn't ever be cached from a local
    // protocol, but guarantees a fresh module graph just in case).
    w.webContents.reloadIgnoringCache()
    return true
  } catch (err) {
    console.error('[restart-renderer] failed:', err)
    return false
  }
})

app.whenReady().then(async () => {
  // Banner: one line with every state a support ticket asks for.
  // Landing at INFO level so it's always in main.log regardless of
  // level filtering — makes "which version did they run" + "where
  // does its logs live" answerable without further back-and-forth.
  log.info(
    `[startup] version=${app.getVersion()} ` +
    `electron=${process.versions.electron} ` +
    `chrome=${process.versions.chrome} ` +
    `node=${process.versions.node} ` +
    `os=${process.platform}/${process.arch} ` +
    `packaged=${app.isPackaged} ` +
    `portable=${isPortable} ` +
    `logDir=${wdcLogsDir}`
  )
  protocol.handle('app', (request) => {
    const url = new URL(request.url)
    const relPath = url.pathname.startsWith('/') ? url.pathname.slice(1) : url.pathname
    const safePath = relPath === '' ? 'index.html' : relPath
    // F59 defense-in-depth: reject any path that traverses outside the
    // bundled renderer dir via `..` segments or an absolute root. Renderer
    // URLs are emitted by our own router today, but a single stray
    // navigation (third-party embed, typo'd href) would otherwise be
    // allowed to read arbitrary files off disk through app://.
    const rendererRoot = join(__dirname, 'renderer')
    const fullPath = join(rendererRoot, safePath)
    const resolved = resolve(fullPath)
    if (!resolved.startsWith(resolve(rendererRoot) + sep) && resolved !== resolve(rendererRoot)) {
      return new Response('forbidden', { status: 403 })
    }
    return net.fetch(pathToFileURL(resolved).toString())
  })
  // Catalog source is the public NKS catalog at https://wdc.nks-hub.cz
  // (SettingsStore default). No local sidecar — daemon fetches directly.
  await spawnDaemon()
  createWindow()
  createTray()
  await setupAutoUpdater()
})

app.on('window-all-closed', () => { /* keep running — hide to tray on close */ })
