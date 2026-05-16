/**
 * Scenario 30 — Bind IP API rejection paths (security boundaries).
 *
 * `SiteManager.ValidateBindAddresses` rejects:
 *   - Mixed wildcard "*" combined with concrete IP(s)
 *   - Shell injection chars (semicolons, backticks, $(...))
 *   - Non-parseable IP strings (garbage like "not-an-ip")
 *   - Oversize entries (>64 chars)
 *
 * Unit-tested in `BindAddressNormalizationTests`, but the API layer
 * wraps validation in a try/catch so it's important to verify the
 * end-to-end shape: 4xx response, no site created on disk, no partial
 * vhost rendered.
 */
import { scenario, api, assert, tmpDir, rmTree, writeFile, wdcDataDir } from '../harness.mjs'
import { join } from 'node:path'
import { existsSync, readdirSync } from 'node:fs'

function siteExistsOnDisk(domain) {
  const apacheRoot = join(wdcDataDir(), 'binaries', 'apache')
  if (!existsSync(apacheRoot)) return false
  for (const v of readdirSync(apacheRoot)) {
    const candidate = join(apacheRoot, v, 'conf', 'sites-enabled', `${domain}.conf`)
    if (existsSync(candidate)) return true
  }
  return false
}

async function tryCreate(ctx, domain, bindAddresses) {
  const docroot = tmpDir(`bind-reject-${domain.replace(/[^a-z0-9]/gi, '-')}`)
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

export default scenario('30', 'Bind IP API rejection paths', 'P1', async (ctx) => {
  // Case 1: mixed wildcard + specific — daemon must reject (Apache can't
  // sensibly bind both name-based wildcard and IP-specific blocks).
  {
    const domain = 'reject-mixed.e2e.loc'
    const res = await tryCreate(ctx, domain, ['*', '127.0.0.1'])
    assert.ok(
      res.status >= 400 && res.status < 500,
      `mixed * + specific must be 4xx; got ${res.status} body=${JSON.stringify(res.body).slice(0, 200)}`,
    )
    assert.ok(
      !siteExistsOnDisk(domain),
      `rejected site must not land on disk under sites-enabled/`,
    )
  }

  // Case 2: shell injection char in bind address. ValidateBindAddresses
  // refuses anything containing `;`, `` ` ``, `$`, etc. — defense-in-depth
  // against a future code path passing the string into a shell command.
  {
    const domain = 'reject-shell.e2e.loc'
    const res = await tryCreate(ctx, domain, ['127.0.0.1; rm -rf /'])
    assert.ok(
      res.status >= 400 && res.status < 500,
      `shell-injection chars must be 4xx; got ${res.status}`,
    )
  }

  // Case 3: non-parseable IP string. IPAddress.TryParse returns false →
  // ValidateBindAddress throws ArgumentException → API maps to 400.
  {
    const domain = 'reject-garbage.e2e.loc'
    const res = await tryCreate(ctx, domain, ['not-an-ip-address'])
    assert.ok(
      res.status >= 400 && res.status < 500,
      `garbage IP must be 4xx; got ${res.status}`,
    )
  }

  // Case 4: oversize bind address (>64 chars). Defends against memory
  // pressure attacks via crafted POST bodies.
  {
    const domain = 'reject-oversize.e2e.loc'
    const oversize = 'fe80::' + 'a'.repeat(64)  // 70+ chars total
    const res = await tryCreate(ctx, domain, [oversize])
    assert.ok(
      res.status >= 400 && res.status < 500,
      `oversize bind (${oversize.length} chars) must be 4xx; got ${res.status}`,
    )
  }
})
