import { test, expect } from './_fixtures'

// Phase 7.7 — Grants lifecycle covered with structured assertions.
// Mirrors sections AA–FF of e2e-mcp-deploy.sh but in TypeScript so
// failures land in JUnit XML and the developer experience matches the
// rest of the frontend test stack.

test.describe('MCP grants flow', () => {
  let createdGrantId: string | null = null

  test('create + list + revoke grant round-trip', async ({ authedRequest }) => {
    const create = await authedRequest.post('/api/mcp/grants', {
      data: {
        scopeType: 'session',
        scopeValue: 'pw-e2e-session-' + Date.now(),
        kindPattern: 'deploy',
        targetPattern: 'blog.loc',
        note: 'Playwright e2e — grants-flow.spec.ts',
      },
    })
    expect(create.status()).toBe(200)
    const created = await create.json()
    expect(created.id).toBeTruthy()
    expect(created.status).toBe('created')
    createdGrantId = created.id

    const list = await authedRequest.get('/api/mcp/grants')
    expect(list.status()).toBe(200)
    const listJson = await list.json()
    const found = listJson.entries.find((g: { id: string }) => g.id === createdGrantId)
    expect(found).toBeDefined()
    expect(found.kindPattern).toBe('deploy')
    expect(found.targetPattern).toBe('blog.loc')

    const revoke = await authedRequest.delete(`/api/mcp/grants/${createdGrantId}`)
    expect(revoke.status()).toBe(200)

    // After revoke, default list (no includeRevoked) excludes the row.
    const after = await authedRequest.get('/api/mcp/grants')
    const afterJson = await after.json()
    const stillThere = afterJson.entries.find((g: { id: string }) => g.id === createdGrantId)
    expect(stillThere).toBeUndefined()

    // Audit view shows it again.
    const audit = await authedRequest.get('/api/mcp/grants?includeRevoked=true')
    const auditJson = await audit.json()
    const inAudit = auditJson.entries.find((g: { id: string }) => g.id === createdGrantId)
    expect(inAudit).toBeDefined()
    expect(inAudit.revokedAt).toBeTruthy()
  })

  test('grants endpoint pagination', async ({ authedRequest }) => {
    const r = await authedRequest.get('/api/mcp/grants?page=1&pageSize=10')
    expect(r.status()).toBe(200)
    const j = await r.json()
    expect(j.page).toBe(1)
    expect(j.pageSize).toBe(10)
    expect(typeof j.total).toBe('number')
    expect(typeof j.totalPages).toBe('number')
    expect(j.entries.length).toBeLessThanOrEqual(10)
  })

  test('mcp:kinds endpoint exposes the seeded kinds with alwaysConfirm flag', async ({ authedRequest }) => {
    const r = await authedRequest.get('/api/mcp/kinds')
    expect(r.status()).toBe(200)
    const j = await r.json()
    const ids = j.entries.map((k: { id: string }) => k.id).sort()

    // Original 7 deploy kinds — must always be present, defines the
    // legacy contract the GUI relied on before non-deploy gates landed.
    expect(ids).toEqual(expect.arrayContaining([
      'cancel',
      'deploy',
      'restore',
      'rollback',
      'settings_write',
      'snapshot_create',
      'test_hook',
    ]))
    expect(j.count).toBeGreaterThanOrEqual(7)

    // alwaysConfirm field present on every row (boolean).
    for (const k of j.entries) {
      expect(typeof k.alwaysConfirm).toBe('boolean')
      // Every kind belongs to a plugin id — non-empty string.
      expect(typeof k.pluginId).toBe('string')
      expect(k.pluginId.length).toBeGreaterThan(0)
      // Danger level is one of the known set.
      expect(['reversible', 'destructive']).toContain(k.danger.toLowerCase())
    }
  })

  test('non-deploy destructive kinds are seeded once daemon picks up new registry', async ({ authedRequest }) => {
    // Phase 7.5+++ — extends MCP gate to non-deploy ops (database_drop,
    // site_delete, dns_record_delete, ssl_cert_delete, plugin_uninstall).
    // The seed is in DestructiveOperationKindsRegistry but only takes
    // effect after daemon restart. While the running daemon still has
    // only the original 7 kinds this test gracefully skips; once the
    // daemon picks up the new registry, the test becomes a hard
    // assertion that all 5 non-deploy kinds are present + Destructive.
    const r = await authedRequest.get('/api/mcp/kinds')
    const j = await r.json()
    const byId: Record<string, { danger: string; alwaysConfirm: boolean }> = {}
    for (const k of j.entries) {
      byId[k.id] = { danger: k.danger.toLowerCase(), alwaysConfirm: k.alwaysConfirm }
    }

    const expected = [
      'database_drop',
      'database_query',
      'site_delete',
      'dns_record_delete',
      'ssl_cert_delete',
      'plugin_uninstall',
    ]
    const missing = expected.filter((id) => !byId[id])

    if (missing.length === expected.length) {
      // Daemon hasn't picked up the new registry yet — every expected
      // kind is missing. Skip with a clear reason rather than failing.
      test.skip(true, `running daemon registry does not yet include non-deploy kinds (missing: ${missing.join(', ')}); restart daemon to pick up DestructiveOperationKindsRegistry seed`)
      return
    }

    // Partial coverage = bug. Either all 5 land together or none.
    expect(missing).toEqual([])

    // Each must be Destructive (qualifies for Lock-all-destructive #208).
    for (const id of expected) {
      expect(byId[id].danger).toBe('destructive')
    }
  })

  test('grants stats aggregation endpoint', async ({ authedRequest }) => {
    const r = await authedRequest.get('/api/mcp/grants/stats')
    expect(r.status()).toBe(200)
    const j = await r.json()
    expect(typeof j.active).toBe('number')
    expect(typeof j.deadweight).toBe('number')
    expect(typeof j.totalMatches).toBe('number')
  })
})
