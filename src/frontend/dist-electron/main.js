"use strict";
const electron = require("electron");
const path = require("path");
const child_process = require("child_process");
const fs = require("fs");
const os = require("os");
let win = null;
let tray = null;
let daemon = null;
let daemonConnected = false;
let isQuitting = false;
const PORT_FILE = path.join(os.tmpdir(), "nks-wdc-daemon.port");
const DAEMON_EXE = path.join(__dirname, "../../daemon/bin/wdc-daemon.exe");
const DAEMON_DEV = path.join(__dirname, "../../daemon");
function spawnDaemon() {
  const isDev = !electron.app.isPackaged;
  if (isDev) {
    daemon = child_process.spawn("dotnet", ["run"], {
      cwd: DAEMON_DEV,
      stdio: "pipe",
      detached: false
    });
  } else {
    daemon = child_process.spawn(DAEMON_EXE, [], { stdio: "pipe", detached: false });
  }
  daemon.stdout?.on("data", (d) => console.log("[daemon]", d.toString().trim()));
  daemon.stderr?.on("data", (d) => console.error("[daemon err]", d.toString().trim()));
  daemon.on("exit", (code) => {
    console.log(`[daemon] exited code=${code}`);
    daemonConnected = false;
    updateTray();
  });
  let attempts = 0;
  const check = setInterval(() => {
    if (fs.existsSync(PORT_FILE)) {
      daemonConnected = true;
      updateTray();
      clearInterval(check);
    }
    if (++attempts > 30) clearInterval(check);
  }, 500);
}
function createWindow() {
  win = new electron.BrowserWindow({
    width: 900,
    height: 600,
    backgroundColor: "#1a1a2e",
    show: false,
    webPreferences: {
      preload: path.join(__dirname, "../preload/index.js"),
      contextIsolation: true
    }
  });
  if (process.env.ELECTRON_RENDERER_URL) {
    win.loadURL(process.env.ELECTRON_RENDERER_URL);
  } else {
    win.loadFile(path.join(__dirname, "../renderer/index.html"));
  }
  win.once("ready-to-show", () => win?.show());
  win.on("close", (e) => {
    if (!isQuitting) {
      e.preventDefault();
      win?.hide();
    }
  });
}
function updateTray() {
  if (!tray) return;
  const label = daemonConnected ? "NKS WebDev Console (connected)" : "NKS WebDev Console (disconnected)";
  tray.setToolTip("NKS WebDev Console");
  const menu = electron.Menu.buildFromTemplate([
    { label, enabled: false },
    { type: "separator" },
    {
      label: win?.isVisible() ? "Hide Window" : "Show Window",
      click: () => {
        if (win?.isVisible()) {
          win.hide();
        } else {
          win?.show();
          win?.focus();
        }
        updateTray();
      }
    },
    { type: "separator" },
    {
      label: "Services",
      submenu: [
        { label: "Apache", enabled: false },
        { label: "MySQL", enabled: false },
        { label: "PHP", enabled: false },
        { type: "separator" },
        { label: "Manage...", click: () => {
          win?.show();
          win?.focus();
        } }
      ]
    },
    { type: "separator" },
    {
      label: "Quit",
      click: () => {
        isQuitting = true;
        daemon?.kill();
        electron.app.quit();
      }
    }
  ]);
  tray.setContextMenu(menu);
}
function createTray() {
  const size = 16;
  const buf = Buffer.alloc(size * size * 4);
  for (let i = 0; i < size * size; i++) {
    buf[i * 4 + 0] = 34;
    buf[i * 4 + 1] = 197;
    buf[i * 4 + 2] = 94;
    buf[i * 4 + 3] = 255;
  }
  const icon = electron.nativeImage.createFromBuffer(buf, { width: size, height: size });
  tray = new electron.Tray(icon.resize({ width: 16, height: 16 }));
  updateTray();
  tray.on("click", () => {
    if (win?.isVisible()) {
      win.hide();
    } else {
      win?.show();
      win?.focus();
    }
    updateTray();
  });
}
electron.app.on("before-quit", () => {
  isQuitting = true;
});
electron.app.whenReady().then(() => {
  spawnDaemon();
  createWindow();
  createTray();
});
electron.app.on("window-all-closed", () => {
});
