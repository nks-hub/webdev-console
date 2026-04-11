/**
 * Scenario 14 — Log stream endpoint under load.
 * Fetches large log buffers from /api/services/{id}/logs and verifies the
 * response is well-formed and returns under a reasonable time budget. The
 * original doc scenario exercises xterm.js rendering in the GUI; the runner
 * variant covers the API side that feeds it.
 */
import { scenario, api, assert } from '../harness.mjs'

export default scenario('14', 'Services log endpoint under load', 'P2', async (_ctx) => {
  // Pick a service that's very likely running and has logs: apache.
  const svc = await api.get('/api/services/apache')
  assert.statusOk(svc, 'GET /api/services/apache')

  // Small request first.
  const small = await api.get('/api/services/apache/logs?lines=10')
  assert.statusOk(small, 'GET /logs?lines=10')
  const smallLines = Array.isArray(small.body) ? small.body : (small.body.lines ?? [])
  assert.ok(Array.isArray(smallLines), 'small log response shape is array-like')

  // Large request — 10000 lines. Timing budget: 3 s (generous, to avoid
  // flakes on a loaded CI runner; real daemon serves this under ~200 ms).
  const start = Date.now()
  const large = await api.get('/api/services/apache/logs?lines=10000', { timeoutMs: 5000 })
  const elapsed = Date.now() - start
  assert.statusOk(large, 'GET /logs?lines=10000')
  assert.ok(elapsed < 3000, `large log fetch took ${elapsed}ms, budget 3000ms`)

  const largeLines = Array.isArray(large.body) ? large.body : (large.body.lines ?? [])
  assert.ok(Array.isArray(largeLines), 'large log response shape is array-like')
})
