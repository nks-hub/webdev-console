/**
 * Scenario #17 - Cloudflare Tunnel config round-trip.
 *
 * Verifies config persistence without requiring a real API token or
 * cloudflared binary.
 */
import { scenario, api, assert, SkipError } from '../harness.mjs'

export default scenario('17', 'Cloudflare config round-trip', 'P1', async (_ctx) => {
  const pluginsRes = await api.get('/api/plugins')
  assert.statusOk(pluginsRes, 'GET /api/plugins')
  const plugins = Array.isArray(pluginsRes.body) ? pluginsRes.body : pluginsRes.body.plugins ?? []
  const cfPlugin = plugins.find((p) => p.id === 'nks.wdc.cloudflare')
  if (!cfPlugin) {
    throw new SkipError('Cloudflare plugin not loaded')
  }

  const cfgRes = await api.get('/api/cloudflare/config')
  assert.statusOk(cfgRes, 'GET /api/cloudflare/config')
  const cfg = cfgRes.body
  assert.ok(typeof cfg === 'object' && cfg !== null, 'cloudflare config is object')
  for (const key of ['cloudflaredPath', 'tunnelToken', 'tunnelName', 'tunnelId', 'apiToken', 'accountId', 'subdomainTemplate']) {
    assert.ok(key in cfg, `config includes ${key}`)
  }

  const putRes = await api.put('/api/cloudflare/config', {
    body: {
      tunnelName: 'e2e-test-tunnel',
      accountId: 'test-account-id-12345',
      subdomainTemplate: '{stem}-e2e-{hash}',
    },
  })
  assert.statusOk(putRes, 'PUT /api/cloudflare/config')
  assert.eq(putRes.body.tunnelName, 'e2e-test-tunnel', 'tunnelName saved')
  assert.eq(putRes.body.accountId, 'test-account-id-12345', 'accountId saved')

  const readBackRes = await api.get('/api/cloudflare/config')
  assert.statusOk(readBackRes, 'GET /api/cloudflare/config after PUT')
  const readBack = readBackRes.body
  assert.eq(readBack.tunnelName, 'e2e-test-tunnel', 'tunnelName persisted')
  if (readBack.apiToken && !readBack.apiToken.startsWith('**') && !readBack.apiToken.startsWith('••')) {
    throw new Error('apiToken not redacted')
  }

  const first = await api.get('/api/cloudflare/suggest-subdomain?domain=e2etest.loc')
  assert.statusOk(first, 'GET /api/cloudflare/suggest-subdomain first')
  assert.ok(first.body.suggestion?.startsWith('e2etest-'), `unexpected suggestion: ${first.body.suggestion}`)
  const hash = first.body.suggestion.replace('e2etest-e2e-', '')
  assert.ok(/^[0-9a-f]{6}$/.test(hash), `hash part is 6 hex chars: ${hash}`)

  const repeat = await api.get('/api/cloudflare/suggest-subdomain?domain=e2etest.loc')
  assert.statusOk(repeat, 'GET /api/cloudflare/suggest-subdomain repeat')
  assert.eq(first.body.suggestion, repeat.body.suggestion, 'subdomain suggestion deterministic')

  const alpha = await api.get('/api/cloudflare/suggest-subdomain?domain=alpha.loc')
  const bravo = await api.get('/api/cloudflare/suggest-subdomain?domain=bravo.loc')
  assert.statusOk(alpha, 'GET /api/cloudflare/suggest-subdomain alpha')
  assert.statusOk(bravo, 'GET /api/cloudflare/suggest-subdomain bravo')
  assert.ok(alpha.body.suggestion !== bravo.body.suggestion, 'different domains produce different suggestions')

  await api.put('/api/cloudflare/config', {
    body: {
      tunnelName: null,
      accountId: null,
      subdomainTemplate: '{stem}-{hash}',
    },
  })
})
