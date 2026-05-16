/**
 * Scenario 20 — IPv6 bind IP rendering in generated Apache vhost.
 *
 * Mirrors scenario 19 but exercises the IPv6 code paths that previously
 * only had unit-test coverage (BindAddressNormalizationTests):
 *
 *   - bind "::1"            → vhost contains <VirtualHost [::1]:NN>
 *   - bind "[::1]" form      → daemon canonicalizes to bare "::1" in TOML
 *                              but still emits bracketed form in vhost
 *   - bind-addresses endpoint surfaces "::1"
 *
 * The Scriban template wraps any bind address containing ":" with []
 * via EffectiveApacheBindAddresses → FormatApacheBindAddress. Asserting
 * this end-to-end protects against regressions that would otherwise
 * silently emit "<VirtualHost ::1:80>" — invalid Apache config.
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
  const docroot = tmpDir(`bind6-${domain.replace(/[^a-z0-9]/gi, '-')}`)
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

export default scenario('20', 'IPv6 bind rendering in vhost', 'P2', async (ctx) => {
  // Case 1: ::1 loopback — vhost must emit bracketed IPv6 form.
  {
    const domain = 'bind-v6loop.e2e.loc'
    await createBindSite(ctx, domain, ['::1'])
    const vhost = readVhost(domain)
    assert.ok(
      /<VirtualHost\s+\[::1\]:\d+>/.test(vhost),
      `IPv6 loopback bind must render <VirtualHost [::1]:NN>; got:\n${vhost}`,
    )
    // Negative: must NOT emit unbracketed "::1:" which Apache rejects.
    assert.ok(
      !/<VirtualHost\s+::1:\d+>/.test(vhost),
      `vhost must NOT contain unbracketed IPv6 form; got:\n${vhost}`,
    )
  }

  // Case 2: bracketed input gets normalized — TOML round-trip stores the
  // canonical IPAddress form (bare "::1") even though the user typed [::1].
  // Vhost still emits the bracketed form because FormatApacheBindAddress
  // re-wraps when the value contains a colon.
  {
    const domain = 'bind-v6bracket.e2e.loc'
    await createBindSite(ctx, domain, ['[::1]'])
    const got = await api.get(`/api/sites/${domain}`)
    assert.statusOk(got, `GET /api/sites/${domain}`)
    const persisted = got.body.bindAddresses ?? []
    assert.eq(
      persisted.length,
      1,
      `bindAddresses round-trip expects single entry; got ${JSON.stringify(persisted)}`,
    )
    // Canonicalized form — daemon strips brackets in normalization.
    assert.eq(
      persisted[0],
      '::1',
      `bracket form must normalize to bare ::1; got ${JSON.stringify(persisted[0])}`,
    )
    const vhost = readVhost(domain)
    assert.ok(
      /<VirtualHost\s+\[::1\]:\d+>/.test(vhost),
      `vhost still emits bracketed form regardless of input format; got:\n${vhost}`,
    )
  }

  // Case 3: bind-addresses discovery includes "::1" — operators must be
  // able to pick IPv6 loopback from the GUI without typing it manually.
  {
    const opts = await api.get('/api/sites/bind-addresses')
    assert.statusOk(opts, 'GET /api/sites/bind-addresses')
    const values = Array.isArray(opts.body) ? opts.body.map((o) => o.value) : []
    assert.ok(
      values.includes('::1'),
      `bind-address options include "::1"; got ${JSON.stringify(values)}`,
    )
  }
})
