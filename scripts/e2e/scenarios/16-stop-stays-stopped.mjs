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

  // Walk the candidate list and pick the first one that (a) is exposed by
  // the daemon, (b) isn't disabled, and (c) actually starts without a
  // "not installed" error. This lets hosts missing Caddy fall through to
  // Mailpit or Redis instead of the whole scenario skipping.
  let serviceId = null
  let originalState = null
  let startResponse = null
  for (const candidateId of CANDIDATES) {
    const candidate = services.find((svc) => (svc.id ?? '').toLowerCase() === candidateId)
    if (!candidate) continue
    const state = parseServiceState(candidate)
    if (state === 5) continue // disabled on this host

    // Try to start it — if the binary is missing we get a 500 with a
    // specific marker and we move on to the next candidate silently.
    await api.post(`/api/services/${candidateId}/stop`).catch(() => {})
    await sleep(250)
    const attempt = await api.post(`/api/services/${candidateId}/start`)
    if (attempt.status >= 400) {
      const err = typeof attempt.body === 'string' ? attempt.body : JSON.stringify(attempt.body ?? '')
      if (err.toLowerCase().includes('not installed') || err.toLowerCase().includes('executable not found')) {
        continue // try next candidate
      }
      throw new Error(`Unexpected failure starting ${candidateId}: HTTP ${attempt.status} ${err.slice(0, 200)}`)
    }
    serviceId = candidateId
    originalState = state
    startResponse = attempt
    break
  }

  if (!serviceId) throw new SkipError('no candidate service could be started on this host')

  ctx.cleanup(async () => {
    if (originalState === 2) {
      await api.post(`/api/services/${serviceId}/start`).catch(() => {})
    } else {
      await api.post(`/api/services/${serviceId}/stop`).catch(() => {})
    }
  })

  assert.statusOk(startResponse, `POST /api/services/${serviceId}/start`)

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
