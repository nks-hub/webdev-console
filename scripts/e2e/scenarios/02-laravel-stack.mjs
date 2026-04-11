/**
 * Scenario 2 — Laravel site on the full stack.
 *
 * Laravel sites traditionally serve from <code>public/</code> while the
 * project root holds <code>artisan</code> and <code>composer.json</code>.
 * The daemon's detector searches both docroot and its parent so the site
 * framework resolves to "laravel" without extra configuration.
 *
 * This scenario also creates a backing MySQL database (the typical Laravel
 * workflow) and exercises the detect-framework + REST round-trip path.
 *
 * Skips if no PHP version is installed.
 * Database creation step skips gracefully if MySQL plugin is not Running.
 */
import { scenario, api, assert, SkipError, tmpDir, rmTree, writeFile } from '../harness.mjs'
import { join } from 'node:path'
import { mkdirSync } from 'node:fs'

const DOMAIN = 'laravel-e2e.loc'
const DB_NAME = 'laravel_e2e'

export default scenario('2', 'Laravel stack (PHP + MySQL)', 'P1', async (ctx) => {
  const vers = await api.get('/api/php/versions')
  if (vers.status !== 200 || !Array.isArray(vers.body) || vers.body.length === 0) {
    throw new SkipError('no PHP versions installed')
  }
  const phpVer = vers.body[0].majorMinor ?? vers.body[0].version

  // Build the classic Laravel layout: project-root with artisan + composer.json,
  // docroot is project-root/public/.
  const projectRoot = tmpDir('laravel')
  const docroot = join(projectRoot, 'public')
  mkdirSync(docroot, { recursive: true })
  writeFile(
    join(projectRoot, 'artisan'),
    "#!/usr/bin/env php\n<?php\n// Laravel artisan marker for e2e framework detection.\nrequire __DIR__.'/vendor/autoload.php';\n",
  )
  writeFile(
    join(projectRoot, 'composer.json'),
    JSON.stringify({ name: 'e2e/laravel', require: { 'laravel/framework': '^11.0' } }, null, 2),
  )
  writeFile(join(docroot, 'index.php'), '<?php echo "Laravel front controller";')
  ctx.cleanup(() => rmTree(projectRoot))

  await api.delete(`/api/sites/${DOMAIN}`).catch(() => {})

  const create = await api.post('/api/sites', {
    body: {
      domain: DOMAIN,
      documentRoot: docroot,
      phpVersion: phpVer,
      sslEnabled: false,
      httpPort: 80,
      httpsPort: 443,
      aliases: [],
      environment: {},
    },
  })
  ctx.cleanup(() => api.delete(`/api/sites/${DOMAIN}`).catch(() => {}))
  assert.statusOk(create, 'POST /api/sites (Laravel)')

  // Framework detection — Laravel's artisan lives in the PARENT of docroot.
  // The daemon searches both levels so this must resolve to "laravel".
  const detect = await api.post(`/api/sites/${DOMAIN}/detect-framework`)
  assert.statusOk(detect, 'detect-framework')
  assert.eq(detect.body?.framework, 'laravel', 'detected framework is laravel (from project-root/artisan)')

  // Optional backing DB — only if MySQL is Running.
  const mysqlSvc = await api.get('/api/services/mysql')
  if (mysqlSvc.status === 200 && mysqlSvc.body?.state === 2) {
    await api.delete(`/api/databases/${DB_NAME}`).catch(() => {})
    const db = await api.post('/api/databases', { body: { name: DB_NAME } })
    ctx.cleanup(() => api.delete(`/api/databases/${DB_NAME}`).catch(() => {}))
    assert.statusOk(db, `POST /api/databases ${DB_NAME}`)

    const list = await api.get('/api/databases')
    assert.statusOk(list, 'GET /api/databases')
    const dbs = Array.isArray(list.body) ? list.body : (list.body.databases ?? [])
    const found = dbs.find((d) => (typeof d === 'string' ? d : d.name) === DB_NAME)
    assert.ok(found, `${DB_NAME} appears in db listing`)
  }

  // REST round-trip.
  const got = await api.get(`/api/sites/${DOMAIN}`)
  assert.statusOk(got, `GET /api/sites/${DOMAIN}`)
  assert.eq(got.body.phpVersion, phpVer, 'phpVersion round-trip')
})
