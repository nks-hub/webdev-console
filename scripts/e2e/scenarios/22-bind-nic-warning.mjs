/**
 * Scenario 22 — Bind-IP NIC sanity warning end-to-end.
 *
 * Asserts the warning produced by SiteManager.CollectBindAddressWarnings
 * reaches the HTTP response shape:
 *
 *   - POST /api/sites with bindAddresses=["198.51.100.99"] (RFC 5737
 *     TEST-NET-2 — guaranteed-non-routable on any production host) →
 *     201 with body shape { site, warnings, hints? } and the warnings
 *     array contains a message mentioning the bogus IP and the
 *     "not assigned" phrase.
 *
 *   - POST /api/sites with bindAddresses=["127.0.0.1"] → 201 with
 *     body shape == raw SiteConfig (no wrapper), confirming the
 *     warning channel only kicks in when there's something to warn
 *     about.
 *
 *   - PUT /api/sites/{domain} updating to a bogus IP → 200 with
 *     body shape { site, warnings } so editing reuses the same
 *     channel as creation.
 *
 * Background: scenario 19 covers vhost rendering for explicit IPs;
 * scenarios 20–21 cover IPv6 + multi-IP variants. None of them assert
 * the operator-facing warning when the IP doesn't actually exist on
 * a NIC — Apache would silently fail to bind on `apachectl start` and
 * the user wouldn't know why. This scenario closes that gap.
 */
import { scenario, api, assert, tmpDir, rmTree, writeFile } from '../harness.mjs'
import { join } from 'node:path'

async function createBindSite(ctx, domain, bindAddresses) {
  const docroot = tmpDir(`bind-nic-${domain.replace(/[^a-z0-9]/gi, '-')}`)
  writeFile(join(docroot, 'index.html'), `<html><body>${domain}</body></html>`)
  ctx.cleanup(() => rmTree(docroot))

  await api.delete(`/api/sites/${domain}`).catch(() => {})
  const res = await api.post('/api/sites', {
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
  return res
}

export default scenario('22', 'Bind-IP NIC warning end-to-end', 'P2', async (ctx) => {
  // Case 1: bogus IP → response includes warnings[] flag.
  {
    const domain = 'nic-warn-bogus.e2e.loc'
    const res = await createBindSite(ctx, domain, ['198.51.100.99'])
    assert.statusOk(res, 'POST with bogus IP must still succeed (soft warning, not reject)')
    // Wrapped body shape — when warnings/hints fire, the daemon returns
    // { site, warnings, hints }, not the raw SiteConfig.
    assert.ok(
      typeof res.body === 'object' && res.body !== null && 'site' in res.body,
      `expected wrapped { site, warnings } body; got ${JSON.stringify(res.body).slice(0, 200)}`,
    )
    const warnings = res.body.warnings ?? []
    assert.ok(
      Array.isArray(warnings) && warnings.length > 0,
      `expected warnings[] to be a non-empty array; got ${JSON.stringify(warnings)}`,
    )
    const hit = warnings.find((w) => typeof w === 'string'
      && w.includes('198.51.100.99')
      && /not assigned/i.test(w))
    assert.ok(
      hit !== undefined,
      `warnings must include a message naming 198.51.100.99 as not assigned; got ${JSON.stringify(warnings)}`,
    )
  }

  // Case 2: loopback bind → raw SiteConfig (no wrapper), confirming
  // the warning channel does NOT trigger on a known-good address.
  {
    const domain = 'nic-warn-ok.e2e.loc'
    const res = await createBindSite(ctx, domain, ['127.0.0.1'])
    assert.statusOk(res, 'POST with loopback bind succeeds')
    // Raw SiteConfig has a top-level "domain" field; wrapped response
    // would have a nested "site" object instead.
    assert.ok(
      typeof res.body === 'object' && res.body !== null && 'domain' in res.body && !('warnings' in res.body),
      `expected raw SiteConfig body (no warnings wrapper); got ${JSON.stringify(res.body).slice(0, 200)}`,
    )
  }

  // Case 3: PUT /api/sites/{domain} flipping to bogus IP must also
  // emit the warning. The update endpoint uses a slightly different
  // wrapper shape — { site, warnings } — but the same content rule.
  {
    const domain = 'nic-warn-flip.e2e.loc'
    // Start clean on loopback.
    const create = await createBindSite(ctx, domain, ['127.0.0.1'])
    assert.statusOk(create, 'baseline POST on loopback')

    // Flip to bogus via PUT.
    const put = await api.put(`/api/sites/${domain}`, {
      body: {
        domain,
        documentRoot: create.body.documentRoot ?? create.body.site?.documentRoot,
        phpVersion: 'none',
        sslEnabled: false,
        httpPort: 80,
        httpsPort: 443,
        aliases: [],
        bindAddresses: ['198.51.100.99'],
        environment: {},
      },
    })
    assert.statusOk(put, 'PUT flipping to bogus IP')
    assert.ok(
      typeof put.body === 'object' && put.body !== null && 'site' in put.body && Array.isArray(put.body.warnings),
      `PUT response must be wrapped { site, warnings } on warning; got ${JSON.stringify(put.body).slice(0, 200)}`,
    )
    const flipHit = put.body.warnings.find((w) => typeof w === 'string' && w.includes('198.51.100.99'))
    assert.ok(
      flipHit !== undefined,
      `PUT warnings must name 198.51.100.99; got ${JSON.stringify(put.body.warnings)}`,
    )
  }
})
