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
      const req = http.get(`http://localhost:${info.port}/api/status`, (res) => {
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
    daemon = spawn('dotnet', ['run', '--project', projectDir, '--', '--urls', 'http://localhost:5199'], {
      stdio: 'pipe',
      detached: false
    })
  } else {
    daemon = spawn(DAEMON_EXE, [], { stdio: 'pipe', detached: false })
  }

  daemon.stdout?.on('data', (d) => console.log('[daemon]', d.toString().trim()))
  daemon.stderr?.on('data', (d) => console.error('[daemon err]', d.toString().trim()))
  daemon.on('exit', (code) => {
    console.log(`[daemon] exited code=${code}`)
    daemonConnected = false
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

function createWindow() {
  win = new BrowserWindow({
    width: 900,
    height: 600,
    backgroundColor: '#1a1a2e',
    show: false,
    webPreferences: {
      preload: join(__dirname, 'preload.js'),
      contextIsolation: true
    }
  })

  if (process.env.ELECTRON_RENDERER_URL) {
    win.loadURL(process.env.ELECTRON_RENDERER_URL)
  } else {
    win.loadFile(join(__dirname, '../renderer/index.html'))
  }

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
  const label = daemonConnected ? 'NKS WebDev Console (connected)' : 'NKS WebDev Console (disconnected)'
  tray.setToolTip('NKS WebDev Console')

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

function createTray() {
  // Create a 16x16 green square as placeholder tray icon (RGBA buffer)
  const size = 16
  const buf = Buffer.alloc(size * size * 4)
  for (let i = 0; i < size * size; i++) {
    buf[i * 4 + 0] = 0x22 // R
    buf[i * 4 + 1] = 0xc5 // G
    buf[i * 4 + 2] = 0x5e // B
    buf[i * 4 + 3] = 0xff // A
  }
  const icon = nativeImage.createFromBuffer(buf, { width: size, height: size })
  tray = new Tray(icon.resize({ width: 16, height: 16 }))
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
})

app.on('window-all-closed', () => { /* keep running — hide to tray on close */ })
