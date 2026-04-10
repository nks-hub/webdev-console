import { contextBridge } from 'electron'
import { readFileSync, existsSync } from 'fs'
import { join } from 'path'
import { tmpdir } from 'os'

contextBridge.exposeInMainWorld('daemonApi', {
  getPort: (): number => {
    const f = join(tmpdir(), 'devforge-daemon.port')
    if (existsSync(f)) return parseInt(readFileSync(f, 'utf8').trim(), 10)
    return 50051
  }
})
