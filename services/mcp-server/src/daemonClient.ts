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
  /**
   * Optional list of scopes the daemon granted to this token (e.g. plain
   * `*` for legacy / no-restriction tokens, or any subset of
   * `deploy:read`, `deploy:write`, `deploy:admin`). Read from an optional
   * `scope:` prefixed third line of the port file. The MCP server uses
   * this to decide which tools to register.
   *
   * Default when the line is absent (or contains only `*`): legacy
   * behaviour — all tools register, mirroring how the MCP server worked
   * before scopes existed. Per v2 audit fix the destructive deploy tools
   * (deploy / rollback / cancel) ALSO check `MCP_DEPLOY_AUTO_APPROVE` env
   * var before firing, so absent scope is not the only gate.
   */
  scopes: string[]
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
        // Optional third line "scope:deploy:read,deploy:write,..." — when
        // absent we assume legacy "no restriction" (= ['*']) so existing
        // daemons that don't write the scope line keep working.
        let scopes: string[] = ['*']
        if (lines.length >= 3) {
          const m = lines[2]!.match(/^scope:(.+)$/i)
          if (m) {
            scopes = m[1]!.split(',').map(s => s.trim()).filter(Boolean)
          }
        }
        return { port, token, scopes }
      } catch {
        continue
      }
    }
    return null
  }

  /** Returns the scope list from the current port file (or `['*']` if absent). */
  scopes(): string[] {
    return this.current?.scopes ?? ['*']
  }

  /** True iff the granted scope list includes the given one (or `*`). */
  hasScope(scope: string): boolean {
    const granted = this.scopes()
    return granted.includes('*') || granted.includes(scope)
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
  private buildInit(
    method: string,
    token: string,
    body?: unknown,
    extraHeaders?: Record<string, string>,
  ): RequestInit {
    // Only advertise application/json when a body is actually present.
    // Bodyless POSTs (start-service, install-ca, create-backup, etc.)
    // should not carry a Content-Type header — some HTTP middleware
    // rejects empty-body requests that claim to be JSON.
    const headers: Record<string, string> = {
      Authorization: `Bearer ${token}`,
    }
    if (body !== undefined) headers['Content-Type'] = 'application/json'
    if (extraHeaders) Object.assign(headers, extraHeaders)
    return {
      method,
      headers,
      body: body !== undefined ? JSON.stringify(body) : undefined,
    }
  }

  async request(
    method: string,
    path: string,
    body?: unknown,
    extraHeaders?: Record<string, string>,
  ): Promise<unknown> {
    const conn = this.ensureConnection()
    let res: Response
    try {
      res = await fetch(`http://127.0.0.1:${conn.port}${path}`, this.buildInit(method, conn.token, body, extraHeaders))
    } catch {
      // Connection refused — daemon stopped between calls.
      this.current = null
      throw new DaemonNotRunningError()
    }
    if (res.status === 401) {
      // Token rotated. Re-read port file once and retry. Wrap the retry
      // in try/catch mirroring the first fetch so a daemon crash between
      // call and retry surfaces as DaemonNotRunningError, not a generic
      // fetch failure.
      this.current = null
      const fresh = this.ensureConnection()
      try {
        res = await fetch(
          `http://127.0.0.1:${fresh.port}${path}`,
          this.buildInit(method, fresh.token, body, extraHeaders),
        )
      } catch {
        this.current = null
        throw new DaemonNotRunningError()
      }
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

  get<T = unknown>(path: string, extraHeaders?: Record<string, string>): Promise<T> {
    return this.request('GET', path, undefined, extraHeaders) as Promise<T>
  }
  post<T = unknown>(path: string, body?: unknown, extraHeaders?: Record<string, string>): Promise<T> {
    return this.request('POST', path, body, extraHeaders) as Promise<T>
  }
  put<T = unknown>(path: string, body?: unknown, extraHeaders?: Record<string, string>): Promise<T> {
    return this.request('PUT', path, body, extraHeaders) as Promise<T>
  }
  delete<T = unknown>(path: string, extraHeaders?: Record<string, string>): Promise<T> {
    return this.request('DELETE', path, undefined, extraHeaders) as Promise<T>
  }
}

export const daemonClient = new DaemonClient()
export { DaemonNotRunningError }
