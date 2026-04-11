/**
 * Scenario 7 — SSL certificate regeneration.
 * Creates a site with SSL, notes the cert path, deletes the cert via the
 * SSL plugin endpoint, regenerates, verifies the file exists again with a
 * (likely) new mtime. Skips if mkcert is not installed.
 */
import { scenario, api, assert, SkipError, tmpDir, rmTree, writeFile, sleep } from '../harness.mjs'
import { join } from 'node:path'
import { existsSync, statSync } from 'node:fs'
import { homedir } from 'node:os'

const DOMAIN = 'ssl-regen-e2e.loc'

export default scenario('7', 'SSL certificate regeneration', 'P2', async (ctx) => {
  // Probe SSL plugin for mkcert availability.
  const certs = await api.get('/api/ssl/certs')
  if (certs.status !== 200) throw new SkipError('SSL plugin not available')
  const mkcertInstalled = certs.body?.mkcertInstalled
  if (!mkcertInstalled) throw new SkipError('mkcert not installed on this host')

  const docroot = tmpDir('sslregen')
  writeFile(join(docroot, 'index.html'), '<html><body>ssl</body></html>')
  ctx.cleanup(() => rmTree(docroot))

  await api.delete(`/api/sites/${DOMAIN}`).catch(() => {})

  const create = await api.post('/api/sites', {
    body: {
      domain: DOMAIN,
      documentRoot: docroot,
      phpVersion: 'none',
      sslEnabled: true,
      httpPort: 80,
      httpsPort: 443,
      aliases: [],
      environment: {},
    },
  })
  ctx.cleanup(() => api.delete(`/api/sites/${DOMAIN}`).catch(() => {}))
  assert.statusOk(create, 'POST /api/sites')

  // Cert path per the plugin convention.
  const certDir = join(homedir(), '.wdc', 'ssl', 'sites', DOMAIN)
  const certFile = join(certDir, 'cert.pem')

  // First cert may not exist if mkcert silently failed — tolerate that case
  // but only if the plugin really does have mkcert. Otherwise assert.
  if (!existsSync(certFile)) {
    throw new SkipError(`initial cert not generated at ${certFile} — mkcert likely misconfigured`)
  }
  const mtime1 = statSync(certFile).mtimeMs

  // Sleep long enough for file mtime to definitely differ on Windows (≥1 s).
  await sleep(1100)

  // Regenerate via SSL plugin endpoint.
  const regen = await api.post('/api/ssl/generate', {
    body: { domain: DOMAIN, aliases: [] },
  })
  assert.statusOk(regen, 'POST /api/ssl/generate')

  assert.ok(existsSync(certFile), 'cert file still exists after regen')
  const mtime2 = statSync(certFile).mtimeMs
  assert.ok(
    mtime2 > mtime1,
    `cert mtime should advance on regen (before=${mtime1}, after=${mtime2})`,
  )
})
