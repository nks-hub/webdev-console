import { app, BrowserWindow, Tray, Menu, nativeImage, shell, dialog } from 'electron'
import { join } from 'path'
import { spawn, ChildProcess } from 'child_process'
import { readFileSync, writeFileSync, existsSync } from 'fs'
import { tmpdir } from 'os'
import http from 'http'

let win: BrowserWindow | null = null
let tray: Tray | null = null
let daemon: ChildProcess | null = null
let daemonConnected = false
let isQuitting = false
let updaterStatus = 'Idle'
let updateDownloaded = false
let checkingForUpdates = false

// Portable mode: if portable.txt exists next to the app, redirect both
// Electron's userData AND the C# daemon's ~/.wdc tree to a local subfolder
// so a USB-stick install leaves no trace on the host machine.
// The daemon honors the WDC_DATA_DIR env var via Core/Services/WdcPaths.cs,
// which every service (SiteManager, BackupManager, TelemetryConsent,
// PluginState, PhpExtensionOverrides, BinaryManager, all plugins) consults.
const isPortable = existsSync(join(app.getAppPath(), 'portable.txt'))
  || existsSync(join(process.cwd(), 'portable.txt'))

let portableWdcDir: string | null = null
if (isPortable) {
  // Store all user data next to the app binary instead of %APPDATA%
  const portableDir = join(app.getAppPath(), 'data')
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
const DAEMON_EXE = join(__dirname, '../../daemon/bin/wdc-daemon.exe')

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
    // /api/status requires auth — the mere fact that we get a response (even 401) means the daemon is up
    await new Promise<void>((resolve, reject) => {
      const req = http.get(`http://localhost:${info.port}/healthz`, (res) => {
        res.on('data', () => {})
        res.on('end', () => resolve())
      })
      req.on('error', reject)
      req.setTimeout(2000, () => { req.destroy(); reject(new Error('timeout')) })
    })
    return true
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
  // Inherit everything else from the Electron process env.
  const daemonEnv: NodeJS.ProcessEnv = { ...process.env }
  if (portableWdcDir) daemonEnv.WDC_DATA_DIR = portableWdcDir

  if (isDev) {
    const projectDir = findDaemonProject()
    console.log('[daemon] starting from:', projectDir)
    daemon = spawn('dotnet', ['run', '--project', projectDir], {
      stdio: 'pipe',
      detached: false,
      env: daemonEnv,
    })
  } else {
    daemon = spawn(DAEMON_EXE, [], { stdio: 'pipe', detached: false, env: daemonEnv })
  }

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
    // Production: load from dist, pass params via hash query
    const indexPath = join(__dirname, '../renderer/index.html')
    const params = new URLSearchParams()
    if (info?.port) params.set('port', String(info.port))
    if (info?.token) params.set('token', info.token)
    win.loadFile(indexPath, { query: Object.fromEntries(params) })
  }

  // Remove default menu bar (File/Edit/View/Window/Help)
  win.setMenuBarVisibility(false)
  win.setAutoHideMenuBar(true)

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
  if (!tray) return

  // Dynamic icon color based on state
  let iconColor: 'green' | 'red' | 'yellow' | 'gray' = 'gray'
  if (daemonConnected) {
    try {
      const services = await daemonGet<any[]>('/api/services')
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
                const { autoUpdater } = await import('electron-updater')
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
      click: () => {
        isQuitting = true
        daemon?.kill()
        app.quit()
      }
    }
  ])
  tray.setContextMenu(menu)
}

function createTrayIcon(color: 'green' | 'red' | 'yellow' | 'gray'): Electron.NativeImage {
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
  updateTray()
  tray.on('click', () => {
    if (win?.isVisible()) {
      win.hide()
    } else {
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
    const { autoUpdater } = await import('electron-updater')
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
    const { autoUpdater } = await import('electron-updater')
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

app.on('before-quit', () => {
  isQuitting = true
})

// Enable Chrome DevTools Protocol in dev mode so tooling/tests can read renderer console
if (!app.isPackaged) {
  app.commandLine.appendSwitch('remote-debugging-port', '9222')
}

app.whenReady().then(async () => {
  await spawnDaemon()
  createWindow()
  createTray()
  await setupAutoUpdater()
})

app.on('window-all-closed', () => { /* keep running — hide to tray on close */ })
