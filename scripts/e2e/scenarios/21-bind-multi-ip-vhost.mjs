/**
 * Scenario 21 — Multi-IP bind rendering in generated Apache vhost.
 *
 * Asserts that listing multiple bind addresses on a single site produces
 * one <VirtualHost IP:PORT> block per (IP, port) pair, in the order the
 * operator supplied them.
 *
 * Covers two combinations:
 *   - ["127.0.0.1", "::1"] — dual-stack loopback (most common config)
 *   - ["*", "127.0.0.1"]    — must be rejected by ValidateBindAddresses
 *
 * Unit tests (SiteManagerTests.GenerateVhostAsync_UsesMultipleBindAddresses)
 * cover the rendering path; this scenario protects the API → daemon →
 * filesystem round-trip from breaking the contract.
 */
import { scenario, api, assert, tmpDir, rmTree, writeFile, wdcDataDir } from '../harness.mjs'
import { join } from 'node:path'
import { readFileSync, existsSync } from 'node:fs'

function readVhost(domain) {
  const vhostPath = join(wdcDataDir(), 'generated', `${domain}.conf`)
  assert.ok(existsSync(vhostPath), `vhost exists at ${vhostPath}`)
  return readFileSync(vhostPath, 'utf-8')
}

async function createBindSite(ctx, domain, bindAddresses) {
  const docroot = tmpDir(`bind-multi-${domain.replace(/[^a-z0-9]/gi, '-')}`)
  writeFile(join(docroot, 'index.html'), `<html><body>${domain}</body></html>`)
  ctx.cleanup(() => rmTree(docroot))

  await api.delete(`/api/sites/${domain}`).catch(() => {})
  return api.post('/api/sites', {
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
}

export default scenario('21', 'Multi-IP bind rendering in vhost', 'P2', async (ctx) => {
  // Case 1: dual-stack loopback — both blocks present, order preserved.
  {
    const domain = 'bind-dualstack.e2e.loc'
    const create = await createBindSite(ctx, domain, ['127.0.0.1', '::1'])
    ctx.cleanup(() => api.delete(`/api/sites/${domain}`).catch(() => {}))
    assert.statusOk(create, 'POST /api/sites accepts dual-stack loopback')

    const vhost = readVhost(domain)
    assert.ok(
      /<VirtualHost\s+127\.0\.0\.1:\d+>/.test(vhost),
      `vhost contains IPv4 loopback block; got:\n${vhost}`,
    )
    assert.ok(
      /<VirtualHost\s+\[::1\]:\d+>/.test(vhost),
      `vhost contains IPv6 loopback block; got:\n${vhost}`,
    )

    // Order: scriban template iterates bind_addresses; IPv4 must come first.
    const v4Idx = vhost.search(/<VirtualHost\s+127\.0\.0\.1:/)
    const v6Idx = vhost.search(/<VirtualHost\s+\[::1\]:/)
    assert.ok(v4Idx >= 0 && v6Idx >= 0, 'both blocks located')
    assert.ok(
      v4Idx < v6Idx,
      `bind_addresses order preserved (IPv4 before IPv6); got v4@${v4Idx} v6@${v6Idx}`,
    )

    // Round-trip: GET returns both addresses verbatim.
    const got = await api.get(`/api/sites/${domain}`)
    assert.statusOk(got, `GET /api/sites/${domain}`)
    const persisted = got.body.bindAddresses ?? []
    assert.eq(
      JSON.stringify(persisted),
      JSON.stringify(['127.0.0.1', '::1']),
      `multi-IP round-trip mismatch (got ${JSON.stringify(persisted)})`,
    )
  }

  // Case 2: wildcard + specific must be rejected (mixed-scope violates
  // ValidateBindAddresses contract — Apache can't sensibly bind both).
  {
    const domain = 'bind-mixed.e2e.loc'
    const create = await createBindSite(ctx, domain, ['*', '127.0.0.1'])
    ctx.cleanup(() => api.delete(`/api/sites/${domain}`).catch(() => {}))
    assert.ok(
      create.status >= 400 && create.status < 500,
      `mixed wildcard+specific must be rejected with 4xx; got ${create.status} body=${JSON.stringify(create.body)}`,
    )
  }
})
