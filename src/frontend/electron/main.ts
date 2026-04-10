import { app, BrowserWindow, Tray, Menu, nativeImage, shell } from 'electron'
import { join } from 'path'
import { spawn, ChildProcess } from 'child_process'
import { readFileSync, existsSync } from 'fs'
import { tmpdir } from 'os'

let win: BrowserWindow | null = null
let tray: Tray | null = null
let daemon: ChildProcess | null = null
let daemonConnected = false

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

  // Hide to tray on close
  win.on('close', (e) => {
    e.preventDefault()
    win?.hide()
  })
}

function updateTray() {
  const icon = nativeImage.createEmpty()
  if (!tray) return
  const label = daemonConnected ? 'NKS WebDev Console (connected)' : 'NKS WebDev Console (disconnected)'
  tray.setToolTip(label)
  const menu = Menu.buildFromTemplate([
    { label, enabled: false },
    { type: 'separator' },
    {
      label: 'Show / Hide',
      click: () => (win?.isVisible() ? win.hide() : win?.show())
    },
    { type: 'separator' },
    {
      label: 'Quit',
      click: () => {
        win?.removeAllListeners('close')
        daemon?.kill()
        app.quit()
      }
    }
  ])
  tray.setContextMenu(menu)
}

function createTray() {
  // Inline 16x16 white circle as base64 PNG (no external file needed)
  const iconData = nativeImage.createFromDataURL(
    'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAYAAAAf8/9hAAAA' +
    'BHNCSVQICAgIfAhkiAAAAAlwSFlzAAALEwAACxMBAJqcGAAAABl0RVh0U29mdHdhcmUAd3d3' +
    'Lmlua3NjYXBlLm9yZ5vuPBoAAADLSURBVDiNpdOxCsIwEAbgL3YJdHMRJ9/FxUVw8Al8AUF' +
    'wcHBwcHBwEMHBQRAcHBxEEMHBQRAcHBxEEMHBQRAcHBxEEMHBQRAcHBxEEMHBQRAcHBxEEM' +
    'HBQRAcHBxEEMHBQRAcHBxEEMHBQRAcHBxEEMHBQRAcHBxEEMHBQRAcHBxEEMHBQRAcHBxE' +
    'EMHBQRAcHBxEEMHBQRAcHBxEEMHBQRAcHBxEEMHBQRAcHBxEEMHBQRAcHBxEEMHBQRAcHBx' +
    'EEMHBQRAcHBxEEA=='
  )
  tray = new Tray(iconData)
  updateTray()
  tray.on('click', () => (win?.isVisible() ? win.hide() : win?.show()))
}

app.whenReady().then(() => {
  spawnDaemon()
  createWindow()
  createTray()
})

app.on('window-all-closed', () => { /* keep running — hide to tray on close */ })
