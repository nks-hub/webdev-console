/**
 * Caller identity headers — the daemon only auto-approves session-, install-
 * and client-scoped trust grants when the MCP server sends X-Mcp-* headers.
 */

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { mkdtempSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { tmpdir } from 'node:os'

import {
  SESSION_ID,
  clientKeyIdFromName,
  identityHeaders,
  resetIdentityForTests,
  resolveInstanceId,
  setClientName,
} from './callerIdentity.js'

describe('callerIdentity', () => {
  let dir: string

  beforeEach(() => {
    dir = mkdtempSync(join(tmpdir(), 'wdc-mcp-identity-'))
    // Keep ~/.wdc/mcp-instance-id out of the real home directory.
    resetIdentityForTests(join(dir, 'mcp-instance-id'))
  })
  afterEach(() => {
    rmSync(dir, { recursive: true, force: true })
    vi.unstubAllEnvs()
    vi.unstubAllGlobals()
  })

  it('persists one instance id per install and reuses it', () => {
    const path = join(dir, 'nested', 'mcp-instance-id')
    const first = resolveInstanceId(path)
    expect(first).toMatch(/^[0-9a-f-]{36}$/)
    expect(resolveInstanceId(path)).toBe(first)
    expect(readFileSync(path, 'utf8').trim()).toBe(first)
    if (process.platform !== 'win32') expect(statSync(path).mode & 0o077).toBe(0)
  })

  it('replaces a corrupt instance id file', () => {
    const path = join(dir, 'mcp-instance-id')
    writeFileSync(path, 'not a valid id!\n')
    const id = resolveInstanceId(path)
    expect(id).toMatch(/^[0-9a-f-]{36}$/)
  })

  it('derives a stable, readable client id from the MCP client name', () => {
    expect(clientKeyIdFromName('claude-code')).toBe('client:claude-code')
    expect(clientKeyIdFromName('Claude Desktop')).toBe('client:claude-desktop')
    expect(clientKeyIdFromName('  ')).toBeUndefined()
    expect(clientKeyIdFromName(undefined)).toBeUndefined()
    // Non-ASCII-only names still get a stable header-safe id.
    const a = clientKeyIdFromName('クロード')
    expect(a).toMatch(/^client:[0-9a-f]{16}$/)
    expect(clientKeyIdFromName('クロード')).toBe(a)
  })

  it('sends session and instance ids, plus the client id once known', () => {
    const before = identityHeaders()
    expect(before['X-Mcp-Session-Id']).toBe(SESSION_ID)
    expect(before['X-Mcp-Instance-Id']).toBeTruthy()
    expect(before['X-Mcp-Api-Key-Id']).toBeUndefined()

    setClientName('cursor-vscode')
    const after = identityHeaders()
    expect(after['X-Mcp-Api-Key-Id']).toBe('client:cursor-vscode')
    expect(after['X-Mcp-Instance-Id']).toBe(before['X-Mcp-Instance-Id'])
  })
})

describe('daemonClient identity headers', () => {
  let dir: string

  beforeEach(() => {
    dir = mkdtempSync(join(tmpdir(), 'wdc-mcp-port-'))
    resetIdentityForTests(join(dir, 'mcp-instance-id'))
  })
  afterEach(() => {
    rmSync(dir, { recursive: true, force: true })
    vi.unstubAllEnvs()
    vi.unstubAllGlobals()
  })

  it.skipIf(process.platform === 'win32')('attaches X-Mcp-* headers to every daemon request', async () => {
    // daemonClient discovers the daemon through os.tmpdir(), which honours TMPDIR.
    vi.stubEnv('TMPDIR', dir)
    writeFileSync(join(dir, 'nks-wdc-mcp.port'), '51234\nmcp-token\n', { mode: 0o600 })
    const fetchMock = vi.fn(async () => new Response('{}', {
      status: 200,
      headers: { 'content-type': 'application/json' },
    }))
    vi.stubGlobal('fetch', fetchMock)
    setClientName('claude-code')

    const { daemonClient } = await import('./daemonClient.js')
    await daemonClient.post('/api/mcp/intents/confirm-request', { intentId: 'x' })

    expect(fetchMock).toHaveBeenCalledOnce()
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit]
    expect(url).toBe('http://127.0.0.1:51234/api/mcp/intents/confirm-request')
    const headers = init.headers as Record<string, string>
    expect(headers.Authorization).toBe('Bearer mcp-token')
    expect(headers['X-Mcp-Session-Id']).toBe(SESSION_ID)
    expect(headers['X-Mcp-Instance-Id']).toBeTruthy()
    expect(headers['X-Mcp-Api-Key-Id']).toBe('client:claude-code')
  })
})
