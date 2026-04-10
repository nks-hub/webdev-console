import { app, BrowserWindow, Tray, Menu, nativeImage, shell } from 'electron'
import { join } from 'path'
import { spawn, ChildProcess } from 'child_process'
import { readFileSync, existsSync } from 'fs'
import { tmpdir } from 'os'

let win: BrowserWindow | null = null
let tray: Tray | null = null
let daemon: ChildProcess | null = null
let daemonConnected = false
let isQuitting = false

const PORT_FILE = join(tmpdir(), 'nks-wdc-daemon.port')
const DAEMON_EXE = join(__dirname, '../../daemon/bin/wdc-daemon.exe')
const DAEMON_DEV = join(__dirname, '../../daemon')

function spawnDaemon() {
  // In dev: use dotnet run. In prod: use compiled exe.
  const isDev = !app.isPackaged

  if (isDev) {
    daemon = spawn('dotnet', ['run'], {
      cwd: DAEMON_DEV,
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
      preload: join(__dirname, '../preload/index.js'),
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

function updateTray() {
  if (!tray) return
  const label = daemonConnected ? 'NKS WebDev Console (connected)' : 'NKS WebDev Console (disconnected)'
  tray.setToolTip('NKS WebDev Console')
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
      submenu: [
        { label: 'Apache', enabled: false },
        { label: 'MySQL', enabled: false },
        { label: 'PHP', enabled: false },
        { type: 'separator' },
        { label: 'Manage...', click: () => { win?.show(); win?.focus() } }
      ]
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

app.whenReady().then(() => {
  spawnDaemon()
  createWindow()
  createTray()
})

app.on('window-all-closed', () => { /* keep running — hide to tray on close */ })
