/**
 * Contract tests for the deploy tools: each tool must call a route the daemon
 * registers, with the method, body and intent header that route reads, and
 * mint intents with the (kind, host) that route's intent gate checks.
 */

import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import { beforeEach, describe, expect, it, vi } from 'vitest'

vi.mock('../daemonClient.js', async (importOriginal) => ({
  ...(await importOriginal<object>()),
  daemonClient: (await import('../testing/recorder.js')).recorder.client,
}))

import { recorder, type RecordedCall } from '../testing/recorder.js'
import { fakeServer, resultBody } from '../testing/fakeServer.js'
import { DAEMON_SRC, matchRoute } from '../testing/daemonRoutes.js'
import { CONFIRMABLE_OPERATIONS, intentFor } from './confirmation.js'
import { deployIntentFor, registerDeployTools } from './deploy.js'

const P = '/api/nks.wdc.deploy/sites/shop.loc'
const DEPLOY = '11111111-1111-4111-8111-111111111111'
const GROUP = '22222222-2222-4222-8222-222222222222'
const TOKEN = 'intent-id.nonce.signature'
const KEY = '33333333-3333-4333-8333-333333333333'

function tools(opts = { readonly: false, deployScopes: ['*'] }) {
  const s = fakeServer()
  registerDeployTools(s.server, opts)
  return s
}

beforeEach(() => recorder.reset())

interface Case {
  tool: string
  args: Record<string, unknown>
  expect: Omit<RecordedCall, 'headers'> & { headers?: Record<string, string> }
  respond?: unknown
}

const cases: Case[] = [
  {
    tool: 'wdc_deploy_site',
    args: { domain: 'shop.loc', host: 'production', branch: 'main', intentToken: TOKEN, idempotencyKey: KEY },
    expect: {
      method: 'POST',
      path: `${P}/deploy`,
      body: { host: 'production', branch: 'main', idempotencyKey: KEY, intentToken: TOKEN },
      headers: { 'X-Intent-Token': TOKEN },
    },
  },
  {
    tool: 'wdc_deploy_preflight',
    args: { domain: 'shop.loc', host: 'staging', intentToken: TOKEN },
    expect: {
      method: 'POST',
      path: `${P}/deploy`,
      body: { host: 'staging', dryRun: true, intentToken: TOKEN },
      headers: { 'X-Intent-Token': TOKEN },
    },
  },
  {
    tool: 'wdc_deploy_rollback',
    args: { domain: 'shop.loc', deployId: DEPLOY, intentToken: TOKEN },
    expect: { method: 'POST', path: `${P}/deploys/${DEPLOY}/rollback`, body: {}, headers: { 'X-Intent-Token': TOKEN } },
  },
  {
    tool: 'wdc_deploy_cancel',
    args: { domain: 'shop.loc', deployId: DEPLOY, intentToken: TOKEN },
    expect: { method: 'DELETE', path: `${P}/deploys/${DEPLOY}`, headers: { 'X-Intent-Token': TOKEN } },
  },
  {
    tool: 'wdc_deploy_group_start',
    args: { domain: 'shop.loc', hosts: ['web1', 'web2'], intentToken: TOKEN },
    expect: { method: 'POST', path: `${P}/groups`, body: { hosts: ['web1', 'web2'] }, headers: { 'X-Intent-Token': TOKEN } },
  },
  {
    tool: 'wdc_deploy_group_rollback',
    args: { domain: 'shop.loc', groupId: GROUP, intentToken: TOKEN },
    expect: { method: 'POST', path: `${P}/groups/${GROUP}/rollback`, body: {}, headers: { 'X-Intent-Token': TOKEN } },
  },
  {
    tool: 'wdc_deploy_restore_snapshot',
    args: { domain: 'shop.loc', deployId: DEPLOY, intentToken: TOKEN },
    expect: { method: 'POST', path: `${P}/snapshots/${DEPLOY}/restore`, body: { confirm: true }, headers: { 'X-Intent-Token': TOKEN } },
  },
  {
    tool: 'wdc_deploy_get_status',
    args: { domain: 'shop.loc', deployId: DEPLOY },
    expect: { method: 'GET', path: `${P}/deploys/${DEPLOY}` },
  },
  {
    tool: 'wdc_deploy_list_releases',
    args: { domain: 'shop.loc', limit: 10 },
    expect: { method: 'GET', path: `${P}/history?limit=10` },
  },
  {
    tool: 'wdc_deploy_list_snapshots',
    args: { domain: 'shop.loc' },
    expect: { method: 'GET', path: `${P}/snapshots` },
  },
  {
    tool: 'wdc_deploy_list_groups',
    args: { domain: 'shop.loc' },
    expect: { method: 'GET', path: `${P}/groups?limit=50` },
  },
  {
    tool: 'wdc_deploy_list_targets',
    args: { domain: 'shop.loc' },
    expect: { method: 'GET', path: `${P}/settings` },
    respond: { hosts: [] },
  },
  {
    tool: 'wdc_deploy_group_status',
    args: { domain: 'shop.loc', groupId: GROUP },
    expect: { method: 'GET', path: `${P}/groups?limit=200` },
    respond: { entries: [{ id: GROUP }] },
  },
]

