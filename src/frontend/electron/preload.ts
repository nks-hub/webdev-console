import { contextBridge } from 'electron'
import { readFileSync, existsSync } from 'fs'
import { join } from 'path'
import { tmpdir } from 'os'

// Read both lines from port file: line 1 = port, line 2 = bearer token
const portFile = join(tmpdir(), 'nks-wdc-daemon.port')
let port = 50051
let token = ''

if (existsSync(portFile)) {
  const lines = readFileSync(portFile, 'utf-8').split('\n').filter(Boolean)
  port = parseInt(lines[0], 10) || 50051
  token = lines[1] || ''
}

contextBridge.exposeInMainWorld('daemonApi', {
  getPort: () => port,
  getToken: () => token,
})
