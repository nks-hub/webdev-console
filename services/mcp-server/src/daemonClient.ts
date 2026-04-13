// Discovery + auth + HTTP plumbing for the NKS WDC daemon.
//
// Reads the same `~/.wdc/daemon.port` file the wdc CLI uses: one line port,
// next line the per-session Bearer token. Token rotates every daemon restart
// — on 401 we re-read the file and retry once.

import { existsSync, readFileSync } from 'node:fs'
import { homedir } from 'node:os'
import { join } from 'node:path'
import { tmpdir } from 'node:os'

interface PortFile {
  port: number
  token: string
}

class DaemonNotRunningError extends Error {
  constructor() {
    super(
      'NKS WDC daemon is not running. Start the WDC GUI or run `wdc daemon start`. ' +
        'The MCP server reads ~/.wdc/daemon.port to discover the local daemon.',
    )
  }
}

class DaemonClient {
  private current: PortFile | null = null

  /**
   * Locate the daemon's port + token. The wdc daemon writes this file to
   * the OS temp directory on every startup so multiple concurrent users
   * don't collide on a shared `~/.wdc` location.
   */
  private readPortFile(): PortFile | null {
    // Order matches what wdc CLI checks: first the OS temp path used by
    // the daemon's Lifetime callback, then the legacy ~/.wdc location.
    const candidates = [
      join(tmpdir(), 'nks-wdc-daemon.port'),
      join(homedir(), '.wdc', 'daemon.port'),
    ]
    for (const path of candidates) {
      if (!existsSync(path)) continue
      try {
        const lines = readFileSync(path, 'utf8').trim().split('\n')
        if (lines.length < 2) continue
        const port = parseInt(lines[0]!, 10)
        const token = lines[1]!.trim()
        if (!Number.isFinite(port) || port <= 0 || !token) continue
        return { port, token }
      } catch {
        continue
      }
    }
    return null
  }

  private ensureConnection(): PortFile {
    if (this.current) return this.current
    const file = this.readPortFile()
    if (!file) throw new DaemonNotRunningError()
    this.current = file
    return file
  }

  /**
   * Send a request to the daemon, refreshing the port file once on 401
   * (token rotated due to daemon restart between calls).
   */
  async request(
    method: string,
    path: string,
    body?: unknown,
  ): Promise<unknown> {
    const conn = this.ensureConnection()
    const url = `http://127.0.0.1:${conn.port}${path}`
    const init: RequestInit = {
      method,
      headers: {
        'Content-Type': 'application/json',
        Authorization: `Bearer ${conn.token}`,
      },
      body: body !== undefined ? JSON.stringify(body) : undefined,
    }
    let res: Response
    try {
      res = await fetch(url, init)
    } catch (err: any) {
      // Connection refused — daemon stopped between calls.
      this.current = null
      throw new DaemonNotRunningError()
    }
    if (res.status === 401) {
      // Token rotated. Re-read port file once and retry.
      this.current = null
      const fresh = this.ensureConnection()
      const retryInit: RequestInit = {
        method,
        headers: {
          'Content-Type': 'application/json',
          Authorization: `Bearer ${fresh.token}`,
        },
        body: body !== undefined ? JSON.stringify(body) : undefined,
      }
      res = await fetch(`http://127.0.0.1:${fresh.port}${path}`, retryInit)
    }
    if (!res.ok) {
      // Surface the daemon's structured error body. All daemon endpoints
      // return JSON with `error` or `detail` per the recent robustness sweep.
      const text = await res.text().catch(() => '')
      let detail = `HTTP ${res.status}`
      if (text) {
        try {
          const obj = JSON.parse(text)
          detail = String(obj.error ?? obj.detail ?? obj.message ?? text)
        } catch {
          detail = text.length > 300 ? text.slice(0, 300) + '…' : text
        }
      }
      throw new Error(`Daemon request failed: ${res.status} ${detail}`)
    }
    if (res.status === 204) return null
    const ct = res.headers.get('content-type') ?? ''
    if (ct.includes('application/json')) return res.json()
    return res.text()
  }

  get<T = unknown>(path: string): Promise<T> {
    return this.request('GET', path) as Promise<T>
  }
  post<T = unknown>(path: string, body?: unknown): Promise<T> {
    return this.request('POST', path, body) as Promise<T>
  }
  put<T = unknown>(path: string, body?: unknown): Promise<T> {
    return this.request('PUT', path, body) as Promise<T>
  }
  delete<T = unknown>(path: string): Promise<T> {
    return this.request('DELETE', path) as Promise<T>
  }
}

export const daemonClient = new DaemonClient()
export { DaemonNotRunningError }
