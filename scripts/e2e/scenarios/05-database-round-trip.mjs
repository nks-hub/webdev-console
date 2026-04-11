/**
 * Scenario 5 — Database create → list tables → drop. A condensed version
 * of the doc scenario (which uses a 50 MB SQL dump); the runner variant
 * exercises the MySQL plugin's lifecycle API and CREATE / DROP database
 * endpoints so schema drift or privilege regressions surface in CI.
 *
 * Skips if MySQL plugin isn't Running.
 */
import { scenario, api, assert, SkipError } from '../harness.mjs'

const DB_NAME = 'wdc_e2e_db'

export default scenario('5', 'MySQL database lifecycle', 'P2', async (ctx) => {
  const svc = await api.get('/api/services/mysql')
  if (svc.status !== 200) throw new SkipError('MySQL plugin not available')
  // Service state enum: 0=Stopped,1=Starting,2=Running,3=Stopping,4=Crashed,5=Disabled
  if (svc.body?.state !== 2) throw new SkipError(`MySQL not running (state=${svc.body?.state})`)

  // Clean up any leftover from a prior failed run.
  await api.delete(`/api/databases/${DB_NAME}`).catch(() => {})

  const create = await api.post('/api/databases', { body: { name: DB_NAME } })
  ctx.cleanup(() => api.delete(`/api/databases/${DB_NAME}`).catch(() => {}))
  assert.statusOk(create, 'POST /api/databases')

  const list = await api.get('/api/databases')
  assert.statusOk(list, 'GET /api/databases')
  const dbs = Array.isArray(list.body) ? list.body : (list.body.databases ?? [])
  const found = dbs.find((d) => (typeof d === 'string' ? d : d.name) === DB_NAME)
  assert.ok(found, `${DB_NAME} appears in listing`)

  // Tables endpoint on empty DB should return an empty array (or similar shape).
  const tables = await api.get(`/api/databases/${DB_NAME}/tables`)
  assert.statusOk(tables, `GET /api/databases/${DB_NAME}/tables`)
  const tblArr = Array.isArray(tables.body) ? tables.body : (tables.body.tables ?? [])
  assert.ok(Array.isArray(tblArr), 'tables response is an array')
  assert.eq(tblArr.length, 0, 'fresh database has no tables')

  const drop = await api.delete(`/api/databases/${DB_NAME}`)
  assert.statusOk(drop, `DELETE /api/databases/${DB_NAME}`)
})
