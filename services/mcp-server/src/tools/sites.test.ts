import { beforeEach, describe, expect, it, vi } from 'vitest'

vi.mock('../daemonClient.js', async (importOriginal) => ({
  ...(await importOriginal<object>()),
  daemonClient: (await import('../testing/recorder.js')).recorder.client,
}))

import { recorder } from '../testing/recorder.js'
import { fakeServer, resultBody } from '../testing/fakeServer.js'
import { registerSitesTools } from './sites.js'

beforeEach(() => recorder.reset())

describe('wdc_get_site_access_log', () => {
  it('keeps the newest entries when the log is too large', async () => {
    const entries = Array.from({ length: 1000 }, (_, i) => ({
      timestamp: `2030-01-01T00:00:${String(i).padStart(4, '0')}`,
      path: `/p/${i}`,
      userAgent: 'u'.repeat(150),
    }))
    recorder.reset(() => ({ domain: 'shop.loc', count: 1000, topClients: [], entries }))
    const { server, call } = fakeServer()
    registerSitesTools(server, { readonly: true, deployScopes: ['*'] })

    const body = resultBody(await call('wdc_get_site_access_log', { domain: 'shop.loc', limit: 1000 })) as {
      entries: { path: string }[]
      truncation: { omittedItems: number }
    }
    expect(recorder.calls[0]!.path).toBe('/api/sites/shop.loc/access-log?limit=1000')
    expect(body.entries.at(-1)!.path).toBe('/p/999')
    expect(body.truncation.omittedItems).toBeGreaterThan(0)
  })
})
