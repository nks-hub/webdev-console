/**
 * Scenario 10 — Caddy as alternative web server.
 * Verifies Caddy plugin is loaded and, if its binary is installed, can be
 * started and stopped via the services API. Skips if Caddy binary missing.
 * The original doc scenario uses Caddy for a full port-8080 site; the
 * runner variant exercises just the service lifecycle since Caddy site
 * generation shares the same Scriban pipeline as Apache (covered by #3/#4).
 */
import { scenario, api, assert, SkipError, sleep } from '../harness.mjs'

export default scenario('10', 'Caddy service lifecycle', 'P2', async (ctx) => {
  // Discover Caddy plugin.
  const plugins = await api.get('/api/plugins')
  assert.statusOk(plugins, 'GET /api/plugins')
  const list = Array.isArray(plugins.body) ? plugins.body : (plugins.body.plugins ?? [])
  const caddyPlugin = list.find((p) => (p.id ?? '').toLowerCase().includes('caddy'))
  if (!caddyPlugin) throw new SkipError('Caddy plugin not loaded')

  // Probe service status first.
  const initial = await api.get('/api/services/caddy')
  if (initial.status !== 200) throw new SkipError('caddy service not exposed')
  const state = initial.body?.state
  if (state === 5) throw new SkipError('Caddy binary not installed (state=Disabled)')

  // Capture current running state so we restore it at the end.
  const wasRunning = state === 2
  ctx.cleanup(async () => {
    if (wasRunning) {
      await api.post('/api/services/caddy/start').catch(() => {})
    } else {
      await api.post('/api/services/caddy/stop').catch(() => {})
    }
  })

  // Try to start — if the daemon returns a 500 with "executable not found"
  // we skip (the plugin is loaded but the binary wasn't downloaded on this
  // host). Any other failure is a real bug.
  await api.post('/api/services/caddy/stop').catch(() => {})
  await sleep(200)

  const start = await api.post('/api/services/caddy/start')
  if (start.status === 500) {
    const err = typeof start.body === 'string' ? start.body : JSON.stringify(start.body ?? '')
    if (err.toLowerCase().includes('executable not found') || err.toLowerCase().includes('not installed')) {
      throw new SkipError('Caddy binary not installed on this host')
    }
  }
  assert.statusOk(start, 'POST /api/services/caddy/start')
  await sleep(500)

  const running = await api.get('/api/services/caddy')
  assert.statusOk(running, 'GET /api/services/caddy after start')
  assert.eq(running.body?.state, 2, 'Caddy state is Running after start')

  const stop = await api.post('/api/services/caddy/stop')
  assert.statusOk(stop, 'POST /api/services/caddy/stop')
})
