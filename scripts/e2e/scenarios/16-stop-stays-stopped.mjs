/**
 * Scenario 16 — manual stop must stay stopped.
 * Covers the ProcessManager fix where an intentional stop previously flowed
 * through the crash path and could schedule an auto-restart. Uses the first
 * service that is available on the current host and can be started cleanly.
 */
import { scenario, api, assert, SkipError, sleep, waitFor } from '../harness.mjs'

const CANDIDATES = ['caddy', 'mailpit', 'redis']

function parseServiceState(body) {
  return body?.state ?? body?.status ?? null
}

export default scenario('16', 'Manual stop stays stopped', 'P1', async (ctx) => {
  const servicesRes = await api.get('/api/services')
  assert.statusOk(servicesRes, 'GET /api/services')
  const services = Array.isArray(servicesRes.body) ? servicesRes.body : (servicesRes.body.services ?? [])

  const target = CANDIDATES
    .map((id) => services.find((svc) => (svc.id ?? '').toLowerCase() === id))
    .find(Boolean)

  if (!target) throw new SkipError('no supported service candidate exposed')

  const serviceId = target.id
  const originalState = parseServiceState(target)
  if (originalState === 5) throw new SkipError(`${serviceId} is disabled on this host`)

  ctx.cleanup(async () => {
    if (originalState === 2) {
      await api.post(`/api/services/${serviceId}/start`).catch(() => {})
    } else {
      await api.post(`/api/services/${serviceId}/stop`).catch(() => {})
    }
  })

  await api.post(`/api/services/${serviceId}/stop`).catch(() => {})
  await sleep(250)

  const start = await api.post(`/api/services/${serviceId}/start`)
  if (start.status >= 400) {
    const err = typeof start.body === 'string' ? start.body : JSON.stringify(start.body ?? '')
    if (err.toLowerCase().includes('not installed') || err.toLowerCase().includes('executable not found')) {
      throw new SkipError(`${serviceId} binary not installed on this host`)
    }
  }
  assert.statusOk(start, `POST /api/services/${serviceId}/start`)

  await waitFor(async () => {
    const running = await api.get(`/api/services/${serviceId}`)
    return parseServiceState(running.body) === 2 ? running : null
  }, { timeoutMs: 5000, intervalMs: 200, label: `${serviceId} running` })

  const stop = await api.post(`/api/services/${serviceId}/stop`)
  assert.statusOk(stop, `POST /api/services/${serviceId}/stop`)

  await waitFor(async () => {
    const stopped = await api.get(`/api/services/${serviceId}`)
    const state = parseServiceState(stopped.body)
    return state === 0 ? stopped : null
  }, { timeoutMs: 5000, intervalMs: 200, label: `${serviceId} stopped` })

  await sleep(1500)
  const final = await api.get(`/api/services/${serviceId}`)
  assert.statusOk(final, `GET /api/services/${serviceId} after stop settle`)
  assert.eq(parseServiceState(final.body), 0, `${serviceId} remains stopped after manual stop`)
})
