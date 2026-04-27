import { test, expect } from './_fixtures'

// Phase 7.4 #109-D1 — `X-Wdc-Backend-Mode` response header on every
// /api/nks.wdc.deploy/* response. Locks the contract so future
// refactors of the deploy.enabled middleware don't accidentally drop
// the informational header (DeploySettings GUI badge + downstream
// consumers depend on it for migration progress visibility).
//
// Default value is "built-in" because deploy.useLegacyHostHandlers
// defaults to true. After phase B/C/D the operator can flip it to
// "plugin" via Settings; the header reflects whichever mode is
// currently authoritative.
//
// Header is attached via ctx.Response.OnStarting in the deploy gate
// middleware, so it appears on success responses (200), error
// responses (4xx/5xx from handler), AND on routes that get gated to
// 404 deploy_disabled — operators see backend-mode regardless of
// downstream handler outcome. We assert on a known-200 path here.

test.describe('X-Wdc-Backend-Mode response header (#109-D1)', () => {
  test('GET /api/nks.wdc.deploy/* returns X-Wdc-Backend-Mode: built-in by default', async ({ authedRequest }) => {
    const r = await authedRequest.get('/api/nks.wdc.deploy/sites/blog.loc/history')
    expect(r.status()).toBe(200)
    const headers = r.headers()
    // Playwright lowercases header names — we set "X-Wdc-Backend-Mode"
    // server-side but read "x-wdc-backend-mode" client-side.
    expect(headers['x-wdc-backend-mode']).toBe('built-in')
  })

  test('header attaches to multiple deploy paths consistently', async ({ authedRequest }) => {
    const paths = [
      '/api/nks.wdc.deploy/sites/blog.loc/snapshots',
      '/api/nks.wdc.deploy/sites/blog.loc/settings',
      '/api/nks.wdc.deploy/sites/blog.loc/groups',
    ]
    for (const path of paths) {
      const r = await authedRequest.get(path)
      const mode = r.headers()['x-wdc-backend-mode']
      expect(mode, `${path} should expose backend-mode header`).toBe('built-in')
    }
  })

  test('header NOT attached to non-deploy routes', async ({ authedRequest }) => {
    // /api/plugins is outside the deploy.enabled middleware scope.
    // The header is gated on path StartsWithSegments("/api/nks.wdc.deploy")
    // so non-deploy routes get no header. Verifies the gate is scoped.
    const r = await authedRequest.get('/api/plugins')
    expect(r.status()).toBe(200)
    expect(r.headers()['x-wdc-backend-mode']).toBeUndefined()
  })
})
