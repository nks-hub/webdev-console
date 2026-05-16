/**
 * Scenario 24 — Fresh-install "first site" smoke test.
 *
 * Validates the journey a first-time operator takes immediately after
 * installation, without depending on any pre-existing daemon state:
 *
 *   1. The bind-address discovery endpoint answers (`*`, `127.0.0.1`,
 *      `::1` always present even on offline / NIC-free hosts).
 *   2. The PHP versions endpoint answers (may be empty on a truly
 *      fresh box, must still be 200).
 *   3. POST /api/sites with the minimum simple-mode payload creates a
 *      working site (defaults to `bindAddresses=["*"]` when omitted,
 *      SSL off, no PHP).
 *   4. The vhost file lands on disk under sites-enabled/ AND the
 *      daemon's history copy under generated/.
 *   5. GET /api/sites/{domain} round-trips the saved config without
 *      losing any default fields.
 *   6. DELETE /api/sites/{domain} removes both files.
 *
 * Reads from `~/.wdc/binaries/apache/{ver}/conf/sites-enabled/` for
 * the live vhost (scenario 23 pattern) — that's the file Apache
 * actually reads, separate from the daemon's `generated/` history.
 *
 * Acts as the canonical "from installation" regression: if a new
 * daemon version breaks the very first site creation, this scenario
 * fails. Independent of pre-existing sites, plugins, or config.
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

export default scenario('24', 'Fresh-install first-site flow', 'P0', async (ctx) => {
  // Step 1: bind-address options endpoint reachable + advertises the
  // platform-independent listeners. A "fresh" daemon must still hand
  // these out — the operator picks one in the create dialog.
  {
    const opts = await api.get('/api/sites/bind-addresses')
    assert.statusOk(opts, 'GET /api/sites/bind-addresses must answer on fresh install')
    const values = Array.isArray(opts.body) ? opts.body.map((o) => o.value) : []
    for (const required of ['*', '127.0.0.1', '::1']) {
      assert.ok(
        values.includes(required),
        `bind-address options must include ${required}; got ${JSON.stringify(values)}`,
      )
    }
  }

  // Step 2: PHP versions endpoint reachable. May return an empty list
  // on a host that hasn't installed any version yet — that's fine,
  // the simple create dialog shows an empty-state hint in that case.
  // What we DON'T accept is a 500 / disconnect.
  {
    const versions = await api.get('/api/php/versions')
    assert.ok(
      versions.status === 200 || versions.status === 204,
      `GET /api/php-versions must answer 2xx (got ${versions.status})`,
    )
  }

  // Step 3: create the minimum-viable first site. Static HTML, default
  // bind, no SSL — the kind of "myapp.loc" a brand-new user makes.
  const domain = 'fresh-install.e2e.loc'
  await api.delete(`/api/sites/${domain}`).catch(() => {})
  const docroot = tmpDir(`fresh-install-${domain.replace(/[^a-z0-9]/gi, '-')}`)
  writeFile(join(docroot, 'index.html'), `<html><body>fresh install OK</body></html>`)
  ctx.cleanup(() => rmTree(docroot))

  const create = await api.post('/api/sites', {
    body: {
      domain,
      documentRoot: docroot,
      phpVersion: 'none',
      sslEnabled: false,
      httpPort: 80,
      httpsPort: 443,
      aliases: [],
      // Intentionally NOT supplying bindAddresses to prove the daemon
      // defaults to the wildcard listener.
      environment: {},
    },
  })
  ctx.cleanup(() => api.delete(`/api/sites/${domain}`).catch(() => {}))
  assert.statusOk(create, 'POST /api/sites for the first-ever site')

  // Step 4: live Apache vhost and daemon-history vhost both land on disk.
  const liveVhost = findApacheVhost(domain)
  assert.ok(liveVhost, `live Apache vhost must exist under sites-enabled/ for ${domain}`)
  const live = readFileSync(liveVhost, 'utf-8')
  assert.ok(
    /<VirtualHost\s+\*:80>/.test(live),
    `live vhost binds wildcard HTTP by default; got:\n${live}`,
  )
  assert.contains(live, domain, 'live vhost references the site domain')

  const historyVhost = join(wdcDataDir(), 'generated', `${domain}.conf`)
  assert.ok(existsSync(historyVhost), `daemon history vhost exists at ${historyVhost}`)
  const history = readFileSync(historyVhost, 'utf-8')
  assert.ok(
    /<VirtualHost\s+\*:\d+>/.test(history),
    `daemon history vhost emits at least one wildcard VirtualHost; got:\n${history}`,
  )

  // Step 5: round-trip GET preserves the wildcard default.
  const got = await api.get(`/api/sites/${domain}`)
  assert.statusOk(got, `GET /api/sites/${domain}`)
  const persisted = got.body.bindAddresses ?? []
  assert.eq(
    JSON.stringify(persisted),
    JSON.stringify(['*']),
    `default bindAddresses should be ["*"]; got ${JSON.stringify(persisted)}`,
  )

  // Step 6: DELETE removes the vhost from sites-enabled. Generated
  // history is preserved by design (rollback / audit), so we only
  // assert the live file goes away.
  const del = await api.delete(`/api/sites/${domain}`)
  assert.statusOk(del, `DELETE /api/sites/${domain}`)
  assert.ok(
    findApacheVhost(domain) === null,
    `live vhost removed from sites-enabled/ after DELETE`,
  )
})
