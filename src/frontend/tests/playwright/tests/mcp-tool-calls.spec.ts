import { test, expect } from './_fixtures'

// Phase 8 — REST-level coverage for the new mcp_tool_calls audit log
// endpoints. Frontend visual e2e is intentionally NOT included here
// (would need a running Electron renderer) — instead this spec verifies
// the backend contract the Activity feed depends on. Mirrors the
// pattern of plugin-readiness.spec.ts: hit the daemon API directly via
// authedRequest and assert the envelope shape.

test.describe('MCP tool-calls audit log (Phase 8)', () => {
  test('POST then GET round-trips a row', async ({ authedRequest }) => {
    const post = await authedRequest.post('/api/mcp/tool-calls', {
      data: {
        toolName: 'wdc_pw_smoke',
        caller: 'playwright',
        sessionId: 'pw-test-session',
        dangerLevel: 'read',
        durationMs: 7,
        argsSummary: '{}',
      },
    })
    expect(post.status()).toBe(200)
    const postJson = await post.json()
    expect(typeof postJson.id).toBe('string')
    expect(postJson.id.length).toBeGreaterThan(0)

    // GET must contain the row we just inserted.
    const get = await authedRequest.get('/api/mcp/tool-calls?limit=10&toolName=wdc_pw_smoke')
    expect(get.status()).toBe(200)
    const j = await get.json()
    expect(j.entries.length).toBeGreaterThan(0)
    const ours = j.entries.find((e: { id: string }) => e.id === postJson.id)
    expect(ours).toBeTruthy()
    expect(ours).toMatchObject({
      toolName: 'wdc_pw_smoke',
      caller: 'playwright',
      sessionId: 'pw-test-session',
      dangerLevel: 'read',
      durationMs: 7,
    })
  })

  test('stats endpoint includes percentile fields', async ({ authedRequest }) => {
    const r = await authedRequest.get('/api/mcp/tool-calls/stats?withinMinutes=1440')
    expect(r.status()).toBe(200)
    const s = await r.json()
    expect(typeof s.total).toBe('number')
    expect(typeof s.reads).toBe('number')
    expect(typeof s.mutates).toBe('number')
    expect(typeof s.destructives).toBe('number')
    expect(typeof s.errors).toBe('number')
    expect(typeof s.distinctSessions).toBe('number')
    // Phase 8 polish 7 perf KPIs.
    expect(typeof s.p50DurationMs).toBe('number')
    expect(typeof s.p95DurationMs).toBe('number')
    expect(typeof s.p99DurationMs).toBe('number')
    expect(typeof s.callsPerMinute).toBe('number')
    expect(typeof s.errorRatePercent).toBe('number')
  })

  test('timeline endpoint returns hourly buckets', async ({ authedRequest }) => {
    const r = await authedRequest.get('/api/mcp/tool-calls/timeline?withinHours=24')
    expect(r.status()).toBe(200)
    const j = await r.json()
    expect(j.withinHours).toBe(24)
    expect(Array.isArray(j.buckets)).toBe(true)
    if (j.buckets.length > 0) {
      const b = j.buckets[0]
      expect(typeof b.hour).toBe('string')
      expect(typeof b.total).toBe('number')
      expect(typeof b.reads).toBe('number')
      expect(typeof b.mutates).toBe('number')
      expect(typeof b.destructives).toBe('number')
      expect(typeof b.errors).toBe('number')
    }
  })

  test('by-tool endpoint orders by count desc', async ({ authedRequest }) => {
    const r = await authedRequest.get('/api/mcp/tool-calls/by-tool?withinHours=24&limit=10')
    expect(r.status()).toBe(200)
    const j = await r.json()
    expect(j.limit).toBe(10)
    expect(Array.isArray(j.rows)).toBe(true)
    // Order is descending — verify monotonic non-increasing counts.
    for (let i = 1; i < j.rows.length; i++) {
      expect(j.rows[i - 1].count).toBeGreaterThanOrEqual(j.rows[i].count)
    }
  })

  test('search query (q=) filters results', async ({ authedRequest }) => {
    // Seed a unique tool name so search has a deterministic match.
    const unique = `wdc_pw_unique_${Date.now()}`
    await authedRequest.post('/api/mcp/tool-calls', {
      data: { toolName: unique, dangerLevel: 'read', durationMs: 3 },
    })

    const r = await authedRequest.get(`/api/mcp/tool-calls?q=${unique}&limit=5`)
    expect(r.status()).toBe(200)
    const j = await r.json()
    expect(j.entries.length).toBeGreaterThanOrEqual(1)
    expect(j.entries[0].toolName).toBe(unique)
  })

  test('CSV export streams plain text with header row', async ({ authedRequest }) => {
    const r = await authedRequest.get('/api/mcp/tool-calls/export.csv')
    expect(r.status()).toBe(200)
    const ct = r.headers()['content-type']
    expect(ct).toContain('text/csv')
    const body = await r.text()
    // Header must be exactly the documented column set (RFC 4180).
    const firstLine = body.split('\n')[0]
    expect(firstLine).toContain('called_at')
    expect(firstLine).toContain('tool_name')
    expect(firstLine).toContain('danger_level')
  })

  test('suggested-grants endpoint returns aggregation envelope', async ({ authedRequest }) => {
    const r = await authedRequest.get('/api/mcp/grants/suggested?withinDays=30&minOccurrences=2')
    expect(r.status()).toBe(200)
    const j = await r.json()
    expect(j.withinDays).toBe(30)
    expect(j.minOccurrences).toBe(2)
    expect(typeof j.count).toBe('number')
    expect(Array.isArray(j.suggestions)).toBe(true)
  })
})
