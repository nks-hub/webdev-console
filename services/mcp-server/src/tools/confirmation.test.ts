/**
 * Unit tests for the intent mapping used by wdc_request_confirmation. The
 * (kind, domain, host) triple must match what the daemon route checks, or the
 * approved intent is rejected with kind/domain/host_mismatch.
 */

import { beforeEach, describe, it, expect, vi } from 'vitest'

vi.mock('../daemonClient.js', async (importOriginal) => ({
  ...(await importOriginal<object>()),
  daemonClient: (await import('../testing/recorder.js')).recorder.client,
}))

import { DaemonRequestError } from '../daemonClient.js'
import { recorder, type RecordedCall } from '../testing/recorder.js'
import { fakeServer, resultBody } from '../testing/fakeServer.js'
import {
  fetchIntentState,
  intentFor,
  intentHeaders,
  normaliseState,
  registerConfirmationTools,
  requestConfirmation,
  waitForIntent,
} from './confirmation.js'

describe('intentFor', () => {
  it.each([
    ['database_drop', 'shop_db', { kind: 'database_drop', domain: 'shop_db', host: '*db*' }],
    ['database_execute', 'shop_db', { kind: 'database_query', domain: 'shop_db', host: '*db*' }],
    ['site_delete', 'blog.test', { kind: 'site_delete', domain: 'blog.test', host: '*site*' }],
    ['service_stop', 'apache', { kind: 'service_restart', domain: 'apache', host: '*svc*' }],
    ['ssl_cert_delete', 'blog.test', { kind: 'ssl_cert_delete', domain: 'blog.test', host: '*ssl*' }],
    ['dns_record_delete', 'zone123', { kind: 'dns_record_delete', domain: 'zone123', host: '*dns*' }],
    ['binary_install', 'php', { kind: 'binary_install', domain: 'php', host: '*binary*' }],
    ['plugin_install', 'nks.wdc.redis', { kind: 'plugin_install', domain: 'nks.wdc.redis', host: '*plugin*' }],
    ['backup_restore', 'ignored', { kind: 'backup_restore', domain: '*backup*', host: '*backup*' }],
  ] as const)('%s on %s', (operation, target, expected) => {
    expect(intentFor(operation, target)).toEqual(expected)
  })
})

describe('intentHeaders', () => {
  it('sends the token as X-Intent-Token', () => {
    expect(intentHeaders('a.b.c')).toEqual({ 'X-Intent-Token': 'a.b.c' })
  })
  it('sends nothing without a token', () => {
    expect(intentHeaders(undefined)).toBeUndefined()
  })
})

const INTENT = 'a1b2c3d4-0000-4000-8000-000000000001'
const created = { intentId: INTENT, intentToken: `${INTENT}.nonce.sig`, expiresAt: '2030-01-01T00:10:00Z' }
const row = (state: string) => ({
  intentId: INTENT,
  state,
  expiresAt: '2030-01-01T00:10:00Z',
  kind: 'site_delete',
  domain: 'shop.loc',
  host: '*site*',
})

/** Responder that answers the intent endpoints; `states` is served in order to successive status reads. */
function intentDaemon(states: string[], opts: { confirmRequestFails?: boolean } = {}) {
  let i = 0
  return (call: RecordedCall) => {
    if (call.method === 'POST' && call.path === '/api/mcp/intents') return created
    if (call.method === 'POST' && call.path === '/api/mcp/intents/confirm-request') {
      if (opts.confirmRequestFails) throw new DaemonRequestError(500, JSON.stringify({ error: 'sse_down' }))
      return null
    }
    if (call.method === 'GET' && call.path === `/api/mcp/intents/${INTENT}`) {
      return row(states[Math.min(i++, states.length - 1)]!)
    }
    throw new Error(`unexpected ${call.method} ${call.path}`)
  }
}

/** Fake clock whose sleep advances time instantly. */
function fakeClock() {
  let t = 0
  return { now: () => t, sleep: async (ms: number) => void (t += ms) }
}

beforeEach(() => recorder.reset())

describe('normaliseState', () => {
  it.each([
    ['pending_confirmation', 'pending'],
    ['ready', 'ready'],
    ['consumed', 'consumed'],
    ['expired', 'expired'],
    ['revoked', 'revoked'],
    ['weird', 'unknown'],
    [undefined, 'unknown'],
  ] as const)('%s → %s', (state, expected) => {
    expect(normaliseState(state)).toBe(expected)
  })
})

describe('fetchIntentState', () => {
  it('reads GET /api/mcp/intents/{id}', async () => {
    recorder.reset(intentDaemon(['ready']))
    const state = await fetchIntentState(INTENT)
    expect(state).toMatchObject({ intentId: INTENT, status: 'ready', expiresAt: '2030-01-01T00:10:00Z' })
    expect(recorder.calls).toEqual([{ method: 'GET', path: `/api/mcp/intents/${INTENT}`, headers: undefined }])
  })

  it('falls back to the inventory list on daemons without the single-intent route', async () => {
    recorder.reset((call) => {
      if (call.path === `/api/mcp/intents/${INTENT}`) throw new DaemonRequestError(404, '')
      if (call.path === '/api/mcp/intents?limit=500') return { count: 1, entries: [row('consumed')] }
      throw new Error('unexpected')
    })
    expect((await fetchIntentState(INTENT)).status).toBe('consumed')
  })

  it('does not fall back when the daemon says the intent does not exist', async () => {
    recorder.reset(() => {
      throw new DaemonRequestError(404, JSON.stringify({ error: 'intent_not_found', intentId: INTENT }))
    })
    await expect(fetchIntentState(INTENT)).rejects.toThrow(/intent_not_found/)
    expect(recorder.calls).toHaveLength(1)
  })
})

