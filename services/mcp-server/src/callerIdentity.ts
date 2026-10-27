// Caller identity the MCP server sends to the daemon as X-Mcp-* headers so
// that WDC trust grants scoped to a session, an install or an AI client can
// match its requests.
//
// These ids are NOT authentication. The MCP token from nks-wdc-mcp.port is
// what authenticates the MCP server; any process holding that token could
// send any of these values. Session/instance/client-scoped grants are a
// convenience boundary (a grant given to one agent run does not silently
// apply to the next), not a security boundary.

import { createHash, randomUUID } from 'node:crypto'
import { chmodSync, existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { homedir, hostname, userInfo } from 'node:os'
import { dirname, join } from 'node:path'

/**
 * One id per MCP server process, i.e. per AI client connection (stdio
 * transport = one process per connection). Also used as the audit-log
 * session id, so the Activity page and session-scoped grants agree.
 */
export const SESSION_ID = randomUUID()

const ID_PATTERN = /^[A-Za-z0-9._:-]{1,128}$/

let instanceIdCache: string | undefined
let instanceIdPathOverride: string | undefined
let clientKeyId: string | undefined

export function defaultInstanceIdPath(): string {
  return join(homedir(), '.wdc', 'mcp-instance-id')
}

/**
 * Stable per-install id. Persisted as a UUID in ~/.wdc/mcp-instance-id on
 * first use; when the file cannot be read or written (read-only home,
 * sandbox) a deterministic hash of host + user is used instead so the id
 * still stays the same across runs.
 */
export function resolveInstanceId(path: string = defaultInstanceIdPath()): string {
  try {
    if (existsSync(path)) {
      const existing = readFileSync(path, 'utf8').trim()
      if (ID_PATTERN.test(existing)) return existing
    }
    const fresh = randomUUID()
    mkdirSync(dirname(path), { recursive: true })
    writeFileSync(path, fresh + '\n', { encoding: 'utf8', mode: 0o600 })
    try { chmodSync(path, 0o600) } catch { /* not supported on every FS */ }
    return fresh
  } catch {
    let user = ''
    try { user = userInfo().username } catch { /* no passwd entry */ }
    return 'host-' + createHash('sha256').update(`${hostname()}\0${user}`).digest('hex').slice(0, 32)
  }
}

/**
 * Stable id for the AI client, derived from the MCP initialize clientInfo
 * name ("claude-code", "Claude Desktop", "cursor-vscode", ...). Readable
 * so an operator can type it into an api_key-scoped grant.
 */
export function clientKeyIdFromName(name: string | undefined | null): string | undefined {
  const raw = (name ?? '').trim().toLowerCase()
  if (!raw) return undefined
  const slug = raw.replace(/[^a-z0-9._-]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 64)
  if (slug) return `client:${slug}`
  return `client:${createHash('sha256').update(raw).digest('hex').slice(0, 16)}`
}

/** Record the client name from the MCP initialize handshake. */
export function setClientName(name: string | undefined | null): void {
  clientKeyId = clientKeyIdFromName(name)
}

/** `client:<name>` of the connected AI client, once the handshake named it. */
export function currentClientKeyId(): string | undefined {
  return clientKeyId
}

/** Headers attached to every daemon request. */
export function identityHeaders(): Record<string, string> {
  if (instanceIdCache === undefined) instanceIdCache = resolveInstanceId(instanceIdPathOverride)
  const headers: Record<string, string> = {
    'X-Mcp-Session-Id': SESSION_ID,
    'X-Mcp-Instance-Id': instanceIdCache,
  }
  if (clientKeyId) headers['X-Mcp-Api-Key-Id'] = clientKeyId
  return headers
}

/** Test hook: forget cached ids and optionally redirect the instance id file. */
export function resetIdentityForTests(instanceIdPath?: string): void {
  instanceIdCache = undefined
  instanceIdPathOverride = instanceIdPath
  clientKeyId = undefined
}
