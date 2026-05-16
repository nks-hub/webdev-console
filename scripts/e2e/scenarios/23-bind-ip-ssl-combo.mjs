/**
 * Scenario 23 — Bind IP combined with SSL.
 *
 * Asserts the vhost renderer emits *both* the HTTP and HTTPS VirtualHost
 * blocks bound to the same explicit IP. A regression here would mean
 * either:
 *   - HTTPS block falls through to "*" while HTTP is bound to 127.0.0.1,
 *     producing a port-mismatched config Apache refuses to start.
 *   - HTTPS block is omitted entirely when sslEnabled=true + a non-wildcard
 *     bind is set.
 *
 * The Scriban template iterates `bind_addresses` for both HTTP and HTTPS
 * sections, so the correct shape is exactly two `<VirtualHost IP:80>` /
 * `<VirtualHost IP:443>` pairs per bind address.
 */
import { scenario, api, assert, tmpDir, rmTree, writeFile, wdcDataDir } from '../harness.mjs'
import { join } from 'node:path'
import { readFileSync, existsSync, readdirSync } from 'node:fs'

// Locate the Apache plugin's live vhost, not the daemon's history copy
// under `generated/`. The history copy is HTTP-only; the SSL block is
// emitted by the Apache plugin into `sites-enabled/`.
function readApacheVhost(domain) {
  const apacheRoot = join(wdcDataDir(), 'binaries', 'apache')
  if (!existsSync(apacheRoot)) {
    assert.ok(false, `apache binaries dir missing at ${apacheRoot}`)
  }
  const versions = readdirSync(apacheRoot)
  for (const v of versions) {
    const candidate = join(apacheRoot, v, 'conf', 'sites-enabled', `${domain}.conf`)
    if (existsSync(candidate)) return readFileSync(candidate, 'utf-8')
  }
  assert.ok(false, `no apache vhost for ${domain} under any installed version`)
  return ''
}

async function createSslBindSite(ctx, domain, bindAddresses) {
  const docroot = tmpDir(`bind-ssl-${domain.replace(/[^a-z0-9]/gi, '-')}`)
  writeFile(join(docroot, 'index.html'), `<html><body>${domain}</body></html>`)
  ctx.cleanup(() => rmTree(docroot))

  await api.delete(`/api/sites/${domain}`).catch(() => {})
  const res = await api.post('/api/sites', {
    body: {
      domain,
      documentRoot: docroot,
      phpVersion: 'none',
      sslEnabled: true,
      httpPort: 80,
      httpsPort: 443,
      aliases: [],
      bindAddresses,
      environment: {},
    },
  })
  ctx.cleanup(() => api.delete(`/api/sites/${domain}`).catch(() => {}))
  return res
}

export default scenario('23', 'Bind IP + SSL combo', 'P2', async (ctx) => {
  // Case 1: loopback bind + SSL → both 127.0.0.1:80 and 127.0.0.1:443.
  {
    const domain = 'bind-ssl-loop.e2e.loc'
    const res = await createSslBindSite(ctx, domain, ['127.0.0.1'])
    assert.statusOk(res, 'POST /api/sites with SSL + loopback bind')

    const vhost = readApacheVhost(domain)
    assert.ok(
      /<VirtualHost\s+127\.0\.0\.1:80>/.test(vhost),
      `HTTP block bound to 127.0.0.1:80; got:\n${vhost}`,
    )
    assert.ok(
      /<VirtualHost\s+127\.0\.0\.1:443>/.test(vhost),
      `HTTPS block bound to 127.0.0.1:443; got:\n${vhost}`,
    )
    // Negative: no fall-through `*:80` or `*:443` blocks when the
    // operator picked a specific bind. (Localhost auto-aliases are
    // a separate code path and only fire for Domain="localhost".)
    assert.ok(
      !/<VirtualHost\s+\*:80>/.test(vhost),
      `must not fall through to wildcard HTTP block; got:\n${vhost}`,
    )
    assert.ok(
      !/<VirtualHost\s+\*:443>/.test(vhost),
      `must not fall through to wildcard HTTPS block; got:\n${vhost}`,
    )
  }

  // Case 2: IPv6 loopback + SSL → bracketed form on both ports.
  {
    const domain = 'bind-ssl-v6.e2e.loc'
    const res = await createSslBindSite(ctx, domain, ['::1'])
    assert.statusOk(res, 'POST /api/sites with SSL + IPv6 loopback bind')

    const vhost = readApacheVhost(domain)
    assert.ok(
      /<VirtualHost\s+\[::1\]:80>/.test(vhost),
      `HTTP block bound to [::1]:80; got:\n${vhost}`,
    )
    assert.ok(
      /<VirtualHost\s+\[::1\]:443>/.test(vhost),
      `HTTPS block bound to [::1]:443; got:\n${vhost}`,
    )
  }

  // Case 3: wildcard bind + SSL → wildcard on both. Baseline coverage so
  // a future refactor that special-cases SSL doesn't break the default.
  {
    const domain = 'bind-ssl-wild.e2e.loc'
    const res = await createSslBindSite(ctx, domain, ['*'])
    assert.statusOk(res, 'POST /api/sites with SSL + wildcard bind')

    const vhost = readApacheVhost(domain)
    assert.ok(
      /<VirtualHost\s+\*:80>/.test(vhost),
      `wildcard HTTP block emitted; got:\n${vhost}`,
    )
    assert.ok(
      /<VirtualHost\s+\*:443>/.test(vhost),
      `wildcard HTTPS block emitted; got:\n${vhost}`,
    )
  }
})
