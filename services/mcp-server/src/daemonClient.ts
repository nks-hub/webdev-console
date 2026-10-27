// Discovery + auth + HTTP plumbing for the NKS WDC daemon.
//
// Reads the daemon's `nks-wdc-mcp.port` file from the temp folder: one line
// port, next line the per-session MCP Bearer token. Token rotates every
// daemon restart — on 401 we re-read the file and retry once.

import { existsSync, readFileSync, statSync } from 'node:fs'
import { join } from 'node:path'
import { tmpdir } from 'node:os'

import { identityHeaders } from './callerIdentity.js'

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
   * before scopes existed. Scopes only decide which tools are listed; the
   * destructive deploy routes still need an intent the user approved in WDC.
   */
  scopes: string[]
}

/** A request body sent as-is instead of as JSON (e.g. a SQL dump). */
export class RawBody {
  constructor(
    readonly text: string,
    readonly contentType: string,
  ) {}
}

class DaemonNotRunningError extends Error {
  constructor() {
    super(
      'NKS WDC daemon is not running. Start the WDC GUI or run `wdc daemon start`. ' +
        'The MCP server reads nks-wdc-mcp.port in the temp folder, which the daemon writes on start.',
    )
  }
}

/**
 * What the agent should do next for a structured daemon refusal, keyed by
 * the intent-gate `reason` or the top-level `error` code.
 */
const NEXT_STEPS: Record<string, string> = {
  missing_token:
    'This operation needs the user\'s approval: call wdc_request_confirmation first, ' +
    'then retry this tool with the returned intentToken.',
  pending_confirmation:
    'The user has not approved yet; ask them to approve the request in WDC, then retry with the ' +
    'same intentToken. wdc_get_confirmation_status shows whether it has been approved.',
  expired: 'The approval expired; request a new confirmation with wdc_request_confirmation.',
  already_used:
    'The intentToken was already used or revoked; request a new confirmation with wdc_request_confirmation.',
  kind_mismatch:
    'The intentToken was approved for a different operation; request a new confirmation for this exact operation.',
  domain_mismatch:
    'The intentToken was approved for a different target; request a new confirmation for this exact target.',
  host_mismatch:
    'The intentToken was approved for a different host; request a new confirmation for this exact host.',
  kind_unknown:
    'WDC only accepts registered operation kinds (strict kinds mode); ask the user to check MCP settings in WDC.',
  not_found: 'The intent does not exist (it may have been cleaned up); request a new confirmation.',
  nonce_mismatch: 'The intentToken is not valid; request a new confirmation and pass its intentToken unchanged.',
  malformed_token: 'The intentToken is not valid; request a new confirmation and pass its intentToken unchanged.',
  signature_mismatch: 'The intentToken is not valid; request a new confirmation and pass its intentToken unchanged.',
  mcp_disabled: 'MCP is turned off in WDC; ask the user to enable MCP in WDC → Settings → AI clients (MCP).',
  mcp_forbidden: 'The MCP token may not do this; the user has to do it in the WDC app.',
  mcp_capability_disabled:
    'This capability is turned off for MCP in WDC; ask the user to enable it in WDC → Settings → AI clients (MCP) if they want you to do this.',
}

function str(v: unknown): string | undefined {
  if (typeof v === 'string') return v.trim() || undefined
  if (typeof v === 'number' || typeof v === 'boolean') return String(v)
  return undefined
}

/**
 * Structured daemon refusal. The message is composed from every field the
 * daemon sends (error, reason, detail, hint, message, ProblemDetails title),
 * the MCP capability the operator has to enable (with its setting key), the
 * intent kind/target to request via wdc_request_confirmation, plus a
 * next-step sentence, so the agent can act on it without guessing.
 */
export class DaemonRequestError extends Error {
  readonly status: number
  /** Top-level `error` code, e.g. `intent_rejected`, `mcp_forbidden`. */
  readonly code?: string
  /** Intent-gate `reason`, e.g. `pending_confirmation`. */
  readonly reason?: string
  readonly detail?: string
  readonly hint?: string
  readonly capability?: string
  /** Setting that turns the capability on, e.g. `mcp.capabilities.database_admin`. */
  readonly settingKey?: string
  /** Intent kind and target the daemon expects an approved intent for. */
  readonly intentKind?: string
  readonly intentDomain?: string
  readonly nextStep?: string
  readonly body: unknown

