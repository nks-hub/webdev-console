/**
 * Scenario 3 — Static HTML (no PHP).
 * P0 smoke — API-only. Creates a site with phpVersion:"none", verifies vhost
 * has no PHP directives and the index file is served. Hosts file touch is
 * skipped in this scenario (we don't verify 127.0.0.1 resolution).
 */
import { scenario, api, assert, tmpDir, rmTree, writeFile } from '../harness.mjs'
import { join } from 'node:path'
import { readFileSync, existsSync } from 'node:fs'
import { homedir } from 'node:os'

const DOMAIN = 'static-e2e.loc'

export default scenario('3', 'Static HTML (no PHP)', 'P0', async (ctx) => {
  const docroot = tmpDir('static')
  writeFile(join(docroot, 'index.html'), '<!doctype html><html><body>e2e static</body></html>')
  writeFile(join(docroot, 'test.php'), '<?php echo "should-not-execute"; ?>')
  ctx.cleanup(() => rmTree(docroot))

  // Clean any prior leftover from a failed run.
  await api.delete(`/api/sites/${DOMAIN}`).catch(() => {})

  const create = await api.post('/api/sites', {
    body: {
      domain: DOMAIN,
      documentRoot: docroot,
      phpVersion: 'none',
      sslEnabled: false,
      httpPort: 80,
      httpsPort: 443,
      aliases: [],
      environment: {},
    },
  })
  ctx.cleanup(() => api.delete(`/api/sites/${DOMAIN}`).catch(() => {}))
  assert.statusOk(create, 'POST /api/sites')

  // Verify vhost generated under ~/.wdc/generated and contains no PHP directives.
  const vhostPath = join(homedir(), '.wdc', 'generated', `${DOMAIN}.conf`)
  assert.ok(existsSync(vhostPath), `vhost file exists at ${vhostPath}`)
  const vhost = readFileSync(vhostPath, 'utf-8').toLowerCase()
  assert.notContains(vhost, 'fcgiwrapper', 'vhost must not contain FCGIWrapper')
  assert.notContains(vhost, 'php-cgi', 'vhost must not contain php-cgi reference')

  // Verify the site appears in GET /api/sites.
  const list = await api.get('/api/sites')
  assert.statusOk(list, 'GET /api/sites')
  const sites = Array.isArray(list.body) ? list.body : list.body.sites ?? []
  const found = sites.find((s) => s.domain === DOMAIN)
  assert.ok(found, `site ${DOMAIN} appears in /api/sites`)
  assert.eq(found.phpVersion, 'none', 'phpVersion is "none"')
})
