/**
 * wdc_save_cloudflare_config must not let the agent set the Cloudflare
 * credentials or the cloudflared executable path. The daemon refuses them
 * from the MCP token as well; the tool does not offer or forward them.
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

const OPERATOR_ONLY = ['cloudflaredPath', 'tunnelToken', 'apiToken', 'accountId']

beforeEach(() => {
  mockPut.mockClear()
})

describe('wdc_save_cloudflare_config', () => {
  it('does not offer the operator-only fields', () => {
    const keys = Object.keys(saveTool().schema.inputSchema)
    for (const field of OPERATOR_ONLY) expect(keys).not.toContain(field)
    expect(keys).toEqual(expect.arrayContaining(['tunnelName', 'tunnelId', 'defaultZoneId', 'subdomainTemplate']))
  })

  it('forwards only the allowed fields even if more are passed', async () => {
    await saveTool().handler({
      defaultZoneId: 'zone1',
      subdomainTemplate: '{stem}-{hash}',
      cloudflaredPath: '/tmp/evil',
      tunnelToken: 'attacker-tunnel',
      apiToken: 'x',
      accountId: 'y',
    })
    expect(mockPut).toHaveBeenCalledTimes(1)
    const [path, body] = mockPut.mock.calls[0]!
    expect(path).toBe('/api/cloudflare/config')
    for (const field of OPERATOR_ONLY) expect(body).not.toHaveProperty(field)
    expect(body).toMatchObject({ defaultZoneId: 'zone1', subdomainTemplate: '{stem}-{hash}' })
  })
})
