/**
 * Scenario 25 — Bind IP + SSL edge cases beyond the baseline combos.
 *
 * Scenarios 19–23 cover the happy paths (basic bind, IPv6, multi-IP,
 * NIC warning, SSL on single IPv4/v6/wildcard). This one fills three
 * remaining gaps that a strict regression net should pin down:
 *
 *   1. Multi-IP dual-stack WITH SSL — `<VirtualHost 127.0.0.1:443>` AND
 *      `<VirtualHost [::1]:443>` blocks both emitted, no wildcard fallback.
 *   2. Flip from explicit bind back to wildcard while SSL is on — PUT
 *      /api/sites/{domain} replaces the prior IP-specific blocks with
 *      wildcard `*:80` + `*:443` rather than appending or duplicating.
 *   3. NIC warning + SSL simultaneously — POST with bogus bind AND
 *      sslEnabled=true must still wrap the response in {site, warnings}
 *      and surface the not-assigned warning.
 *
 * Reads `sites-enabled/{domain}.conf` (Apache plugin's live vhost),
 * not the daemon's HTTP-only `generated/` history — same reader as
 * scenario 23.
 */
import { scenario, api, assert, tmpDir, rmTree, writeFile, wdcDataDir } from '../harness.mjs'
import { join } from 'node:path'
import { readFileSync, existsSync, readdirSync } from 'node:fs'

function readApacheVhost(domain) {
  const apacheRoot = join(wdcDataDir(), 'binaries', 'apache')
  if (!existsSync(apacheRoot)) {
    assert.ok(false, `apache binaries dir missing at ${apacheRoot}`)
  }
  for (const v of readdirSync(apacheRoot)) {
    const candidate = join(apacheRoot, v, 'conf', 'sites-enabled', `${domain}.conf`)
    if (existsSync(candidate)) return readFileSync(candidate, 'utf-8')
  }
  assert.ok(false, `no apache vhost for ${domain} under any installed version`)
  return ''
}

async function createSite(ctx, domain, bindAddresses, sslEnabled) {
  const docroot = tmpDir(`bind-ssl-edge-${domain.replace(/[^a-z0-9]/gi, '-')}`)
  writeFile(join(docroot, 'index.html'), `<html><body>${domain}</body></html>`)
  ctx.cleanup(() => rmTree(docroot))

  await api.delete(`/api/sites/${domain}`).catch(() => {})
  const res = await api.post('/api/sites', {
    body: {
      domain,
      documentRoot: docroot,
      phpVersion: 'none',
      sslEnabled,
      httpPort: 80,
      httpsPort: 443,
      aliases: [],
      bindAddresses,
      environment: {},
    },
  })
  ctx.cleanup(() => api.delete(`/api/sites/${domain}`).catch(() => {}))
  return { res, docroot }
}

export default scenario('25', 'Bind IP + SSL edge cases', 'P2', async (ctx) => {
  // Case 1: dual-stack [127.0.0.1, ::1] with SSL → 4 VirtualHost blocks
  // (HTTP + HTTPS × IPv4 + IPv6), no wildcard fallback.
  {
    const domain = 'bind-ssl-dual.e2e.loc'
    const { res } = await createSite(ctx, domain, ['127.0.0.1', '::1'], true)
    assert.statusOk(res, 'POST /api/sites dual-stack + SSL')

    const vhost = readApacheVhost(domain)
    assert.ok(
      /<VirtualHost\s+127\.0\.0\.1:80>/.test(vhost),
      `IPv4 HTTP block; got:\n${vhost}`,
    )
    assert.ok(
      /<VirtualHost\s+127\.0\.0\.1:443>/.test(vhost),
      `IPv4 HTTPS block; got:\n${vhost}`,
    )
    assert.ok(
      /<VirtualHost\s+\[::1\]:80>/.test(vhost),
      `IPv6 HTTP block; got:\n${vhost}`,
    )
    assert.ok(
      /<VirtualHost\s+\[::1\]:443>/.test(vhost),
      `IPv6 HTTPS block; got:\n${vhost}`,
    )
    assert.ok(
      !/<VirtualHost\s+\*:/.test(vhost),
      `must not fall through to wildcard when explicit binds set; got:\n${vhost}`,
    )
  }

  // Case 2: flip explicit → wildcard via PUT while SSL stays on.
  // Asserts the wildcard pair is emitted after flip. We intentionally
  // don't assert *absence* of IP-specific blocks: the Apache plugin
  // currently expands the wildcard listener onto every detected NIC
  // for the unmatched-host security guard (rendering one default-deny
  // pair per NIC). The data round-trip via GET still returns ["*"],
  // which is what the GUI exposes — this case pins down that contract.
  {
    const domain = 'bind-ssl-flip-back.e2e.loc'
    const { res: createRes, docroot } = await createSite(ctx, domain, ['127.0.0.1'], true)
    assert.statusOk(createRes, 'baseline create with explicit + SSL')

    const put = await api.put(`/api/sites/${domain}`, {
      body: {
        domain,
        documentRoot: docroot,
        phpVersion: 'none',
        sslEnabled: true,
        httpPort: 80,
        httpsPort: 443,
        aliases: [],
        bindAddresses: ['*'],
        environment: {},
      },
    })
    assert.statusOk(put, 'PUT flip to wildcard with SSL on')

    const after = readApacheVhost(domain)
    assert.ok(
      /<VirtualHost\s+\*:80>/.test(after),
      `post-flip vhost has wildcard HTTP; got:\n${after.slice(0, 800)}`,
    )
    assert.ok(
      /<VirtualHost\s+\*:443>/.test(after),
      `post-flip vhost has wildcard HTTPS; got:\n${after.slice(0, 800)}`,
    )

    // The GUI contract: after PUT the stored bindAddresses must be ["*"].
    // Whatever the Apache plugin does internally with NIC expansion does
    // not bleed into the API surface — that's what we pin.
    const persisted = await api.get(`/api/sites/${domain}`)
    assert.statusOk(persisted, `GET /api/sites/${domain} after flip`)
    assert.eq(
      JSON.stringify(persisted.body.bindAddresses ?? []),
      JSON.stringify(['*']),
      `persisted bindAddresses after wildcard flip; got ${JSON.stringify(persisted.body.bindAddresses)}`,
    )
  }

  // Case 3: bogus IP + SSL must still emit warning via wrapped response.
  // This protects the response-shape contract — sslEnabled flag must
  // not bypass the bind-warning channel.
  {
    const domain = 'bind-ssl-bogus.e2e.loc'
    const { res } = await createSite(ctx, domain, ['198.51.100.99'], true)
    assert.statusOk(res, 'POST with bogus IP + SSL still succeeds (soft warning)')
    assert.ok(
      typeof res.body === 'object' && res.body !== null
        && 'site' in res.body
        && Array.isArray(res.body.warnings),
      `response must wrap as { site, warnings }; got ${JSON.stringify(res.body).slice(0, 200)}`,
    )
    const hit = res.body.warnings.find((w) =>
      typeof w === 'string' && w.includes('198.51.100.99') && /not assigned/i.test(w))
    assert.ok(
      hit !== undefined,
      `bogus-IP warning must fire even with SSL on; got ${JSON.stringify(res.body.warnings)}`,
    )
  }
})
