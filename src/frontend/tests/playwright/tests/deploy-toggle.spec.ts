import { test, expect } from './_fixtures'

// Operator-facing switch: deploy.enabled=false hides every nksdeploy
// surface so users who don't run AI agents (or who haven't set up
// deploys) see no /api/nks.wdc.deploy/* endpoint at all.
//
// This spec validates the gate from the prompt's perspective —
// "nksdeply jako plugin vypinatelny" — by flipping the setting through
// the public API and asserting deploy routes flip to 404. It mirrors
// the bash e2e #105 sections in TS so a regression on the toggle's
// short-circuit middleware (Program.cs line ~3804) breaks Playwright
// loud, not just the bash suite.
//
// Bookkeeping: the test ALWAYS restores the original setting in a
// finally block so a partial failure can't leave the operator with
// deploy disabled.

// Iter 37 — force serial mode so this describe block holds the worker
// for its full duration. Without this, Playwright (despite workers:1)
// can interleave per-test boundaries between sibling describe blocks,
// and our deploy.enabled=false mid-test window leaks `404 deploy_disabled`
// into other specs' /api/nks.wdc.deploy/* assertions. Marking this
// suite serial pins all its tests to one continuous worker slot, so
// the false-state window can't coincide with other tests' execution.
test.describe.configure({ mode: 'serial' })

test.describe('deploy.enabled toggle (operator switch)', () => {
  test('flipping deploy.enabled=false makes deploy routes 404', async ({ authedRequest }) => {
    const before = await authedRequest.get('/api/settings')
    const beforeJson = await before.json()
    const original = beforeJson['deploy.enabled']

    try {
      // Baseline: deploy is on (defaults true). Routes respond.
      const baseline = await authedRequest.get('/api/nks.wdc.deploy/sites/blog.loc/history')
      expect(baseline.status()).toBe(200)

      // Flip off.
      const off = await authedRequest.put('/api/settings', {
        data: { 'deploy.enabled': 'false' },
      })
      expect(off.status()).toBe(200)

      // Every deploy route now returns the gate's 404 envelope. Test
      // multiple methods + paths so a regression that only affects one
      // method (e.g. forgetting to short-circuit GET) gets caught.
      const checks: Array<{ method: 'GET' | 'POST' | 'PUT' | 'DELETE'; path: string }> = [
        { method: 'GET', path: '/api/nks.wdc.deploy/sites/blog.loc/history' },
        { method: 'GET', path: '/api/nks.wdc.deploy/sites/blog.loc/snapshots' },
        { method: 'GET', path: '/api/nks.wdc.deploy/sites/blog.loc/settings' },
        { method: 'POST', path: '/api/nks.wdc.deploy/sites/blog.loc/deploy' },
        { method: 'POST', path: '/api/nks.wdc.deploy/sites/blog.loc/snapshot-now' },
      ]
      for (const c of checks) {
        const r = c.method === 'GET'
          ? await authedRequest.get(c.path)
          : c.method === 'POST'
            ? await authedRequest.post(c.path, { data: {} })
            : c.method === 'PUT'
              ? await authedRequest.put(c.path, { data: {} })
              : await authedRequest.delete(c.path)
        expect(r.status(), `${c.method} ${c.path} should 404 when deploy disabled`).toBe(404)
        const j = await r.json().catch(() => ({}))
        expect(j.error, `${c.method} ${c.path} envelope`).toBe('deploy_disabled')
      }

      // Non-deploy routes stay reachable — confirms the gate is scoped,
      // not a global kill switch.
      const settings = await authedRequest.get('/api/settings')
      expect(settings.status()).toBe(200)
      const plugins = await authedRequest.get('/api/plugins')
      expect(plugins.status()).toBe(200)
    } finally {
      // Restore original. Stringify booleans to the same shape the API
      // accepts — a missing original means "default (true)".
      await authedRequest.put('/api/settings', {
        data: { 'deploy.enabled': original ?? 'true' },
      })
    }
  })

  test('flipping back to enabled restores deploy routes', async ({ authedRequest }) => {
    // Sanity check the inverse: after restoring deploy.enabled=true,
    // a route that just returned 404 in the previous test responds
    // normally again. Catches a "stuck disabled" regression where the
    // gate would somehow latch.
    await authedRequest.put('/api/settings', { data: { 'deploy.enabled': 'true' } })

    const r = await authedRequest.get('/api/nks.wdc.deploy/sites/blog.loc/history')
    expect(r.status()).toBe(200)
    const j = await r.json()
    expect(j.domain).toBe('blog.loc')
    expect(Array.isArray(j.entries)).toBe(true)
  })
})
