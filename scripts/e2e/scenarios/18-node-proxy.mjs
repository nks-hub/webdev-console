/**
 * Scenario #18 - Node.js reverse-proxy site.
 *
 * Exercises the config pipeline for Node upstream sites without requiring a
 * real Node server on the upstream port.
 */
import { scenario, api, assert, tmpDir, rmTree, writeFile } from '../harness.mjs'

export default scenario('18', 'Node.js proxy site lifecycle', 'P1', async (ctx) => {
  const domain = 'e2e-node-proxy.loc'
  const root = tmpDir('node-proxy')
  writeFile(`${root}/package.json`, '{"scripts":{"start":"node server.js"}}')
  ctx.cleanup(() => rmTree(root))
  ctx.cleanup(() => api.delete(`/api/sites/${domain}`))

  const created = await api.post('/api/sites', {
    body: {
      domain,
      documentRoot: root,
      phpVersion: 'none',
      nodeUpstreamPort: 9999,
      sslEnabled: false,
      httpPort: 80,
      httpsPort: 443,
      aliases: [],
    },
  })
  assert.statusOk(created, 'POST /api/sites node proxy')
  assert.eq(created.body.domain, domain, 'created domain')

  const siteRes = await api.get(`/api/sites/${domain}`)
  assert.statusOk(siteRes, `GET /api/sites/${domain}`)
  assert.eq(siteRes.body.nodeUpstreamPort, 9999, 'nodeUpstreamPort persisted')
  assert.eq(siteRes.body.phpVersion, 'none', 'phpVersion persisted')

  const historyRes = await api.get(`/api/sites/${domain}/history`)
  if (historyRes.status !== 404) {
    assert.statusOk(historyRes, `GET /api/sites/${domain}/history`)
  }

  const deleted = await api.delete(`/api/sites/${domain}`)
  assert.statusOk(deleted, `DELETE /api/sites/${domain}`)

  const afterDelete = await api.get(`/api/sites/${domain}`)
  assert.ok(afterDelete.status === 404, 'site not found after delete')
})
