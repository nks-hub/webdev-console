import { test, expect } from './_fixtures'

// Bind-IP API contract — protects the surface the simple-create dialog
// and SiteDetailSimple's IP-vazby picker depend on:
//
//   1. GET /api/sites/bind-addresses returns an array of objects with
//      stable shape { value, label, description, wildcard, loopback,
//      interfaceName? }. The Element Plus select renders `label` and
//      relies on `wildcard` / `loopback` flags to draw the
//      "doporučeno" badge.
//
//   2. The wildcard "*", IPv4 loopback "127.0.0.1", and IPv6 loopback
//      "::1" entries must always be present — they're platform-
//      independent and the simple-mode UI shows them as recommended
//      starter options for first-time operators.
//
//   3. POST /api/sites with an explicit IP renders the API-level shape
//      that the GUI consumes: SiteConfig with the requested address
//      preserved, or — when warnings fire — a { site, warnings, hints }
//      wrapper. Both shapes are accepted by frontend stores/sites.ts.

test.describe('Bind IP API contract', () => {
  test('GET /api/sites/bind-addresses lists wildcard + both loopbacks', async ({ authedRequest }) => {
    const r = await authedRequest.get('/api/sites/bind-addresses')
    expect(r.status()).toBe(200)
    const opts = await r.json() as Array<{
      value: string
      label: string
      description: string
      wildcard: boolean
      loopback: boolean
      interfaceName?: string | null
    }>
    expect(Array.isArray(opts)).toBe(true)

    const findOpt = (v: string) => opts.find(o => o.value === v)

    const wildcard = findOpt('*')
    expect(wildcard).toBeDefined()
    expect(wildcard!.wildcard).toBe(true)
    expect(wildcard!.loopback).toBe(false)

    const loopV4 = findOpt('127.0.0.1')
    expect(loopV4).toBeDefined()
    expect(loopV4!.wildcard).toBe(false)
    expect(loopV4!.loopback).toBe(true)

    const loopV6 = findOpt('::1')
    expect(loopV6).toBeDefined()
    expect(loopV6!.wildcard).toBe(false)
    expect(loopV6!.loopback).toBe(true)

    // Every option must have non-empty label + description (renderer
    // displays both — empty values would render blank rows in the
    // dropdown).
    for (const opt of opts) {
      expect(opt.label).toBeTruthy()
      expect(opt.description).toBeTruthy()
    }
  })

  test('POST /api/sites with bogus IP returns wrapped warning shape', async ({ authedRequest }) => {
    const domain = 'pw-bind-warn.e2e.loc'
    // Clean up any stale fixture from previous runs.
    await authedRequest.delete(`/api/sites/${domain}`).catch(() => {})

    const r = await authedRequest.post('/api/sites', {
      data: {
        domain,
        documentRoot: 'C:\\tmp\\pw-bind-warn',
        phpVersion: 'none',
        sslEnabled: false,
        httpPort: 80,
        httpsPort: 443,
        aliases: [],
        // 198.51.100.99 is RFC 5737 TEST-NET-2 — guaranteed not assigned
        // to any NIC on this host, so CollectBindAddressWarnings fires.
        bindAddresses: ['198.51.100.99'],
        environment: {},
      },
    })
    expect(r.status()).toBe(201)
    const body = await r.json() as { site?: unknown; warnings?: string[] }
    expect(body.site).toBeDefined()
    expect(Array.isArray(body.warnings)).toBe(true)
    expect(body.warnings!.length).toBeGreaterThan(0)
    const hit = body.warnings!.find(w => /198\.51\.100\.99/.test(w) && /not assigned/i.test(w))
    expect(hit, `expected warning naming bogus IP; got ${JSON.stringify(body.warnings)}`).toBeDefined()

    // Round-trip preserves the requested bind even though it warned.
    const got = await authedRequest.get(`/api/sites/${domain}`)
    expect(got.status()).toBe(200)
    const persisted = await got.json() as { bindAddresses: string[] }
    expect(persisted.bindAddresses).toEqual(['198.51.100.99'])

    await authedRequest.delete(`/api/sites/${domain}`)
  })

  test('POST /api/sites with loopback bind returns raw SiteConfig (no wrapper)', async ({ authedRequest }) => {
    const domain = 'pw-bind-ok.e2e.loc'
    await authedRequest.delete(`/api/sites/${domain}`).catch(() => {})

    const r = await authedRequest.post('/api/sites', {
      data: {
        domain,
        documentRoot: 'C:\\tmp\\pw-bind-ok',
        phpVersion: 'none',
        sslEnabled: false,
        httpPort: 80,
        httpsPort: 443,
        aliases: [],
        bindAddresses: ['127.0.0.1'],
        environment: {},
      },
    })
    expect(r.status()).toBe(201)
    const body = await r.json() as Record<string, unknown>
    // Raw SiteConfig has top-level `domain`, NOT a nested `site` field.
    expect(body.domain).toBe(domain)
    expect(body.site).toBeUndefined()
    expect(body.warnings).toBeUndefined()

    await authedRequest.delete(`/api/sites/${domain}`)
  })
})