  constructor(status: number, text: string) {
    let body: unknown = text
    try {
      body = text ? JSON.parse(text) : undefined
    } catch {
      /* plain-text body */
    }
    const obj = body && typeof body === 'object' && !Array.isArray(body) ? (body as Record<string, unknown>) : undefined
    const code = str(obj?.error)
    const reason = str(obj?.reason) ?? (status === 425 ? 'pending_confirmation' : undefined)
    const rawDetail = str(obj?.detail)
    // The intent gate's only detail code: the operator requires a fresh
    // approval for this kind every time, so trust grants were skipped.
    const detail =
      rawDetail === 'always_confirm'
        ? 'WDC requires the user to approve this kind of operation every time (trust grants do not apply)'
        : rawDetail
    const hint = str(obj?.hint)
    const capability = str(obj?.capability)
    const settingKey = str(obj?.settingKey)
    const intentKind = str(obj?.intentKind)
    const intentDomain = str(obj?.intentDomain)
    const extra = [str(obj?.message), str(obj?.title)]

    const parts: string[] = []
    const push = (s: string | undefined) => {
      if (s && !parts.some(p => p.includes(s))) parts.push(s)
    }
    push(code)
    if (reason) push(`reason: ${reason}`)
    if (capability) push(`capability: ${capability}${settingKey ? ` (${settingKey})` : ''}`)
    if (intentKind) push(`intent: kind ${intentKind} on ${intentDomain ?? '?'}`)
    push(detail)
    for (const e of extra) push(e)
    if (!obj) {
      const plain = typeof body === 'string' ? body.trim() : ''
      push(plain.length > 300 ? plain.slice(0, 300) + '…' : plain || undefined)
    }
    if (parts.length === 0) parts.push(`HTTP ${status}`)

    const nextStep = (reason && NEXT_STEPS[reason]) ?? (code && NEXT_STEPS[code]) ?? undefined
    let message = `Daemon request failed: ${status} ${parts.join(' — ')}`
    if (hint) message += `\nHint: ${hint}`
    if (nextStep && !(code === 'mcp_capability_disabled' && hint)) message += `\nNext step: ${nextStep}`
    super(message)
    this.name = 'DaemonRequestError'
    this.status = status
    this.code = code
    this.reason = reason
    this.detail = detail
    this.hint = hint
    this.capability = capability
    this.settingKey = settingKey
    this.intentKind = intentKind
    this.intentDomain = intentDomain
    this.nextStep = nextStep
    this.body = body
  }
}

function isTrustedPortFile(path: string): boolean {
  if (process.platform === 'win32' || typeof process.getuid !== 'function') return true
  try {
    const st = statSync(path)
    return st.uid === process.getuid() && (st.mode & 0o077) === 0
  } catch {
    return false
  }
}

export class DaemonClient {
  private current: PortFile | null = null

  /** @param portFiles Port file locations to try, in order (tests pass their own). */
  constructor(private readonly portFiles?: string[]) {}

  /**
   * Locate the daemon's port + token. The wdc daemon writes this file to
   * the OS temp directory on every startup so multiple concurrent users
   * don't collide on a shared `~/.wdc` location.
   */
  private readPortFile(): PortFile | null {
    // The daemon writes a separate MCP token to nks-wdc-mcp.port. Requests
    // with it must present GUI-confirmed intents for destructive operations
    // and cannot approve intents themselves. The GUI port file
    // (nks-wdc-daemon.port) is deliberately never read: its token can approve.
    const candidates = this.portFiles ?? [join(tmpdir(), 'nks-wdc-mcp.port')]
    for (const path of candidates) {
      if (!existsSync(path)) continue
      // In the shared temp dir another local user could pre-create this file
      // and point us at their own listener; trust only our own 0600 file.
      if (!isTrustedPortFile(path)) continue
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
    // X-Mcp-Session-Id / -Instance-Id / -Api-Key-Id let the daemon match
    // session-, install- and client-scoped trust grants. They are not
    // authentication (the bearer token is); see callerIdentity.ts.
    const headers: Record<string, string> = {
      ...identityHeaders(),
      Authorization: `Bearer ${token}`,
    }
    if (body instanceof RawBody) headers['Content-Type'] = body.contentType
    else if (body !== undefined) headers['Content-Type'] = 'application/json'
    if (extraHeaders) Object.assign(headers, extraHeaders)
    return {
      method,
      headers,
      body: body instanceof RawBody ? body.text : body !== undefined ? JSON.stringify(body) : undefined,
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
      const text = await res.text().catch(() => '')
      throw new DaemonRequestError(res.status, text)
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
  /** POST a non-JSON body (e.g. a SQL dump as text/plain). */
  postText<T = unknown>(path: string, text: string, contentType: string, extraHeaders?: Record<string, string>): Promise<T> {
    return this.request('POST', path, new RawBody(text, contentType), extraHeaders) as Promise<T>
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
