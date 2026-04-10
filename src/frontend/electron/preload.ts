import { contextBridge } from 'electron'
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
function refreshFromPortFile(): void {
  try {
    if (!existsSync(portFilePath)) return
    const st = statSync(portFilePath)
    const mtime = st.mtimeMs
    if (mtime === lastMtimeMs && cachedPort > 0) return // unchanged
    const lines = readFileSync(portFilePath, 'utf-8').split('\n').filter(Boolean)
    if (lines.length >= 2) {
      const p = parseInt(lines[0], 10)
      if (!isNaN(p) && p > 0) cachedPort = p
      cachedToken = lines[1]
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
