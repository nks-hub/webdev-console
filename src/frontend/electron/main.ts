import { app, BrowserWindow, Tray, Menu, nativeImage, shell } from 'electron'
import { join } from 'path'
import { spawn, ChildProcess } from 'child_process'
import { readFileSync, existsSync } from 'fs'
import { tmpdir } from 'os'
import http from 'http'

let win: BrowserWindow | null = null
let tray: Tray | null = null
let daemon: ChildProcess | null = null
let daemonConnected = false
let isQuitting = false

// Portable mode: if portable.txt exists next to the app, use local data dir
const isPortable = existsSync(join(app.getAppPath(), 'portable.txt'))
  || existsSync(join(process.cwd(), 'portable.txt'))

if (isPortable) {
  // Store all user data next to the app binary instead of %APPDATA%
  const portableDir = join(app.getAppPath(), 'data')
  app.setPath('userData', portableDir)
  console.log('[portable] mode enabled, data dir:', portableDir)
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

  if (isDev) {
    const projectDir = findDaemonProject()
    console.log('[daemon] starting from:', projectDir)
    daemon = spawn('dotnet', ['run', '--project', projectDir], {
      stdio: 'pipe',
      detached: false
    })
  } else {
    daemon = spawn(DAEMON_EXE, [], { stdio: 'pipe', detached: false })
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

async function createWindow() {
  win = new BrowserWindow({
    width: 960,
    height: 640,
    backgroundColor: '#141620',
    show: false,
    webPreferences: {
      preload: join(__dirname, 'preload.js'),
      contextIsolation: true,
      sandbox: false
    }
  })

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

app.on('before-quit', () => {
  isQuitting = true
})

app.whenReady().then(async () => {
  await spawnDaemon()
  createWindow()
  createTray()

  // Auto-updater: check for updates when packaged (not in dev)
  if (app.isPackaged && !isPortable) {
    try {
      const { autoUpdater } = await import('electron-updater')
      autoUpdater.checkForUpdatesAndNotify()
      autoUpdater.on('update-available', () => console.log('[updater] Update available'))
      autoUpdater.on('update-downloaded', () => console.log('[updater] Update downloaded — will install on quit'))
    } catch {
      // electron-updater not installed — skip
    }
  }
})

app.on('window-all-closed', () => { /* keep running — hide to tray on close */ })