describe('waitForIntent', () => {
  it('reads once when waitSeconds is 0', async () => {
    recorder.reset(intentDaemon(['pending_confirmation', 'ready']))
    expect((await waitForIntent(INTENT, 0, fakeClock())).status).toBe('pending')
    expect(recorder.calls).toHaveLength(1)
  })

  it('polls until the user approves', async () => {
    recorder.reset(intentDaemon(['pending_confirmation', 'pending_confirmation', 'ready']))
    expect((await waitForIntent(INTENT, 30, fakeClock())).status).toBe('ready')
    expect(recorder.calls).toHaveLength(3)
  })

  it('gives up after waitSeconds and reports pending', async () => {
    recorder.reset(intentDaemon(['pending_confirmation']))
    const state = await waitForIntent(INTENT, 10, { ...fakeClock(), pollMs: 2000 })
    expect(state.status).toBe('pending')
    expect(recorder.calls).toHaveLength(6) // t = 0, 2, 4, 6, 8, 10 s
  })

  it('caps the wait at 120 seconds', async () => {
    recorder.reset(intentDaemon(['pending_confirmation']))
    await waitForIntent(INTENT, 10_000, { ...fakeClock(), pollMs: 10_000 })
    expect(recorder.calls).toHaveLength(13) // 0..120 s every 10 s
  })
})

describe('requestConfirmation', () => {
  const req = { kind: 'site_delete', domain: 'shop.loc', host: '*site*', ttlSeconds: 600, prompt: 'AI wants to delete site shop.loc: cleanup' }

  it('mints the intent and raises the approval banner', async () => {
    recorder.reset(intentDaemon(['pending_confirmation']))
    const r = await requestConfirmation(req)
    expect(recorder.calls).toEqual([
      { method: 'POST', path: '/api/mcp/intents', body: { domain: 'shop.loc', host: '*site*', kind: 'site_delete', expiresIn: 600 }, headers: undefined },
      { method: 'POST', path: '/api/mcp/intents/confirm-request', body: { intentId: INTENT, prompt: req.prompt }, headers: undefined },
    ])
    expect(r).toMatchObject({ ...created, status: 'pending' })
    expect(r.warning).toBeUndefined()
  })

  it('returns the created intent with a warning when the banner request fails', async () => {
    recorder.reset(intentDaemon([], { confirmRequestFails: true }))
    const r = await requestConfirmation(req)
    expect(r).toMatchObject({ intentId: INTENT, intentToken: created.intentToken, status: 'pending' })
    expect(r.warning).toMatch(/intent was created but WDC could not be asked[\s\S]*sse_down[\s\S]*Do not request another confirmation/)
  })

  it('waits for the decision when waitSeconds is set', async () => {
    recorder.reset(intentDaemon(['pending_confirmation', 'ready']))
    const r = await requestConfirmation({ ...req, waitSeconds: 60 }, fakeClock())
    expect(r).toMatchObject({ intentId: INTENT, status: 'ready' })
    expect(r.next).toMatch(/Approved/)
  })

  it('passes releaseId through when given', async () => {
    recorder.reset(intentDaemon([]))
    await requestConfirmation({ ...req, releaseId: 'r1' })
    expect(recorder.calls[0]!.body).toMatchObject({ releaseId: 'r1' })
  })
})

describe('confirmation tools', () => {
  it('registers only the status tool in read-only mode', () => {
    const { server, tools } = fakeServer()
    registerConfirmationTools(server, { readonly: true, deployScopes: ['*'] })
    expect([...tools.keys()]).toEqual(['wdc_get_confirmation_status'])
    expect(tools.get('wdc_get_confirmation_status')!.config.annotations).toMatchObject({ readOnlyHint: true })
  })

  it('wdc_get_confirmation_status reports status, expiry and the next step', async () => {
    const { server, call } = fakeServer()
    registerConfirmationTools(server, { readonly: false, deployScopes: ['*'] })
    recorder.reset(intentDaemon(['revoked']))
    const body = resultBody(await call('wdc_get_confirmation_status', { intentId: INTENT })) as Record<string, unknown>
    expect(body).toMatchObject({ intentId: INTENT, status: 'revoked', expiresAt: '2030-01-01T00:10:00Z' })
    expect(body.next).toMatch(/revoked/)
  })

  it('wdc_request_confirmation maps the operation to its intent and builds the prompt', async () => {
    const { server, call } = fakeServer()
    registerConfirmationTools(server, { readonly: false, deployScopes: ['*'] })
    recorder.reset(intentDaemon(['pending_confirmation']))
    const r = await call('wdc_request_confirmation', { operation: 'backup_restore', target: 'x', reason: 'roll back' })
    expect(r.isError).toBeUndefined()
    expect(recorder.calls[0]!.body).toEqual({ domain: '*backup*', host: '*backup*', kind: 'backup_restore', expiresIn: 600 })
    expect(recorder.calls[1]!.body).toMatchObject({ prompt: 'AI wants to restore a backup over the current state: roll back' })
  })

  it('rejects waitSeconds above 120', async () => {
    const { server, call } = fakeServer()
    registerConfirmationTools(server, { readonly: false, deployScopes: ['*'] })
    await expect(call('wdc_request_confirmation', { operation: 'site_delete', target: 'a.loc', reason: 'r', waitSeconds: 121 })).rejects.toThrow()
  })
})
