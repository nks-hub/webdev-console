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

  test('mcp:kinds endpoint returns 7 core kinds with alwaysConfirm flag', async ({ authedRequest }) => {
    const r = await authedRequest.get('/api/mcp/kinds')
    expect(r.status()).toBe(200)
    const j = await r.json()
    expect(j.count).toBe(7)
    const ids = j.entries.map((k: { id: string }) => k.id).sort()
    expect(ids).toEqual([
      'cancel',
      'deploy',
      'restore',
      'rollback',
      'settings_write',
      'snapshot_create',
      'test_hook',
    ])
    // alwaysConfirm field present on every row (boolean).
    for (const k of j.entries) {
      expect(typeof k.alwaysConfirm).toBe('boolean')
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
