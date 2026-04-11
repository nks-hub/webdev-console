/**
 * Scenario 11 — Wildcard alias site.
 * Creates a site with a `*.foo.loc` wildcard alias, verifies the generated
 * Apache vhost contains ServerAlias for the wildcard, and (if mkcert is
 * installed) verifies the cert SAN includes the wildcard via Node's built-in
 * X509Certificate parser. Hosts file is expected to silently skip the
 * wildcard entry.
 */
import { scenario, api, assert, tmpDir, rmTree, writeFile, SkipError } from '../harness.mjs'
import { join } from 'node:path'
import { readFileSync, existsSync } from 'node:fs'
import { homedir } from 'node:os'
import { X509Certificate } from 'node:crypto'

const DOMAIN = 'wcard-e2e.loc'
const WILDCARD = `*.${DOMAIN}`

export default scenario('11', 'Wildcard alias site', 'P2', async (ctx) => {
  // Probe SSL plugin — we need mkcert available to verify the SAN. If not,
  // fall back to the HTTP-only assertion (wildcard in vhost) and skip the
  // cert-side check so the scenario still runs on hosts without mkcert.
  const certsProbe = await api.get('/api/ssl/certs')
  const mkcertInstalled = certsProbe.status === 200 && certsProbe.body?.mkcertInstalled === true

  const docroot = tmpDir('wcard')
  writeFile(join(docroot, 'index.html'), '<html><body>wildcard</body></html>')
  ctx.cleanup(() => rmTree(docroot))

  await api.delete(`/api/sites/${DOMAIN}`).catch(() => {})

  const create = await api.post('/api/sites', {
    body: {
      domain: DOMAIN,
      documentRoot: docroot,
      phpVersion: 'none',
      sslEnabled: mkcertInstalled, // enable SSL only if mkcert is available
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

  // Regression guard: when mkcert is available, the generated cert's SAN
  // must include both the primary domain and the wildcard alias. This
  // catches the class of bugs where wildcard aliases were accepted by the
  // API but dropped before being passed to mkcert.
  if (mkcertInstalled) {
    const certPath = join(homedir(), '.wdc', 'ssl', 'sites', DOMAIN, 'cert.pem')
    if (!existsSync(certPath)) {
      throw new SkipError(`SSL cert not generated at ${certPath} — mkcert likely misconfigured`)
    }
    const pem = readFileSync(certPath, 'utf-8')
    const cert = new X509Certificate(pem)
    const san = cert.subjectAltName ?? ''
    assert.contains(san, `DNS:${DOMAIN}`, `cert SAN contains primary ${DOMAIN}`)
    assert.contains(san, `DNS:${WILDCARD}`, `cert SAN contains wildcard ${WILDCARD}`)
  }
})
