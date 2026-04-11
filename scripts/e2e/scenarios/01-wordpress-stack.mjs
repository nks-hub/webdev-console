/**
 * Scenario 1 — WordPress site on the full stack.
 *
 * Creates a temp docroot containing a minimal wp-config.php so the daemon's
 * framework auto-detector returns "wordpress", then provisions the site with
 * PHP + SSL, verifies:
 *   - POST /api/sites returns 201
 *   - detect-framework → "wordpress"
 *   - generated vhost references the chosen PHP version
 *   - vhost has HTTPS block if mkcert is available
 *   - GET /api/sites/:domain round-trips framework + phpVersion
 *
 * Skips if no PHP version is installed.
 */
import { scenario, api, assert, SkipError, tmpDir, rmTree, writeFile } from '../harness.mjs'
import { join } from 'node:path'
import { readFileSync, existsSync } from 'node:fs'
import { homedir } from 'node:os'

const DOMAIN = 'wordpress-e2e.loc'

export default scenario('1', 'WordPress stack (PHP + SSL)', 'P1', async (ctx) => {
  // Discover a PHP version to target.
  const vers = await api.get('/api/php/versions')
  if (vers.status !== 200 || !Array.isArray(vers.body) || vers.body.length === 0) {
    throw new SkipError('no PHP versions installed')
  }
  const phpVer = vers.body[0].majorMinor ?? vers.body[0].version

  // Check mkcert for SSL.
  const certs = await api.get('/api/ssl/certs')
  const sslAvailable = certs.status === 200 && certs.body?.mkcertInstalled === true

  const docroot = tmpDir('wordpress')
  writeFile(
    join(docroot, 'wp-config.php'),
    "<?php\n// WordPress config marker for e2e framework detection.\ndefine('DB_NAME', 'wp_e2e');\ndefine('DB_USER', 'root');\ndefine('DB_PASSWORD', '');\ndefine('DB_HOST', 'localhost');\n",
  )
  writeFile(join(docroot, 'index.php'), '<?php echo "WordPress bootstrap";')
  ctx.cleanup(() => rmTree(docroot))

  await api.delete(`/api/sites/${DOMAIN}`).catch(() => {})

  const create = await api.post('/api/sites', {
    body: {
      domain: DOMAIN,
      documentRoot: docroot,
      phpVersion: phpVer,
      sslEnabled: sslAvailable,
      httpPort: 80,
      httpsPort: 443,
      aliases: [],
      environment: {},
    },
  })
  ctx.cleanup(() => api.delete(`/api/sites/${DOMAIN}`).catch(() => {}))
  assert.statusOk(create, 'POST /api/sites (WordPress)')

  // Framework detection.
  const detect = await api.post(`/api/sites/${DOMAIN}/detect-framework`)
  assert.statusOk(detect, 'detect-framework')
  assert.eq(detect.body?.framework, 'wordpress', 'detected framework is wordpress')

  // Vhost references the requested PHP version.
  const vhostPath = join(homedir(), '.wdc', 'generated', `${DOMAIN}.conf`)
  assert.ok(existsSync(vhostPath), `vhost exists at ${vhostPath}`)
  const vhost = readFileSync(vhostPath, 'utf-8')
  assert.contains(vhost, phpVer, `vhost references PHP ${phpVer}`)

  // When SSL is available the vhost must include an HTTPS VirtualHost block.
  if (sslAvailable) {
    assert.contains(vhost, '*:443', 'vhost has HTTPS block on :443')
    assert.contains(vhost, 'SSLCertificateFile', 'vhost has SSLCertificateFile directive')
  }

  // REST round-trip.
  const got = await api.get(`/api/sites/${DOMAIN}`)
  assert.statusOk(got, `GET /api/sites/${DOMAIN}`)
  assert.eq(got.body.phpVersion, phpVer, 'phpVersion round-trip')
})
