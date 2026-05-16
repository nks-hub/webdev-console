/**
 * Scenario 33 — Delete + recreate cycle resets vhost state cleanly.
 *
 * A site that's deleted then recreated with different bind addresses
 * must NOT retain artifacts from the previous incarnation. Catches
 * regressions where:
 *   - The Apache plugin's sites-enabled/ file isn't pruned on delete.
 *   - The daemon's generated/ history isn't rotated when the new
 *     create happens.
 *   - bindAddresses TOML round-trip leaks old values via legacy
 *     `BindAddress` singular field.
 *
 * Sequence:
 *   1. Create site bound to 127.0.0.1 → vhost present with v4 block.
 *   2. Delete site → vhost removed from sites-enabled/.
 *   3. Recreate same domain with bindAddresses=["::1"] → vhost present,
 *      contains [::1] block, must NOT contain 127.0.0.1 from previous.
 *   4. GET round-trip returns only `["::1"]`.
 */
import { scenario, api, assert, tmpDir, rmTree, writeFile, wdcDataDir } from '../harness.mjs'
import { join } from 'node:path'
import { readFileSync, existsSync, readdirSync } from 'node:fs'

function findApacheVhost(domain) {
  const apacheRoot = join(wdcDataDir(), 'binaries', 'apache')
  if (!existsSync(apacheRoot)) return null
  for (const v of readdirSync(apacheRoot)) {
    const candidate = join(apacheRoot, v, 'conf', 'sites-enabled', `${domain}.conf`)
    if (existsSync(candidate)) return candidate
  }
  return null
}

export default scenario('33', 'Bind delete + recreate cycle is clean', 'P2', async (ctx) => {
  const domain = 'bind-recycle.e2e.loc'
  const docroot = tmpDir(`bind-recycle-${domain.replace(/[^a-z0-9]/gi, '-')}`)
  writeFile(join(docroot, 'index.html'), `<html><body>${domain}</body></html>`)
  ctx.cleanup(() => rmTree(docroot))
  ctx.cleanup(() => api.delete(`/api/sites/${domain}`).catch(() => {}))

  // Step 1: create with IPv4 loopback.
  await api.delete(`/api/sites/${domain}`).catch(() => {})
  const create1 = await api.post('/api/sites', {
    body: {
      domain,
      documentRoot: docroot,
      phpVersion: 'none',
      sslEnabled: false,
      httpPort: 80,
      httpsPort: 443,
      aliases: [],
      bindAddresses: ['127.0.0.1'],
      environment: {},
    },
  })
  assert.statusOk(create1, 'first POST with 127.0.0.1')
  const initial = readFileSync(findApacheVhost(domain), 'utf-8')
  assert.ok(/<VirtualHost\s+127\.0\.0\.1:\d+>/.test(initial), 'initial vhost has v4 block')

  // Step 2: delete — vhost file gone from sites-enabled.
  const del = await api.delete(`/api/sites/${domain}`)
  assert.statusOk(del, 'DELETE /api/sites/{domain}')
  assert.ok(findApacheVhost(domain) === null, 'vhost removed after DELETE')

  // Step 3: recreate same domain with different bind. Critical: the
  // new vhost must NOT retain the prior 127.0.0.1 block. A regression
  // here would mean the daemon merges instead of replaces.
  const create2 = await api.post('/api/sites', {
    body: {
      domain,
      documentRoot: docroot,
      phpVersion: 'none',
      sslEnabled: false,
      httpPort: 80,
      httpsPort: 443,
      aliases: [],
      bindAddresses: ['::1'],
      environment: {},
    },
  })
  assert.statusOk(create2, 'second POST with ::1')
  const after = readFileSync(findApacheVhost(domain), 'utf-8')
  assert.ok(/<VirtualHost\s+\[::1\]:\d+>/.test(after), 'recreated vhost has v6 block')
  assert.ok(
    !/<VirtualHost\s+127\.0\.0\.1:\d+>/.test(after),
    `recreated vhost must NOT carry stale v4 block; got:\n${after.slice(0, 600)}`,
  )

  // Step 4: GET round-trip — only the new bind, no leak.
  const got = await api.get(`/api/sites/${domain}`)
  assert.statusOk(got, `GET /api/sites/${domain}`)
  assert.eq(
    JSON.stringify(got.body.bindAddresses ?? []),
    JSON.stringify(['::1']),
    `bindAddresses only contains ::1 after recreate; got ${JSON.stringify(got.body.bindAddresses)}`,
  )
})
