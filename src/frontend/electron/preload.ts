import { contextBridge, ipcRenderer } from 'electron'
import { readFileSync, existsSync, statSync } from 'fs'
import { join } from 'path'
import { tmpdir } from 'os'

const portFilePath = join(tmpdir(), 'nks-wdc-daemon.port')

let cachedPort = 0
let cachedToken = ''
let lastMtimeMs = 0

/**
 * Reads the port file and updates the cache — but only if the file's mtime
 * changed since the last read. This makes getPort()/getToken() effectively
 * live-refreshing: when the daemon restarts it rewrites the port file with a
 * new token, preload picks up the change on the next call without any manual
 * reload. Previous implementation cached once at startup and never refreshed,
 * which left the renderer with a stale token after every daemon restart.
 */
// Base64 token shape check — the daemon emits a 32-byte random token encoded
// as base64 (44 chars ending in =). We validate the shape before caching so
// a corrupted or hand-edited port file can't inject arbitrary strings into
// the renderer's Authorization header.
const TOKEN_SHAPE = /^[A-Za-z0-9+/=]{16,512}$/

function refreshFromPortFile(): void {
  try {
    if (!existsSync(portFilePath)) return
    const st = statSync(portFilePath)
    const mtime = st.mtimeMs
    if (mtime === lastMtimeMs && cachedPort > 0) return // unchanged
    const lines = readFileSync(portFilePath, 'utf-8').split('\n').filter(Boolean)
    if (lines.length >= 2) {
      const p = parseInt(lines[0], 10)
      // Port must be a valid 16-bit TCP port (1..65535); anything outside
      // is a corrupted file and must not be trusted.
      if (!isNaN(p) && p > 0 && p <= 65535) cachedPort = p
      // Token must match the base64 shape we expect; reject anything else
      // so a tampered file can't smuggle spaces, control chars, or CRLF
      // injection into the Authorization header the renderer later uses.
      const candidate = lines[1].trim()
      if (TOKEN_SHAPE.test(candidate)) {
        cachedToken = candidate
      }
      lastMtimeMs = mtime
    }
  } catch {
    // ignore transient read errors
  }
}

// Initial read so the first getPort() call has a value even before any change
refreshFromPortFile()

contextBridge.exposeInMainWorld('daemonApi', {
  getPort: () => {
    refreshFromPortFile()
    return cachedPort
  },
  getToken: () => {
    refreshFromPortFile()
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

  // Show a native file/directory picker. Returns { canceled, filePaths }.
  // Pass { properties: ['openDirectory'] } for folder picker (F79 path
  // inputs in Settings/Cesty), ['openFile'] for a single file, etc.
  showOpenDialog: (options?: Electron.OpenDialogOptions) =>
    ipcRenderer.invoke('show-open-dialog', options),
})
