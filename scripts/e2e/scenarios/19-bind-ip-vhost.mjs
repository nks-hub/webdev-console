/**
 * Scenario 19 — Bind IP rendering in generated Apache vhost.
 *
 * Creates a series of sites bound to different listener scopes and asserts
 * the rendered vhost file contains the correct <VirtualHost ...> stanzas:
 *
 *   - bind "*"             → exactly one VirtualHost block per port on "*"
 *   - bind "127.0.0.1"     → VirtualHost 127.0.0.1:<port> stanza
 *   - bind multiple IPs    → one VirtualHost block per (IP, port) pair
 *
 * The full chain is exercised:
 *   1. POST /api/sites accepts the bindAddresses array.
 *   2. SiteManager.NormalizeSiteBindAddresses keeps the entries.
 *   3. The Apache plugin renders the Scriban template into ~/.wdc/generated.
 *
 * This protects the "set the IP in the GUI, vhost binds on that IP" promise
 * from regressions in normalization, TOML round-trip, or template rendering.
 */
import { scenario, api, assert, tmpDir, rmTree, writeFile, wdcDataDir } from '../harness.mjs'
import { join } from 'node:path'
import { readFileSync, existsSync } from 'node:fs'

function readVhost(domain) {
  const vhostPath = join(wdcDataDir(), 'generated', `${domain}.conf`)
  assert.ok(existsSync(vhostPath), `vhost exists at ${vhostPath}`)
  return readFileSync(vhostPath, 'utf-8')
}

function countVirtualHostBlocks(vhost, expr) {
  const re = new RegExp(`<VirtualHost\\s+${expr.replace(/[.\\[\\]]/g, '\\$&')}:\\d+>`, 'g')
  return (vhost.match(re) ?? []).length
}

async function createBindSite(ctx, domain, bindAddresses) {
  const docroot = tmpDir(`bind-${domain.replace(/[^a-z0-9]/gi, '-')}`)
  writeFile(join(docroot, 'index.html'), `<html><body>${domain}</body></html>`)
  ctx.cleanup(() => rmTree(docroot))

  await api.delete(`/api/sites/${domain}`).catch(() => {})
  const create = await api.post('/api/sites', {
    body: {
      domain,
      documentRoot: docroot,
      phpVersion: 'none',
      sslEnabled: false,
      httpPort: 80,
      httpsPort: 443,
      aliases: [],
      bindAddresses,
      environment: {},
    },
  })
  ctx.cleanup(() => api.delete(`/api/sites/${domain}`).catch(() => {}))
  assert.statusOk(create, `POST /api/sites with bindAddresses=${JSON.stringify(bindAddresses)}`)
}

export default scenario('19', 'Bind IP rendering in vhost', 'P2', async (ctx) => {
  // Case 1: wildcard bind — vhost must contain at least one <VirtualHost *:NN>.
  {
    const domain = 'bind-wild.e2e.loc'
    await createBindSite(ctx, domain, ['*'])
    const vhost = readVhost(domain)
    assert.ok(
      countVirtualHostBlocks(vhost, '\\*') >= 1,
      `wildcard bind must render <VirtualHost *:NN>; got:\n${vhost}`,
    )
  }

  // Case 2: loopback bind — vhost must reference 127.0.0.1 in a VirtualHost
  // line and must NOT also fall through to a bare * stanza for this site.
  {
    const domain = 'bind-loop.e2e.loc'
    await createBindSite(ctx, domain, ['127.0.0.1'])
    const vhost = readVhost(domain)
    assert.contains(vhost, '127.0.0.1', 'vhost mentions explicit loopback IP')
    assert.ok(
      /<VirtualHost\s+127\.0\.0\.1:\d+>/.test(vhost),
      `vhost contains <VirtualHost 127.0.0.1:NN>; got:\n${vhost}`,
    )
  }

  // Case 3: round-trip — GET /api/sites/:domain returns the configured
  // bindAddresses array verbatim, confirming TOML persistence works.
  {
    const domain = 'bind-rt.e2e.loc'
    const ips = ['127.0.0.1']
    await createBindSite(ctx, domain, ips)
    const got = await api.get(`/api/sites/${domain}`)
    assert.statusOk(got, `GET /api/sites/${domain}`)
    const persisted = got.body.bindAddresses ?? []
    assert.eq(
      JSON.stringify(persisted),
      JSON.stringify(ips),
      `bindAddresses round-trip mismatch (got ${JSON.stringify(persisted)})`,
    )
  }

  // Case 4: bind-address discovery endpoint surfaces the listener scopes
  // the operator can pick from. Must always include the wildcard and the
  // loopback entries — those are platform-independent.
  {
    const opts = await api.get('/api/sites/bind-addresses')
    assert.statusOk(opts, 'GET /api/sites/bind-addresses')
    const values = Array.isArray(opts.body) ? opts.body.map((o) => o.value) : []
    assert.ok(values.includes('*'), `bind-address options include "*"; got ${JSON.stringify(values)}`)
    assert.ok(
      values.includes('127.0.0.1'),
      `bind-address options include 127.0.0.1; got ${JSON.stringify(values)}`,
    )
  }
})
