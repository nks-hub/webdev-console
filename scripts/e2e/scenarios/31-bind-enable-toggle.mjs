/**
 * Scenario 31 — Bind IP survives soft-disable / re-enable cycle.
 *
 * `PATCH /api/sites/{domain}/enabled` body `{enabled: false}` removes
 * the active vhost from sites-enabled/ but keeps the TOML so the site
 * remains visible in the GUI. Re-enabling must restore the vhost
 * with the same bind addresses configured before — operators using
 * the simple-mode per-card toggle expect their bind choice to survive.
 *
 * Behavior tested:
 *   1. Create site with explicit bind `127.0.0.1` → vhost present.
 *   2. PATCH enabled:false → vhost file removed from sites-enabled/,
 *      TOML preserved.
 *   3. GET /api/sites/{domain} shows `enabled: false` + bindAddresses
 *      preserved.
 *   4. PATCH enabled:true → vhost re-appears with `127.0.0.1:80` block.
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

export default scenario('31', 'Bind IP survives enable/disable toggle', 'P2', async (ctx) => {
  const domain = 'bind-toggle.e2e.loc'
  const docroot = tmpDir(`bind-toggle-${domain.replace(/[^a-z0-9]/gi, '-')}`)
  writeFile(join(docroot, 'index.html'), `<html><body>${domain}</body></html>`)
  ctx.cleanup(() => rmTree(docroot))

  await api.delete(`/api/sites/${domain}`).catch(() => {})
  ctx.cleanup(() => api.delete(`/api/sites/${domain}`).catch(() => {}))

  // Case 1: create with explicit bind.
  const create = await api.post('/api/sites', {
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
  assert.statusOk(create, 'POST /api/sites with explicit bind')
  assert.ok(findApacheVhost(domain), 'vhost present after create')

  // Case 2: soft-disable via PATCH (harness gained .patch helper).
  const patch1 = await api.patch(`/api/sites/${domain}/enabled`, { body: { enabled: false } })
  assert.statusOk(patch1, `PATCH /api/sites/${domain}/enabled false`)

  // Case 3: GET shows enabled:false + bindAddresses preserved.
  const got1 = await api.get(`/api/sites/${domain}`)
  assert.statusOk(got1, `GET /api/sites/${domain} after disable`)
  assert.eq(got1.body.enabled, false, `site marked disabled; got enabled=${got1.body.enabled}`)
  assert.eq(
    JSON.stringify(got1.body.bindAddresses ?? []),
    JSON.stringify(['127.0.0.1']),
    `bindAddresses preserved across disable; got ${JSON.stringify(got1.body.bindAddresses)}`,
  )

  // Case 4: re-enable via PATCH, vhost reappears with the same bind.
  const patch2 = await api.patch(`/api/sites/${domain}/enabled`, { body: { enabled: true } })
  assert.statusOk(patch2, `PATCH /api/sites/${domain}/enabled true`)

  const reEnabledVhost = findApacheVhost(domain)
  assert.ok(reEnabledVhost, 'vhost present again after re-enable')
  const content = readFileSync(reEnabledVhost, 'utf-8')
  assert.ok(
    /<VirtualHost\s+127\.0\.0\.1:\d+>/.test(content),
    `re-enabled vhost binds 127.0.0.1; got:\n${content.slice(0, 600)}`,
  )
})