describe('deploy tool → daemon route contract', () => {
  it.each(cases)('$tool calls $expect.method $expect.path', async (c) => {
    recorder.reset(() => c.respond ?? { entries: [] })
    const { call } = tools()
    const r = await call(c.tool, c.args)
    expect(r.isError, r.content[0]?.text).toBeUndefined()
    expect(recorder.calls).toHaveLength(1)
    const [actual] = recorder.calls
    expect(actual).toEqual({ headers: undefined, ...c.expect })
    expect(matchRoute(actual!.method, actual!.path), `${actual!.method} ${actual!.path} is not a daemon route`).toBeDefined()
  })

  it('covers every registered deploy tool except the intent minting one', () => {
    const covered = new Set(cases.map(c => c.tool))
    const registered = [...tools().tools.keys()].filter(t => t !== 'wdc_deploy_create_intent' && t !== 'wdc_deploy_get_release' && t !== 'wdc_deploy_get_logs')
    expect(registered.filter(t => !covered.has(t))).toEqual([])
  })

  it('never sends X-Allow-Unconfirmed, even with the old env var set', async () => {
    process.env.MCP_DEPLOY_AUTO_APPROVE = 'true'
    try {
      const { call } = tools()
      for (const c of cases) await call(c.tool, c.args)
      expect(recorder.calls.every(cl => !cl.headers || !('X-Allow-Unconfirmed' in cl.headers))).toBe(true)
    } finally {
      delete process.env.MCP_DEPLOY_AUTO_APPROVE
    }
  })

  it.each(['wdc_deploy_site', 'wdc_deploy_rollback', 'wdc_deploy_cancel', 'wdc_deploy_group_start', 'wdc_deploy_group_rollback', 'wdc_deploy_restore_snapshot'])(
    '%s requires an intentToken',
    async (tool) => {
      const c = cases.find(x => x.tool === tool)!
      const { intentToken: _omit, ...args } = c.args
      await expect(tools().call(tool, args)).rejects.toThrow(/intentToken/)
      expect(recorder.calls).toHaveLength(0)
    },
  )

  it('rejects a group with fewer than two hosts or repeated hosts', async () => {
    const { call } = tools()
    await expect(call('wdc_deploy_group_start', { domain: 'shop.loc', hosts: ['web1'], intentToken: TOKEN })).rejects.toThrow()
    await expect(call('wdc_deploy_group_start', { domain: 'shop.loc', hosts: ['web1', 'web1'], intentToken: TOKEN })).rejects.toThrow()
  })

  it('no longer registers the lock/unlock stubs', () => {
    const names = [...tools().tools.keys()]
    expect(names).not.toContain('wdc_deploy_lock')
    expect(names).not.toContain('wdc_deploy_unlock')
  })
})

