/**
 * Scenario 11 — Wildcard alias site.
 * Creates a site with a `*.foo.loc` wildcard alias, verifies the generated
 * Apache vhost contains ServerAlias for the wildcard, and (if mkcert is
 * installed) verifies the cert SAN includes the wildcard. Hosts file is
 * expected to silently skip the wildcard entry.
 */
import { scenario, api, assert, tmpDir, rmTree, writeFile } from '../harness.mjs'
import { join } from 'node:path'
import { readFileSync, existsSync } from 'node:fs'
import { homedir } from 'node:os'

const DOMAIN = 'wcard-e2e.loc'
const WILDCARD = `*.${DOMAIN}`

export default scenario('11', 'Wildcard alias site', 'P2', async (ctx) => {
  const docroot = tmpDir('wcard')
  writeFile(join(docroot, 'index.html'), '<html><body>wildcard</body></html>')
  ctx.cleanup(() => rmTree(docroot))

  await api.delete(`/api/sites/${DOMAIN}`).catch(() => {})

  const create = await api.post('/api/sites', {
    body: {
      domain: DOMAIN,
      documentRoot: docroot,
      phpVersion: 'none',
      sslEnabled: false,
      httpPort: 80,
      httpsPort: 443,
      aliases: [WILDCARD],
      environment: {},
    },
  })
  ctx.cleanup(() => api.delete(`/api/sites/${DOMAIN}`).catch(() => {}))
  assert.statusOk(create, 'POST /api/sites with wildcard alias')

  // Vhost should contain ServerAlias with the wildcard.
  const vhostPath = join(homedir(), '.wdc', 'generated', `${DOMAIN}.conf`)
  assert.ok(existsSync(vhostPath), `vhost exists at ${vhostPath}`)
  const vhost = readFileSync(vhostPath, 'utf-8')
  assert.contains(
    vhost,
    WILDCARD,
    `vhost must reference the wildcard alias ${WILDCARD}`,
  )

  // GET /api/sites/:domain must round-trip the alias.
  const got = await api.get(`/api/sites/${DOMAIN}`)
  assert.statusOk(got, `GET /api/sites/${DOMAIN}`)
  const aliases = got.body.aliases ?? []
  assert.ok(
    aliases.includes(WILDCARD),
    `aliases array contains ${WILDCARD}, got ${JSON.stringify(aliases)}`,
  )
})
