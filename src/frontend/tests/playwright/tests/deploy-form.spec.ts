import { test, expect } from './_fixtures'

// Phase 7.7 — Deploy form coverage. Tests the dry-run preview API
// which powers the GUI Preview button (DeployQuickBar +
// DeployConfirmModal). The bash e2e covers the same paths via curl;
// these duplicate them in TS so frontend devs can extend.

test.describe('Deploy form (dry-run preview)', () => {
  test('dry-run returns plan without DB write', async ({ authedRequest }) => {
    const r = await authedRequest.post('/api/nks.wdc.deploy/sites/blog.loc/deploy', {
      data: { host: 'production', branch: 'main', dryRun: true },
    })
    expect(r.status()).toBe(200)
    const j = await r.json()

    // Core fields the GUI Preview dialog reads.
    expect(j.dryRun).toBe(true)
    expect(j.deployId).toBeNull()
    expect(j.wouldRelease).toMatch(/^[0-9]{8}_[0-9]{6}$/)
    expect(j.wouldExtractTo).toContain('releases')
    expect(j.branch).toBe('main')
    expect(typeof j.totalHooksEnabled).toBe('number')
    expect(typeof j.keepReleases).toBe('number')
    expect(typeof j.existingReleaseCount).toBe('number')
    expect(typeof j.alwaysConfirmKind).toBe('boolean')
    expect(Array.isArray(j.sharedDirs)).toBe(true)
    expect(Array.isArray(j.sharedFiles)).toBe(true)
  })

  test('dry-run alwaysConfirmKind reflects live setting', async ({ authedRequest }) => {
    // Save current value to restore.
    const before = await authedRequest.get('/api/settings')
    const beforeJson = await before.json()
    const original = beforeJson['mcp.always_confirm_kinds'] || ''

    try {
      // Flip on.
      await authedRequest.put('/api/settings', {
        data: { 'mcp.always_confirm_kinds': 'deploy' },
      })

      const r = await authedRequest.post('/api/nks.wdc.deploy/sites/blog.loc/deploy', {
        data: { host: 'production', dryRun: true },
      })
      const j = await r.json()
      expect(j.alwaysConfirmKind).toBe(true)
    } finally {
      // Always restore — even if assertions failed.
      await authedRequest.put('/api/settings', {
        data: { 'mcp.always_confirm_kinds': original },
      })
    }
  })

  test('cancel endpoint MCP-gated under kind=cancel', async ({ authedRequest }) => {
    const r = await authedRequest.delete('/api/nks.wdc.deploy/sites/blog.loc/deploys/never-existed-id', {
      headers: { 'X-Intent-Token': 'bogus.fake.signature' },
    })
    expect(r.status()).toBe(403)
    const j = await r.json()
    expect(j.error).toBe('intent_rejected')
  })

  test('test-hook endpoint MCP-gated under kind=test_hook', async ({ authedRequest }) => {
    const r = await authedRequest.post('/api/nks.wdc.deploy/sites/blog.loc/hooks/test', {
      headers: { 'X-Intent-Token': 'bogus.fake.signature' },
      data: { type: 'shell', command: 'echo hi', timeoutSeconds: 5 },
    })
    expect(r.status()).toBe(403)
  })

  test('settings PUT MCP-gated under kind=settings_write', async ({ authedRequest }) => {
    const r = await authedRequest.put('/api/nks.wdc.deploy/sites/blog.loc/settings', {
      headers: { 'X-Intent-Token': 'bogus.fake.signature' },
      data: { hosts: [] },
    })
    expect(r.status()).toBe(403)
  })

  test('snapshot-now MCP-gated under kind=snapshot_create', async ({ authedRequest }) => {
    const r = await authedRequest.post('/api/nks.wdc.deploy/sites/blog.loc/snapshot-now', {
      headers: { 'X-Intent-Token': 'bogus.fake.signature' },
      data: { host: 'production' },
    })
    expect(r.status()).toBe(403)
  })
})