describe('deploy tool results', () => {
  it('wdc_deploy_get_logs reads the daemon\'s error field', async () => {
    recorder.reset(() => ({ deployId: DEPLOY, host: 'production', finalPhase: 'Failed', error: 'rsync exited 23', success: false }))
    const body = resultBody(await tools().call('wdc_deploy_get_logs', { domain: 'shop.loc', deployId: DEPLOY }))
    expect(body).toMatchObject({ deployId: DEPLOY, host: 'production', finalPhase: 'Failed', error: 'rsync exited 23', success: false })
  })

  it('wdc_deploy_list_targets returns host fields only', async () => {
    recorder.reset(() => ({
      hosts: [{ name: 'production', branch: 'main', sshHost: 'web', localTargetPath: '/srv/shop' }, { sshHost: 'nameless' }],
      notifications: { slackWebhook: 'https://hooks.slack.com/secret' },
      advanced: { envVars: { DB_PASSWORD: 'x' } },
    }))
    const body = resultBody(await tools().call('wdc_deploy_list_targets', { domain: 'shop.loc' })) as Record<string, unknown>
    expect(body).toMatchObject({ domain: 'shop.loc', configured: true, count: 1 })
    expect(body.hosts).toEqual([expect.objectContaining({ name: 'production', branch: 'main', localTargetPath: '/srv/shop' })])
    expect(JSON.stringify(body)).not.toContain('secret')
    expect(JSON.stringify(body)).not.toContain('DB_PASSWORD')
  })

  it('wdc_deploy_list_targets reports an unconfigured site', async () => {
    recorder.reset(() => {
      throw new Error('Daemon request failed: 404 no_settings_yet')
    })
    const body = resultBody(await tools().call('wdc_deploy_list_targets', { domain: 'shop.loc' }))
    expect(body).toEqual({ domain: 'shop.loc', configured: false, count: 0, hosts: [] })
  })

  it('wdc_deploy_group_status errors for an unknown group', async () => {
    recorder.reset(() => ({ entries: [{ id: 'other' }] }))
    const r = await tools().call('wdc_deploy_group_status', { domain: 'shop.loc', groupId: GROUP })
    expect(r.isError).toBe(true)
    expect(r.content[0]!.text).toMatch(/not found/)
  })
})

describe('wdc_deploy_create_intent', () => {
  const created = { intentId: 'iid', intentToken: TOKEN, expiresAt: '2030-01-01T00:00:00Z' }

  it.each([
    ['deploy', { host: 'production' }, { kind: 'deploy', host: 'production' }],
    ['rollback', {}, { kind: 'rollback', host: '*' }],
    ['cancel', { host: 'ignored' }, { kind: 'cancel', host: '*' }],
    ['restore', {}, { kind: 'restore', host: '*restore*' }],
  ] as const)('mints %s intents', async (operation, extra, intent) => {
    recorder.reset(c => (c.path === '/api/mcp/intents' ? created : null))
    const body = resultBody(await tools().call('wdc_deploy_create_intent', { domain: 'shop.loc', operation, ...extra })) as Record<string, unknown>
    expect(recorder.calls[0]).toMatchObject({ method: 'POST', path: '/api/mcp/intents', body: { domain: 'shop.loc', ...intent, expiresIn: 900 } })
    expect(recorder.calls[1]).toMatchObject({ method: 'POST', path: '/api/mcp/intents/confirm-request', body: { intentId: 'iid' } })
    expect(body).toMatchObject({ ...created, ...intent })
    for (const call of recorder.calls) expect(matchRoute(call.method, call.path)).toBeDefined()
  })

  it('requires a host for deploy intents', async () => {
    const r = await tools().call('wdc_deploy_create_intent', { domain: 'shop.loc' })
    expect(r.isError).toBe(true)
    expect(r.content[0]!.text).toMatch(/host is required/)
    expect(recorder.calls).toHaveLength(0)
  })

  it('returns the intent with a warning when the banner cannot be raised', async () => {
    recorder.reset(c => {
      if (c.path === '/api/mcp/intents') return created
      throw new Error('Daemon request failed: 500 boom')
    })
    const r = await tools().call('wdc_deploy_create_intent', { domain: 'shop.loc', operation: 'rollback' })
    expect(r.isError).toBeUndefined()
    expect(resultBody(r)).toMatchObject({ intentToken: TOKEN, warning: expect.stringMatching(/WDC → MCP → Intents/) })
  })
})

