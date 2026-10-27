/**
 * wdc_save_cloudflare_config keeps the credential fields; the daemon decides
 * whether the MCP token may set them (capability cloudflare_credentials plus
 * an approved intent), so the tool forwards the intent token as a header.
 */

import { describe, it, expect, vi, beforeEach } from 'vitest'

vi.mock('../daemonClient.js', () => ({
  daemonClient: {
    get: vi.fn(() => Promise.resolve({})),
    put: vi.fn(() => Promise.resolve({})),
    post: vi.fn(() => Promise.resolve({})),
    delete: vi.fn(() => Promise.resolve(null)),
  },
}))

import { registerCloudflareTools } from './cloudflare.js'
import { daemonClient } from '../daemonClient.js'

const mockPut = vi.mocked(daemonClient.put)

interface RegisteredTool {
  name: string
  schema: { inputSchema: Record<string, unknown> }
  handler: (args: Record<string, unknown>) => Promise<unknown>
}

function saveTool(): RegisteredTool {
  const tools: RegisteredTool[] = []
  const server = {
    registerTool(name: string, schema: RegisteredTool['schema'], handler: RegisteredTool['handler']) {
      tools.push({ name, schema, handler })
    },
  }
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  registerCloudflareTools(server as any, { readonly: false, deployScopes: ['*'] })
  return tools.find(t => t.name === 'wdc_save_cloudflare_config')!
}

beforeEach(() => {
  mockPut.mockClear()
})

describe('wdc_save_cloudflare_config', () => {
  it('keeps the credential fields and accepts an intent token', () => {
    const keys = Object.keys(saveTool().schema.inputSchema)
    expect(keys).toEqual(expect.arrayContaining(['cloudflaredPath', 'tunnelToken', 'apiToken', 'accountId', 'intentToken']))
  })

  it('sends the intent token as X-Intent-Token, not in the body', async () => {
    await saveTool().handler({ apiToken: 'tok', accountId: 'acc', intentToken: 'intent.token.sig' })
    expect(mockPut).toHaveBeenCalledWith(
      '/api/cloudflare/config',
      { apiToken: 'tok', accountId: 'acc' },
      { 'X-Intent-Token': 'intent.token.sig' },
    )
  })

  it('sends no intent header without a token', async () => {
    await saveTool().handler({ defaultZoneId: 'zone1' })
    expect(mockPut).toHaveBeenCalledWith('/api/cloudflare/config', { defaultZoneId: 'zone1' }, undefined)
  })
})
