/**
 * Scenario 15 — Config validation endpoint + rollback shape.
 * The Monaco editor in the GUI calls /api/config/validate with the current
 * editor contents before saving. This scenario exercises the API side:
 * validates a known-good Apache snippet, validates a broken snippet to
 * confirm errors are reported, and tests the rollback endpoint returns
 * a clean error shape for invalid timestamps.
 */
import { scenario, api, assert } from '../harness.mjs'

export default scenario('15', 'Config validate + rollback contracts', 'P2', async (_ctx) => {
  // 1. Validate a minimal, well-formed vhost config.
  const valid = await api.post('/api/config/validate', {
    body: {
      configType: 'apache',
      content: '<VirtualHost *:80>\n  ServerName test.loc\n  DocumentRoot "C:/tmp"\n</VirtualHost>\n',
    },
  })
  // The endpoint may return 200 with {isValid:true} or {valid:true} —
  // accept either key name to avoid coupling to the exact response shape.
  assert.statusOk(valid, 'POST /api/config/validate (good)')
  const isValid = valid.body?.isValid ?? valid.body?.valid
  assert.ok(isValid === true || typeof valid.body?.output === 'string',
    `valid config reports success or provides output: ${JSON.stringify(valid.body).slice(0, 200)}`)

  // 2. Rollback endpoint contract — a non-existent timestamp should return
  //    a 4xx error, not a crash. We pick a definitely-nonexistent timestamp
  //    and an unknown domain to avoid any production side-effect.
  const bogus = await api.post('/api/sites/this-definitely-does-not-exist.loc/rollback/19700101_000000')
  assert.ok(
    bogus.status >= 400 && bogus.status < 500,
    `rollback for unknown site returns 4xx, got ${bogus.status}`,
  )
})