describe('intent (kind, host) matches what the daemon route checks', () => {
  const src = readFileSync(join(DAEMON_SRC, 'NKS.WebDevConsole.Daemon', 'Deploy', 'DeployEndpoints.cs'), 'utf8')

  /** Source of the handler registered for a route, up to the next registration. */
  function handler(method: string, route: string): string {
    const start = src.indexOf(`app.Map${method}("/api/nks.wdc.deploy${route}"`)
    expect(start, `${method} ${route} registration`).toBeGreaterThanOrEqual(0)
    const next = src.indexOf('app.Map', start + 10)
    return src.slice(start, next < 0 ? undefined : next)
  }

  function gate(code: string): { kind: string; host: string } {
    const m = code.match(/ValidateAndConsumeAsync\(\s*\w+ \?\? "", "(\w+)", domain, (?:host: )?("[^"]*"|[\w[\]0-9]+)/)
    expect(m, 'intent gate call').not.toBeNull()
    return { kind: m![1]!, host: m![2]!.replace(/"/g, '') }
  }

  it.each([
    ['Post', '/sites/{domain}/deploys/{deployId}/rollback', 'rollback'],
    ['Delete', '/sites/{domain}/deploys/{deployId}', 'cancel'],
    ['Post', '/sites/{domain}/groups/{groupId}/rollback', 'rollback'],
  ] as const)('%s %s', (method, route, op) => {
    expect(gate(handler(method, route))).toEqual(deployIntentFor(op, undefined))
  })

  it('deploy checks kind deploy for the requested host, read from the body', () => {
    const code = handler('Post', '/sites/{domain}/deploy')
    expect(gate(code)).toEqual({ kind: 'deploy', host: 'host' })
    expect(code).toMatch(/root\.TryGetProperty\("intentToken"/)
    expect(deployIntentFor('deploy', 'production')).toEqual({ kind: 'deploy', host: 'production' })
  })

  it('group deploy checks kind deploy for hosts[0] and needs two hosts', () => {
    const code = handler('Post', '/sites/{domain}/groups')
    expect(gate(code)).toEqual({ kind: 'deploy', host: 'hosts[0]' })
    expect(code).toMatch(/hosts\.Count < 2/)
  })

  it('snapshot restore checks kind restore for *restore*', () => {
    expect(src).toMatch(/var intentHost = bodyHost \?\? "\*restore\*";/)
    expect(src).toMatch(/ValidateAndConsumeAsync\(\s*intentToken \?\? "", "restore", domain, intentHost/)
    expect(deployIntentFor('restore', undefined)).toEqual({ kind: 'restore', host: '*restore*' })
  })

  it('wdc_request_confirmation mints the same deploy intents', () => {
    expect(intentFor('deploy_rollback', 'shop.loc')).toEqual({ kind: 'rollback', domain: 'shop.loc', host: '*' })
    expect(intentFor('deploy_cancel', 'shop.loc')).toEqual({ kind: 'cancel', domain: 'shop.loc', host: '*' })
    expect(intentFor('deploy_restore', 'shop.loc')).toEqual({ kind: 'restore', domain: 'shop.loc', host: '*restore*' })
    expect(Object.keys(CONFIRMABLE_OPERATIONS)).toEqual(expect.arrayContaining(['deploy_rollback', 'deploy_cancel', 'deploy_restore']))
  })
})

describe('scopes', () => {
  it('registers only read tools for deploy:read', () => {
    const names = [...tools({ readonly: false, deployScopes: ['deploy:read'] }).tools.keys()]
    expect(names.every(n => tools().tools.get(n)!.config.annotations?.readOnlyHint)).toBe(true)
    expect(names).toContain('wdc_deploy_group_status')
  })

  it('registers no write tools in read-only mode', () => {
    const t = tools({ readonly: true, deployScopes: ['*'] }).tools
    expect([...t.values()].every(x => x.config.annotations?.readOnlyHint)).toBe(true)
  })
})
