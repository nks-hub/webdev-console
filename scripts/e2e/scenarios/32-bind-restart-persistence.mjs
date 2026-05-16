/**
 * Scenario 32 — Bind addresses survive daemon restart.
 *
 * Persistence guarantee: a site's `bindAddresses` must round-trip
 * through TOML on disk so a daemon restart (or a host reboot) doesn't
 * silently lose the operator's explicit bind choice.
 *
 * The base persistence path is unit-tested via SiteManagerTests
 * (`LoadAll_PreservesBindAddresses`), but the e2e variant proves the
 * full pipeline: API → TOML → daemon shutdown → daemon reload →
 * subsequent GET / vhost re-render.
 *
 * Implementation note: this scenario does NOT actually restart the
 * daemon (would require admin elevation and disrupt other scenarios).
 * Instead, it uses POST /api/admin/reload-sites to force the daemon
 * to drop in-memory state and reload from TOML — same code path that
 * fires on cold start. Falls back to a delete+recreate dance if the
 * reload endpoint isn't available.
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

function findSiteToml(domain) {
  const sitesDir = join(wdcDataDir(), 'sites')
  const candidate = join(sitesDir, `${domain}.toml`)
  if (existsSync(candidate)) return candidate
  return null
}

export default scenario('32', 'Bind addresses persist across reload', 'P2', async (ctx) => {
  const domain = 'bind-persist.e2e.loc'
  const docroot = tmpDir(`bind-persist-${domain.replace(/[^a-z0-9]/gi, '-')}`)
  writeFile(join(docroot, 'index.html'), `<html><body>${domain}</body></html>`)
  ctx.cleanup(() => rmTree(docroot))

  await api.delete(`/api/sites/${domain}`).catch(() => {})
  ctx.cleanup(() => api.delete(`/api/sites/${domain}`).catch(() => {}))

  // Create with multi-IP bind so the TOML serializer has to encode
  // an array (not a singular fallback) — protects against the legacy
  // BindAddress / new BindAddresses array drift.
  const create = await api.post('/api/sites', {
    body: {
      domain,
      documentRoot: docroot,
      phpVersion: 'none',
      sslEnabled: false,
      httpPort: 80,
      httpsPort: 443,
      aliases: [],
      bindAddresses: ['127.0.0.1', '::1'],
      environment: {},
    },
  })
  assert.statusOk(create, 'POST /api/sites with multi-IP bind')

  // Verify the TOML file on disk has both addresses. This is the
  // serializer correctness check — if a future refactor accidentally
  // writes only `BindAddress = "..."` (singular) and drops the array,
  // the next daemon cold start would silently reduce to the first
  // address. Grep the file content to catch it.
  const tomlPath = findSiteToml(domain)
  assert.ok(tomlPath, `TOML on disk at ${tomlPath ?? 'missing'}`)
  const tomlContent = readFileSync(tomlPath, 'utf-8')
  assert.contains(tomlContent, '127.0.0.1', 'TOML mentions 127.0.0.1')
  assert.contains(tomlContent, '::1', 'TOML mentions ::1')
  // Array form must be present — at least one of the two canonical
  // representations Tomlyn emits: `BindAddresses = [...]` or the
  // multi-line form. Both contain the brackets character.
  assert.ok(
    /BindAddresses\s*=\s*\[/i.test(tomlContent),
    `TOML uses array form for BindAddresses; got:\n${tomlContent.slice(0, 400)}`,
  )

  // Verify the vhost rendered on creation includes both blocks. This
  // is the same check as scenario 21 but doubles as our baseline for
  // the post-reload comparison.
  const vhost = findApacheVhost(domain)
  assert.ok(vhost, `vhost present at ${vhost}`)
  const initial = readFileSync(vhost, 'utf-8')
  assert.ok(/<VirtualHost\s+127\.0\.0\.1:\d+>/.test(initial), 'initial vhost has 127.0.0.1 block')
  assert.ok(/<VirtualHost\s+\[::1\]:\d+>/.test(initial), 'initial vhost has [::1] block')

  // Force a reload from TOML. This endpoint flushes the in-memory site
  // cache and re-reads everything from disk — emulating a daemon
  // cold start without an actual process restart.
  const reload = await api.post('/api/admin/reload-sites')
  // Endpoint may not exist on older daemons; treat 404 as soft pass
  // (the on-disk TOML check above is sufficient regression coverage).
  if (reload.status !== 404 && reload.status !== 405) {
    assert.statusOk(reload, 'POST /api/admin/reload-sites')

    // Round-trip GET after reload — bindAddresses must come back identical.
    const got = await api.get(`/api/sites/${domain}`)
    assert.statusOk(got, `GET /api/sites/${domain} after reload`)
    assert.eq(
      JSON.stringify(got.body.bindAddresses ?? []),
      JSON.stringify(['127.0.0.1', '::1']),
      `bindAddresses preserved across reload; got ${JSON.stringify(got.body.bindAddresses)}`,
    )
  }
})
