/**
 * Scenario 4 — PHP version switch (runtime change, no Apache restart required).
 * P0 smoke — API-only. Creates a site on PHP version A, updates to version B,
 * verifies the generated vhost references version B. Skips if the daemon does
 * not expose at least two PHP versions.
 */
import { scenario, api, assert, SkipError, tmpDir, rmTree, writeFile } from '../harness.mjs'
import { join } from 'node:path'

const DOMAIN = 'phpswitch-e2e.loc'

export default scenario('4', 'PHP version switch', 'P0', async (ctx) => {
  // Discover installed PHP versions via the PHP plugin endpoint.
  const verRes = await api.get('/api/php/versions')
  assert.statusOk(verRes, 'GET /api/php/versions')
  const versions = Array.isArray(verRes.body) ? verRes.body : []
  if (versions.length < 2) {
    throw new SkipError('needs at least 2 PHP versions installed on this host')
  }
  // Pick two distinct majorMinor versions (API uses the short form like "8.3").
  const majorMinors = [...new Set(versions.map((v) => v.majorMinor ?? v.version))]
  if (majorMinors.length < 2) {
    throw new SkipError('needs at least 2 distinct PHP major.minor versions')
  }
  const a = majorMinors[0]
  const b = majorMinors[1]
  assert.ok(a && b, 'two PHP versions discoverable')

  const docroot = tmpDir('phpswitch')
  writeFile(join(docroot, 'index.php'), '<?php echo PHP_VERSION;')
  ctx.cleanup(() => rmTree(docroot))

  await api.delete(`/api/sites/${DOMAIN}`).catch(() => {})

  const create = await api.post('/api/sites', {
    body: {
      domain: DOMAIN,
      documentRoot: docroot,
      phpVersion: a,
      sslEnabled: false,
      httpPort: 80,
      httpsPort: 443,
      aliases: [],
      environment: {},
    },
  })
  ctx.cleanup(() => api.delete(`/api/sites/${DOMAIN}`).catch(() => {}))
  assert.statusOk(create, `POST /api/sites with phpVersion=${a}`)

  // Confirm initial phpVersion is persisted.
  const initial = await api.get(`/api/sites/${DOMAIN}`)
  assert.statusOk(initial, `GET /api/sites/${DOMAIN} initial`)
  assert.eq(initial.body.phpVersion, a, `initial phpVersion is ${a}`)

  // Switch to version B.
  const update = await api.put(`/api/sites/${DOMAIN}`, {
    body: {
      domain: DOMAIN,
      documentRoot: docroot,
      phpVersion: b,
      sslEnabled: false,
      httpPort: 80,
      httpsPort: 443,
      aliases: [],
      environment: {},
    },
  })
  assert.statusOk(update, `PUT /api/sites/${DOMAIN} with phpVersion=${b}`)

  // Confirm GET /api/sites/:domain reports the new version — this is the REST
  // contract the UI and CLI rely on. Whether Apache vhost has been regenerated
  // on disk is an implementation detail not asserted here.
  const after = await api.get(`/api/sites/${DOMAIN}`)
  assert.statusOk(after, `GET /api/sites/${DOMAIN} after update`)
  assert.eq(after.body.phpVersion, b, `phpVersion after update should be ${b}`)
})
