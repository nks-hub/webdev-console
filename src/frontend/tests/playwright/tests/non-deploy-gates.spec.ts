import { test, expect } from './_fixtures'

// Phase 7.5+++ wave 1+2 — non-deploy MCP gates live-asserted.
//
// 11 endpoints across 6 categories now require X-Intent-Token for AI
// callers. With a bogus token the daemon must return 403 (intent_rejected).
// Without the token, GUI flows are untouched (we don't test that path
// here — covered by existing e2e). These specs lock the wire contract
// per kind so a refactor that drops the validator call gets caught loud.
//
// Each spec mints a request with a syntactically-malformed intent token
// (`bogus.fake.signature`). The validator rejects with intent_rejected.
// We don't actually drop databases or delete sites — the gate fires
// before any side-effect.

test.describe('Non-deploy MCP gates — bogus token → 403', () => {
  const TOKEN = 'bogus.fake.signature'

  test('database_drop gates DELETE /api/databases/{name}', async ({ authedRequest }) => {
    const r = await authedRequest.delete('/api/databases/some_test_db', {
      headers: { 'X-Intent-Token': TOKEN },
    })
    expect(r.status()).toBe(403)
    const j = await r.json()
    expect(j.error).toBe('intent_rejected')
  })

  test('database_query gates POST /api/databases/{name}/query', async ({ authedRequest }) => {
    const r = await authedRequest.post('/api/databases/some_test_db/query', {
      headers: { 'X-Intent-Token': TOKEN },
      data: { sql: 'SELECT 1' },
    })
    expect(r.status()).toBe(403)
    const j = await r.json()
    expect(j.error).toBe('intent_rejected')
  })

  test('database_import gates POST /api/databases/{name}/import', async ({ authedRequest }) => {
    const r = await authedRequest.post('/api/databases/some_test_db/import', {
      headers: { 'X-Intent-Token': TOKEN },
      data: { sql: '-- noop' },
    })
    expect(r.status()).toBe(403)
    const j = await r.json()
    expect(j.error).toBe('intent_rejected')
  })

  test('site_delete gates DELETE /api/sites/{domain}', async ({ authedRequest }) => {
    const r = await authedRequest.delete('/api/sites/some.example.loc', {
      headers: { 'X-Intent-Token': TOKEN },
    })
    expect(r.status()).toBe(403)
    const j = await r.json()
    expect(j.error).toBe('intent_rejected')
  })

  test('dns_record_delete gates DELETE /api/cloudflare/zones/.../dns/...', async ({ authedRequest }) => {
    const r = await authedRequest.delete('/api/cloudflare/zones/zone-stub/dns/record-stub', {
      headers: { 'X-Intent-Token': TOKEN },
    })
    expect(r.status()).toBe(403)
    const j = await r.json()
    expect(j.error).toBe('intent_rejected')
  })

  test('ssl_cert_delete gates DELETE /api/ssl/certs/{domain}', async ({ authedRequest }) => {
    const r = await authedRequest.delete('/api/ssl/certs/some.example.loc', {
      headers: { 'X-Intent-Token': TOKEN },
    })
    expect(r.status()).toBe(403)
    const j = await r.json()
    expect(j.error).toBe('intent_rejected')
  })

  test('plugin_install gates POST /api/plugins/install', async ({ authedRequest }) => {
    const r = await authedRequest.post('/api/plugins/install', {
      headers: { 'X-Intent-Token': TOKEN },
      data: { id: 'fake.plugin.id', downloadUrl: 'https://example.com/plugin.zip' },
    })
    expect(r.status()).toBe(403)
    const j = await r.json()
    expect(j.error).toBe('intent_rejected')
  })

  test('plugin_uninstall gates DELETE /api/plugins/{id}', async ({ authedRequest }) => {
    const r = await authedRequest.delete('/api/plugins/fake.plugin.id', {
      headers: { 'X-Intent-Token': TOKEN },
    })
    expect(r.status()).toBe(403)
    const j = await r.json()
    expect(j.error).toBe('intent_rejected')
  })

  test('binary_install gates POST /api/binaries/install', async ({ authedRequest }) => {
    const r = await authedRequest.post('/api/binaries/install', {
      headers: { 'X-Intent-Token': TOKEN },
      data: { app: 'php', version: '99.99.99' },
    })
    expect(r.status()).toBe(403)
    const j = await r.json()
    expect(j.error).toBe('intent_rejected')
  })

  test('service_restart gates POST /api/services/{id}/stop', async ({ authedRequest }) => {
    const r = await authedRequest.post('/api/services/some-service/stop', {
      headers: { 'X-Intent-Token': TOKEN },
    })
    expect(r.status()).toBe(403)
    const j = await r.json()
    expect(j.error).toBe('intent_rejected')
  })

  test('service_restart gates POST /api/services/{id}/restart', async ({ authedRequest }) => {
    const r = await authedRequest.post('/api/services/some-service/restart', {
      headers: { 'X-Intent-Token': TOKEN },
    })
    expect(r.status()).toBe(403)
    const j = await r.json()
    expect(j.error).toBe('intent_rejected')
  })
})
