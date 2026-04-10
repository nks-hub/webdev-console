import { contextBridge } from 'electron'
import { readFileSync, existsSync } from 'fs'
import { join } from 'path'
import { tmpdir } from 'os'

const portFilePath = join(tmpdir(), 'nks-wdc-daemon.port')

let cachedPort = 5199
let cachedToken = ''

function readPortFile(): boolean {
  try {
    if (!existsSync(portFilePath)) return false
    const lines = readFileSync(portFilePath, 'utf-8').split('\n').filter(Boolean)
    if (lines.length >= 2) {
      cachedPort = parseInt(lines[0], 10)
      cachedToken = lines[1]
      return true
    }
  } catch {}
  return false
}

// Try immediately
readPortFile()

// Poll every 500ms for up to 30s if not found
if (!cachedToken) {
  let attempts = 0
  const interval = setInterval(() => {
    if (readPortFile() || attempts++ > 60) clearInterval(interval)
  }, 500)
}

contextBridge.exposeInMainWorld('daemonApi', {
  getPort: () => cachedPort,
  getToken: () => cachedToken,
})
