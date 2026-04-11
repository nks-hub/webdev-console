/**
 * Scenario 6 (condensed) — Site config history persistence.
 * Original doc scenario 6 covers hosts-file backup rotation; this runner
 * version exercises the closely related concern of per-site config history
 * (Phase 6 feature): after an update to a site, the generated vhost history
 * should record the prior version.
 *
 * Promoted to P1 because the rollback button (Phase 6 UI) depends on it.
 */
import { scenario, api, assert, tmpDir, writeFile, rmTree } from '../harness.mjs'
import { join } from 'node:path'

const DOMAIN = 'histcheck-e2e.loc'

export default scenario('6', 'Site config history records updates', 'P1', async (ctx) => {
  const docroot = tmpDir('hist')
  writeFile(join(docroot, 'index.html'), '<html><body>hist</body></html>')
  ctx.cleanup(() => rmTree(docroot))

  await api.delete(`/api/sites/${DOMAIN}`).catch(() => {})

  const base = {
    domain: DOMAIN,
    documentRoot: docroot,
    phpVersion: 'none',
    sslEnabled: false,
    httpPort: 80,
    httpsPort: 443,
    aliases: [],
    environment: {},
  }

  const create = await api.post('/api/sites', { body: base })
  ctx.cleanup(() => api.delete(`/api/sites/${DOMAIN}`).catch(() => {}))
  assert.statusOk(create, 'POST /api/sites')

  // First update — change aliases list, wait a tick so history files get
  // distinct timestamps (history keys by mtime or ISO string).
  const update1 = await api.put(`/api/sites/${DOMAIN}`, {
    body: { ...base, aliases: ['www.histcheck-e2e.loc'] },
  })
  assert.statusOk(update1, 'PUT /api/sites #1')

  // Request history. Shape may be {history:[...]} or a bare array. After
  // exactly one update we expect at least one archive entry (the vhost
  // written on initial POST, now archived by AtomicWriter before the PUT
  // overwrite).
  const hist = await api.get(`/api/sites/${DOMAIN}/history`)
  assert.statusOk(hist, 'GET /api/sites/:domain/history')
  const entries = Array.isArray(hist.body) ? hist.body : (hist.body.history ?? hist.body.versions ?? [])
  assert.ok(Array.isArray(entries), 'history response is an array')
  assert.ok(entries.length >= 1, `history has at least 1 entry after update, got ${entries.length}`)

  // Endpoint also returns 404 for non-existent sites — verify that contract.
  const missing = await api.get('/api/sites/definitely-not-a-site-e2e.loc/history')
  assert.eq(missing.status, 404, 'history returns 404 for unknown domain')
})
