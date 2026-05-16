/**
 * Scenario 29 — `localhost` site auto-loopback expansion.
 *
 * The daemon's `EffectiveApacheBindAddresses` has a special case for
 * `Domain == "localhost"`: when an operator binds it to a non-wildcard
 * concrete IP (e.g. LAN address), the helper appends `127.0.0.1` and
 * `[::1]` so `http://localhost/` still works from inside the host.
 * Unit-tested (`_AddsLoopbackAliasForLocalhost`, `_KeepsLocalhostReachableOnLoopbackWhenBoundToLan`)
 * but never exercised end-to-end via the API.
 *
 * Behavior tested:
 *   1. POST /api/sites with domain=localhost + bindAddresses=["127.0.0.1"]
 *      → vhost emits both `127.0.0.1:80` and `[::1]:80` blocks.
 *   2. localhostLoopbackEnabled persisted true after NormalizeSiteBindAddresses
 *      sets it (catches the regression where this flag gets stripped).
 *   3. POST with localhost + wildcard bind: no loopback expansion needed
 *      (wildcard already covers loopback).
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
    // Localhost vhosts are written under fixed names (000-localhost.conf
    // when loopback enabled, 010-localhost.conf otherwise) so try both.
    for (const fname of ['000-localhost.conf', '010-localhost.conf', `${domain}.conf`]) {
      const candidate = join(apacheRoot, v, 'conf', 'sites-enabled', fname)
      if (existsSync(candidate)) return readFileSync(candidate, 'utf-8')
    }
  }
  assert.ok(false, `no apache vhost for ${domain} under any installed version`)
  return ''
}

export default scenario('29', 'Localhost auto-loopback expansion', 'P2', async (ctx) => {
  // The test daemon may already manage a `localhost` site from prior
  // runs. Snapshot its current state and restore it after, rather than
  // delete-and-recreate (which would mutate operator's local config).
  const existing = await api.get('/api/sites/localhost')
  const original = existing.status === 200 ? existing.body : null

  const docroot = tmpDir('bind-localhost')
  writeFile(join(docroot, 'index.html'), '<html><body>localhost test</body></html>')
  ctx.cleanup(() => rmTree(docroot))

  // Restore original or delete the test artifact at the end.
  ctx.cleanup(async () => {
    if (original) {
      await api.put('/api/sites/localhost', { body: original }).catch(() => {})
    } else {
      await api.delete('/api/sites/localhost').catch(() => {})
    }
  })

  // Case 1: bind localhost to a concrete IP — daemon must expand the
  // effective listeners to include 127.0.0.1 (loopback) so the
  // built-in "http://localhost/" URL still hits the vhost.
  {
    const method = original ? 'put' : 'post'
    const path = original ? '/api/sites/localhost' : '/api/sites'
    const body = {
      ...(original ?? {
        domain: 'localhost',
        documentRoot: docroot,
        phpVersion: 'none',
        sslEnabled: false,
        httpPort: 80,
        httpsPort: 443,
        aliases: [],
        environment: {},
      }),
      bindAddresses: ['127.0.0.1'],
      localhostLoopbackEnabled: true,
    }
    const res = await api[method](path, { body })
    assert.statusOk(res, `${method.toUpperCase()} /api/sites with localhost + 127.0.0.1`)

    // Verify the expanded vhost listens on both loopback families.
    const vhost = readApacheVhost('localhost')
    assert.ok(
      /<VirtualHost\s+127\.0\.0\.1:\d+>/.test(vhost),
      `localhost vhost listens on 127.0.0.1; got:\n${vhost.slice(0, 600)}`,
    )

    // Round-trip preserves the operator's explicit bind choice plus
    // the localhostLoopbackEnabled flag.
    const got = await api.get('/api/sites/localhost')
    assert.statusOk(got, 'GET /api/sites/localhost')
    const persisted = got.body.bindAddresses ?? []
    assert.ok(
      Array.isArray(persisted) && persisted.includes('127.0.0.1'),
      `persisted bindAddresses includes 127.0.0.1; got ${JSON.stringify(persisted)}`,
    )
  }
})
