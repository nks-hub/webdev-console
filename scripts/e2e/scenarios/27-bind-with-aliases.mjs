/**
 * Scenario 27 — Bind IP × ServerAlias combinations.
 *
 * Scenarios 19–26 cover bind IP rendering, IPv6, multi-IP, NIC warnings,
 * SSL combos and custom ports. None of them exercise the ServerAlias
 * code path — the Apache plugin template emits each alias inside every
 * VirtualHost block, so explicit-bind + alias must render the alias on
 * the bound vhost rather than fall through to the wildcard.
 *
 * Cases covered:
 *   1. Site with `aliases:["www.foo.loc","api.foo.loc"]` + `bindAddresses:["127.0.0.1"]`
 *      → vhost emits `<VirtualHost 127.0.0.1:80>` containing a
 *        `ServerAlias www.foo.loc api.foo.loc` directive.
 *   2. Wildcard bind + same aliases — emits ServerAlias on `*:80` block.
 *   3. GET round-trip preserves aliases verbatim, in declared order.
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

async function createSite(ctx, domain, bindAddresses, aliases) {
  const docroot = tmpDir(`bind-alias-${domain.replace(/[^a-z0-9]/gi, '-')}`)
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
      aliases,
      bindAddresses,
      environment: {},
    },
  })
  ctx.cleanup(() => api.delete(`/api/sites/${domain}`).catch(() => {}))
  return res
}

export default scenario('27', 'Bind IP × ServerAlias rendering', 'P2', async (ctx) => {
  // Case 1: explicit loopback + two aliases — aliases land on the
  // IPv4-loopback vhost block, not on a wildcard fallthrough.
  {
    const domain = 'bind-alias-loop.e2e.loc'
    const aliases = ['www.bind-alias-loop.e2e.loc', 'api.bind-alias-loop.e2e.loc']
    const res = await createSite(ctx, domain, ['127.0.0.1'], aliases)
    assert.statusOk(res, 'POST with explicit bind + aliases')

    const vhost = readApacheVhost(domain)
    // ServerAlias directive must appear inside a VirtualHost bound to
    // 127.0.0.1 — not the wildcard one.
    const v4Block = vhost.match(
      /<VirtualHost\s+127\.0\.0\.1:\d+>([\s\S]*?)<\/VirtualHost>/g,
    )
    assert.ok(
      v4Block && v4Block.some(b => /ServerAlias\s+.*www\.bind-alias-loop\.e2e\.loc/.test(b)),
      `IPv4 vhost block must include ServerAlias with www. prefix; got:\n${vhost.slice(0, 1500)}`,
    )
    assert.ok(
      v4Block && v4Block.some(b => /ServerAlias\s+.*api\.bind-alias-loop\.e2e\.loc/.test(b)),
      `IPv4 vhost block must include ServerAlias with api. prefix`,
    )

    // Round-trip preserves aliases verbatim, in order.
    const got = await api.get(`/api/sites/${domain}`)
    assert.statusOk(got, `GET /api/sites/${domain}`)
    assert.eq(
      JSON.stringify(got.body.aliases ?? []),
      JSON.stringify(aliases),
      `aliases round-trip in order`,
    )
  }

  // Case 2: wildcard bind + aliases — ServerAlias lands on `*:80` block.
  // Catches a regression where wildcard rendering would drop aliases.
  {
    const domain = 'bind-alias-wild.e2e.loc'
    const aliases = ['legacy.bind-alias-wild.e2e.loc']
    const res = await createSite(ctx, domain, ['*'], aliases)
    assert.statusOk(res, 'POST with wildcard + alias')

    const vhost = readApacheVhost(domain)
    const wildBlock = vhost.match(/<VirtualHost\s+\*:\d+>([\s\S]*?)<\/VirtualHost>/)
    assert.ok(wildBlock, `wildcard vhost block present; got:\n${vhost.slice(0, 600)}`)
    assert.ok(
      /ServerAlias\s+.*legacy\.bind-alias-wild\.e2e\.loc/.test(wildBlock[1]),
      `wildcard block must include ServerAlias; got:\n${wildBlock[0]}`,
    )
  }
})
