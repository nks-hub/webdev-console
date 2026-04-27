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

    // Iter 77 — DryRunPlanView renders a "Soak" row when soakSeconds > 0,
    // so the contract for the field must hold (number, never undefined).
    // healthCheckUrl is rendered next to it; it's nullable but the property
    // must exist so the v-if doesn't throw on undefined.
    expect(typeof j.soakSeconds).toBe('number')
    expect(j.soakSeconds).toBeGreaterThanOrEqual(0)
    expect(j).toHaveProperty('healthCheckUrl')
    expect(j.healthCheckUrl === null || typeof j.healthCheckUrl === 'string').toBe(true)
  })

  test('dry-run plan exposes #188/#189 telemetry fields', async ({ authedRequest }) => {
    // Phase 7.5+++ #188 added branch + currentRelease + totalHooksEnabled
    // to the dry-run response; #189 added sourceLastModified so the
    // operator spots stale-source re-deploys before committing. Keep all
    // four asserted so a refactor that drops one breaks Playwright loud.
    const r = await authedRequest.post('/api/nks.wdc.deploy/sites/blog.loc/deploy', {
      data: { host: 'production', branch: 'main', dryRun: true },
    })
    expect(r.status()).toBe(200)
    const j = await r.json()

    // branch echoes back unchanged.
    expect(j.branch).toBe('main')
    // currentRelease can be null on a fresh site, but the property must
    // exist (frontend's DryRunPlanView reads it directly).
    expect(j).toHaveProperty('currentRelease')
    // totalHooksEnabled is a number (count of hooks marked enabled).
    expect(typeof j.totalHooksEnabled).toBe('number')
    expect(j.totalHooksEnabled).toBeGreaterThanOrEqual(0)
    // sourceLastModified is null OR an ISO 8601 timestamp string. The
    // frontend formats it via formatRelative — we just assert shape.
    if (j.sourceLastModified !== null) {
      expect(typeof j.sourceLastModified).toBe('string')
      expect(j.sourceLastModified).toMatch(/^\d{4}-\d{2}-\d{2}T/)
    }
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

  test('test-host-connection probes TCP and surfaces structured result', async ({ authedRequest }) => {
    // Host-only endpoint (#127) used by the host-edit dialog's "Test SSH"
    // button. Two cases: (1) reachable host:port → ok=true + latencyMs,
    // (2) unreachable → ok=false + code=socket_error|timeout.
    //
    // localhost:22 may or may not be reachable on the dev box (depends on
    // whether OpenSSH server is running). To make the test deterministic
    // we hit a port we know is closed (an unprivileged ephemeral port
    // like 65530 — TCP listen would fail), so we should always get
    // ok=false rather than depending on test environment.
    const r = await authedRequest.post('/api/nks.wdc.deploy/test-host-connection', {
      data: { host: '127.0.0.1', port: 65530 },
    })
    expect(r.status()).toBe(200)
    const j = await r.json()
    expect(j.ok).toBe(false)
    // Either timeout or socket_error — both are valid "unreachable" codes.
    // Don't pin the exact code (cross-platform variance).
    expect(['timeout', 'socket_error']).toContain(j.code)
    expect(typeof j.error).toBe('string')
  })

  test('test-host-connection rejects malformed body with 400', async ({ authedRequest }) => {
    // Missing host → 400 (not a hidden 200 with ok=false). The probe
    // endpoint validates input shape before opening any sockets.
    const r1 = await authedRequest.post('/api/nks.wdc.deploy/test-host-connection', {
      data: { port: 22 },
    })
    expect(r1.status()).toBe(400)

    // Out-of-range port → 400 too.
    const r2 = await authedRequest.post('/api/nks.wdc.deploy/test-host-connection', {
      data: { host: '127.0.0.1', port: 70000 },
    })
    expect(r2.status()).toBe(400)
  })
})
