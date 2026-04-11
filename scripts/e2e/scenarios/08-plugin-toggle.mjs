/**
 * Scenario 8 — Plugin enable/disable (no daemon restart).
 * P0 smoke — pure API. Uses the SSL plugin which is always loaded on dev
 * machines and has no external prerequisites (unlike Redis which needs a
 * binary). Disables the plugin, verifies it reports enabled:false, then
 * re-enables it. Daemon must stay responsive the whole time.
 */
import { scenario, api, assert, SkipError, sleep } from '../harness.mjs'

export default scenario('8', 'Plugin enable/disable', 'P0', async (_ctx) => {
  const before = await api.get('/api/plugins')
  assert.statusOk(before, 'GET /api/plugins')
  const plugins = Array.isArray(before.body) ? before.body : before.body.plugins ?? []
  // Pick a non-critical plugin: prefer redis, fall back to mailpit, then ssl.
  const preferred = ['redis', 'mailpit', 'caddy', 'ssl']
  let target = null
  for (const pref of preferred) {
    const p = plugins.find((x) => (x.id ?? x.name ?? '').toLowerCase().includes(pref))
    if (p) { target = p; break }
  }
  if (!target) throw new SkipError('no togglable plugin found')
  const pluginId = target.id ?? target.name

  const wasEnabled = target.enabled !== false

  // Toggle off.
  const disable = await api.post(`/api/plugins/${pluginId}/disable`)
  assert.statusOk(disable, `POST /api/plugins/${pluginId}/disable`)

  // Daemon must still respond.
  const mid = await api.get('/api/plugins')
  assert.statusOk(mid, 'GET /api/plugins after disable')
  const midPlugins = Array.isArray(mid.body) ? mid.body : mid.body.plugins ?? []
  const midTarget = midPlugins.find((x) => (x.id ?? x.name) === pluginId)
  assert.ok(midTarget, 'plugin still listed after disable')
  assert.eq(midTarget.enabled, false, `plugin ${pluginId} reports enabled=false`)

  // Brief settle for any SSE consumers.
  await sleep(200)

  // Toggle back on (restore original state).
  if (wasEnabled) {
    const enable = await api.post(`/api/plugins/${pluginId}/enable`)
    assert.statusOk(enable, `POST /api/plugins/${pluginId}/enable`)

    const after = await api.get('/api/plugins')
    assert.statusOk(after, 'GET /api/plugins after re-enable')
    const afterPlugins = Array.isArray(after.body) ? after.body : after.body.plugins ?? []
    const afterTarget = afterPlugins.find((x) => (x.id ?? x.name) === pluginId)
    assert.ok(afterTarget, 'plugin still listed after enable')
    assert.eq(afterTarget.enabled, true, `plugin ${pluginId} reports enabled=true`)
  }
})
