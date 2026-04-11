/**
 * Scenario 12 (condensed) — /api/services returns all managed services
 * with a consistent shape. Original doc scenario 12 is "Start All via GUI";
 * here we exercise the aggregate services endpoint the dashboard polls so
 * any schema drift surfaces in CI before the GUI breaks.
 */
import { scenario, api, assert } from '../harness.mjs'

export default scenario('12', 'Services aggregate endpoint', 'P1', async (_ctx) => {
  const res = await api.get('/api/services')
  assert.statusOk(res, 'GET /api/services')
  const list = Array.isArray(res.body) ? res.body : (res.body.services ?? [])
  assert.ok(Array.isArray(list), 'services response is an array')
  assert.ok(list.length > 0, 'at least one service reported')

  // Every entry must have id + displayName + state or status.
  for (const s of list) {
    assert.ok(typeof s.id === 'string' && s.id.length > 0, `service id present: ${JSON.stringify(s)}`)
    const hasName = typeof s.displayName === 'string' || typeof s.name === 'string'
    assert.ok(hasName, `service has displayName/name: ${JSON.stringify(s)}`)
    const hasState = 'state' in s || 'status' in s
    assert.ok(hasState, `service has state/status: ${JSON.stringify(s)}`)
  }

  // Expected v1 services must be present (any subset — redis/mailpit/caddy
  // may be disabled if PluginState hides them).
  const required = ['apache', 'mysql', 'php']
  for (const req of required) {
    const found = list.find((s) => (s.id ?? '').toLowerCase().includes(req))
    assert.ok(found, `expected service '${req}' in services list`)
  }
})
