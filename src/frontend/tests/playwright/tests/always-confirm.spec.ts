import { test, expect } from './_fixtures'

// Always-confirm override coverage. Bash e2e #198 already proves the
// validator path with full MCP caller context; this Playwright spec
// stays at the surface where the operator interacts: the
// mcp.always_confirm_kinds setting drives the alwaysConfirm flag on
// /api/mcp/kinds (which the McpKinds GUI tab reads to render the 🔒
// chip) and intents minted under always-confirm stay in
// pending_confirmation regardless of whether a grant exists.
//
// We deliberately don't reproduce the full grant-match flow here —
// that needs MCP caller headers (X-Mcp-Session-Id etc.) to drive the
// validator's pre-check, which is well-covered by the bash suite.
// What this spec catches is regressions on the surface the GUI hits.

test.describe('Always-confirm override', () => {
  test('mcp.always_confirm_kinds setting flips alwaysConfirm flag on listed kinds', async ({ authedRequest }) => {
    // Capture original setting so we can restore even if asserts fail.
    const before = await authedRequest.get('/api/settings')
    const beforeJson = await before.json()
    const original = beforeJson['mcp.always_confirm_kinds'] || ''

    try {
      // Flip on always-confirm for a deliberate subset.
      await authedRequest.put('/api/settings', {
        data: { 'mcp.always_confirm_kinds': 'deploy,restore' },
      })

      const kinds = await authedRequest.get('/api/mcp/kinds')
      expect(kinds.status()).toBe(200)
      const kindsJson = await kinds.json()
      const byId: Record<string, boolean> = {}
      for (const k of kindsJson.entries) byId[k.id] = k.alwaysConfirm

      // Listed kinds must report alwaysConfirm=true.
      expect(byId['deploy']).toBe(true)
      expect(byId['restore']).toBe(true)
      // Unlisted kinds must report alwaysConfirm=false — no spillover.
      expect(byId['rollback']).toBe(false)
      expect(byId['cancel']).toBe(false)
      expect(byId['snapshot_create']).toBe(false)
      expect(byId['settings_write']).toBe(false)
      expect(byId['test_hook']).toBe(false)

      // Flip to "lock all destructive" preset (#208 mirrors this).
      await authedRequest.put('/api/settings', {
        data: { 'mcp.always_confirm_kinds': 'restore,rollback,settings_write,snapshot_create,test_hook' },
      })

      const kindsAll = await authedRequest.get('/api/mcp/kinds')
      const kindsAllJson = await kindsAll.json()
      const lockedIds = kindsAllJson.entries
        .filter((k: { alwaysConfirm: boolean }) => k.alwaysConfirm)
        .map((k: { id: string }) => k.id)
        .sort()
      expect(lockedIds).toEqual(['restore', 'rollback', 'settings_write', 'snapshot_create', 'test_hook'])
    } finally {
      // Always restore — even if assertions failed.
      await authedRequest.put('/api/settings', {
        data: { 'mcp.always_confirm_kinds': original },
      })
    }
  })

  test('intent under always-confirm stays in pending_confirmation', async ({ authedRequest }) => {
    // Create an intent without any caller context (no X-Mcp-* headers)
    // so the grants pre-check has nothing to match. Whether or not
    // always-confirm is on, this intent must end up pending_confirmation
    // (no auto-confirm path available). Asserts the safe default —
    // missing headers never accidentally auto-approve.
    const before = await authedRequest.get('/api/settings')
    const beforeJson = await before.json()
    const original = beforeJson['mcp.always_confirm_kinds'] || ''

    try {
      await authedRequest.put('/api/settings', {
        data: { 'mcp.always_confirm_kinds': 'deploy' },
      })

      const create = await authedRequest.post('/api/mcp/intents', {
        data: {
          domain: 'blog.loc',
          host: 'production',
          kind: 'deploy',
          expiresIn: 60,
        },
      })
      expect(create.status()).toBe(200)
      const created = await create.json()
      expect(created.intentId).toBeTruthy()

      // Read state via list — intentId is the freshly-minted one.
      const list = await authedRequest.get('/api/mcp/intents?limit=20')
      const listJson = await list.json()
      const found = listJson.entries.find((e: { intentId: string }) => e.intentId === created.intentId)
      expect(found).toBeDefined()
      expect(found.state).toBe('pending_confirmation')
      expect(found.confirmedAt).toBeNull()
      expect(found.matchedGrantId).toBeNull()
    } finally {
      await authedRequest.put('/api/settings', {
        data: { 'mcp.always_confirm_kinds': original },
      })
    }
  })
})
