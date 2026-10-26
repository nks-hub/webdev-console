/**
 * Error text the agent sees when the daemon refuses a request. It must carry
 * every field the daemon sends (error, reason, detail, hint) and say what to
 * do next, or the agent cannot recover from an intent-gate refusal.
 */

import { mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { DaemonClient, DaemonRequestError } from './daemonClient.js'

const err = (status: number, body: unknown) =>
  new DaemonRequestError(status, typeof body === 'string' ? body : JSON.stringify(body))

describe('DaemonRequestError', () => {
  it('combines error, reason and detail of an intent rejection', () => {
    const e = err(403, { error: 'intent_rejected', reason: 'domain_mismatch', detail: null })
    expect(e.message).toContain('403 intent_rejected — reason: domain_mismatch')
    expect(e.code).toBe('intent_rejected')
    expect(e.reason).toBe('domain_mismatch')
    expect(e.message).toMatch(/Next step: .*different target.*new confirmation/)
  })

  it.each([
    ['missing_token', /call wdc_request_confirmation first/],
    ['expired', /request a new confirmation/],
    ['already_used', /request a new confirmation/],
    ['kind_mismatch', /different operation/],
    ['host_mismatch', /different host/],
    ['signature_mismatch', /not valid/],
  ])('gives a next step for %s', (reason, next) => {
    const e = err(403, { error: 'intent_rejected', reason })
    expect(e.message).toContain(`reason: ${reason}`)
    expect(e.nextStep).toMatch(next)
    expect(e.message).toMatch(next)
  })

  it('tells the agent to wait for approval on 425 pending_confirmation', () => {
    const e = err(425, { error: 'intent_rejected', reason: 'pending_confirmation' })
    expect(e.status).toBe(425)
    expect(e.message).toMatch(/has not approved yet; ask them to approve the request in WDC, then retry with the same intentToken/)
  })

  it('treats a bare 425 as pending confirmation', () => {
    expect(err(425, '').reason).toBe('pending_confirmation')
  })

  it('explains the always_confirm detail', () => {
    const e = err(425, { error: 'intent_rejected', reason: 'pending_confirmation', detail: 'always_confirm' })
    expect(e.message).toMatch(/approve this kind of operation every time/)
  })

  it('points at WDC settings when MCP is disabled', () => {
    const e = err(404, { error: 'mcp_disabled' })
    expect(e.message).toContain('404 mcp_disabled')
    expect(e.message).toMatch(/enable MCP in WDC → Settings/)
  })

  it('surfaces the detail of an MCP-token policy refusal', () => {
    const e = err(403, { error: 'mcp_forbidden', detail: 'operator-only settings: mcp.enabled' })
    expect(e.message).toContain('mcp_forbidden — operator-only settings: mcp.enabled')
    expect(e.message).toMatch(/Next step: .*WDC app/)
  })

  it('shows the hint and capability of a disabled capability group', () => {
    const e = err(403, {
      error: 'mcp_capability_disabled',
      capability: 'databases.write',
      hint: 'Enable "Database changes" in WDC → Settings → MCP to let the agent do this.',
    })
    expect(e.message).toContain('mcp_capability_disabled — capability: databases.write')
    expect(e.message).toContain('Hint: Enable "Database changes" in WDC → Settings → MCP')
    expect(e.capability).toBe('databases.write')
    expect(e.hint).toMatch(/^Enable/)
  })

  it('still gives a next step for a disabled capability without a hint', () => {
    const e = err(403, { error: 'mcp_capability_disabled', capability: 'deploy' })
    expect(e.message).toMatch(/Next step: .*turned off for MCP in WDC/)
  })

  it('includes a hint field next to any error', () => {
    const e = err(400, { error: 'localPaths_required', detail: 'Provide localPaths', hint: 'Configure the host first' })
    expect(e.message).toContain('localPaths_required — Provide localPaths')
    expect(e.message).toContain('Hint: Configure the host first')
    expect(e.nextStep).toBeUndefined()
  })

  it('falls back to message and ProblemDetails title', () => {
    expect(err(500, { message: 'boom' }).message).toBe('Daemon request failed: 500 boom')
    expect(err(400, { title: 'One or more validation errors occurred.' }).message).toContain('validation errors')
  })

  it('keeps plain-text bodies, capped at 300 characters', () => {
    expect(err(502, 'Bad gateway').message).toBe('Daemon request failed: 502 Bad gateway')
    expect(err(500, 'x'.repeat(400)).message.length).toBeLessThan(400)
  })

  it('reports the status when the body is empty', () => {
    expect(err(404, '').message).toBe('Daemon request failed: 404 HTTP 404')
  })

  it('surfaces the capability with its setting key and the daemon hint', () => {
    const e = err(403, {
      error: 'mcp_capability_disabled',
      capability: 'database_admin',
      settingKey: 'mcp.capabilities.database_admin',
      detail: 'POST /api/plugins/mysql/reset-password',
      hint: 'Enable it in WDC → Settings → MCP (mcp.capabilities.database_admin).',
    })
    expect(e.message).toContain('mcp_capability_disabled')
    expect(e.message).toContain('capability: database_admin (mcp.capabilities.database_admin)')
    expect(e.message).toContain('Hint: Enable it in WDC → Settings → MCP')
    expect(e.settingKey).toBe('mcp.capabilities.database_admin')
  })

  it('surfaces the intent kind and target together with the next step', () => {
    const e = err(425, {
      error: 'intent_rejected',
      reason: 'pending_confirmation',
      intentKind: 'node_process',
      intentDomain: 'app.loc',
      hint: 'Ask the user to approve it',
    })
    expect(e.message).toContain('reason: pending_confirmation')
    expect(e.message).toContain('intent: kind node_process on app.loc')
    expect(e.message).toContain('Hint: Ask the user to approve it')
    expect(e.message).toMatch(/Next step: The user has not approved yet/)
    expect(e.intentKind).toBe('node_process')
    expect(e.intentDomain).toBe('app.loc')
  })

  it('does not repeat a detail equal to the error code', () => {
    const e = err(409, { error: 'already_confirmed', detail: 'already_confirmed' })
    expect(e.message).toBe('Daemon request failed: 409 already_confirmed')
  })
})

describe('DaemonClient.request', () => {
  let dir: string
  let client: DaemonClient

  beforeEach(() => {
    dir = mkdtempSync(join(tmpdir(), 'wdc-mcp-client-'))
    const portFile = join(dir, 'port')
    writeFileSync(portFile, '51234\ntok\n', { mode: 0o600 })
    client = new DaemonClient([portFile])
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    rmSync(dir, { recursive: true, force: true })
  })

  it('throws a DaemonRequestError built from the response body', async () => {
    const fetchMock = vi.fn(async () =>
      new Response(JSON.stringify({ error: 'intent_rejected', reason: 'missing_token' }), { status: 403 }),
    )
    vi.stubGlobal('fetch', fetchMock)
    const p = client.delete('/api/sites/shop.loc')
    await expect(p).rejects.toBeInstanceOf(DaemonRequestError)
    await expect(client.delete('/api/sites/shop.loc')).rejects.toThrow(/reason: missing_token[\s\S]*wdc_request_confirmation first/)
    expect(fetchMock).toHaveBeenCalledWith('http://127.0.0.1:51234/api/sites/shop.loc', expect.objectContaining({ method: 'DELETE' }))
  })

  it('returns parsed JSON on success', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => Response.json({ ok: true })))
    await expect(client.get('/api/status')).resolves.toEqual({ ok: true })
  })
})
