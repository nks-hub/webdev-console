/**
 * Scenario #17 — Cloudflare Tunnel config round-trip
 *
 * Verifies the Cloudflare plugin lifecycle WITHOUT requiring a real API
 * token or cloudflared binary. Tests the config persistence path:
 *
 *   1. GET /api/cloudflare/config → returns default shape (no token)
 *   2. PUT /api/cloudflare/config with test values → persists
 *   3. GET /api/cloudflare/config → redacted token + saved fields visible
 *   4. GET /api/cloudflare/suggest-subdomain?domain=test.loc → returns
 *      deterministic {stem}-{hash} with non-empty hash from InstallSalt
 *   5. Run suggest again with same domain → same result (deterministic)
 *   6. Run suggest with different domain → different hash (unique)
 *
 * Skips when Cloudflare plugin is not loaded (plugin DLL missing).
 */

import { describe, it, skip } from '../harness.mjs'

export default async function run(api) {
  describe('Cloudflare config round-trip', async () => {
    // Check plugin is loaded at all
    const plugins = await api.get('/api/plugins')
    const cfPlugin = plugins.find(p => p.id === 'nks.wdc.cloudflare')
    if (!cfPlugin) {
      skip('Cloudflare plugin not loaded')
      return
    }

    // 1. Default config
    await it('returns default config shape', async () => {
      const cfg = await api.get('/api/cloudflare/config')
      if (typeof cfg !== 'object') throw new Error('Expected object')
      // Should have at least these keys (may be null)
      const required = ['cloudflaredPath', 'tunnelToken', 'tunnelName', 'tunnelId', 'apiToken', 'accountId', 'subdomainTemplate']
      for (const key of required) {
        if (!(key in cfg)) throw new Error(`Missing key: ${key}`)
      }
    })

    // 2. Save test values
    await it('persists config via PUT', async () => {
      const result = await api.put('/api/cloudflare/config', {
        tunnelName: 'e2e-test-tunnel',
        accountId: 'test-account-id-12345',
        subdomainTemplate: '{stem}-e2e-{hash}',
      })
      if (result.tunnelName !== 'e2e-test-tunnel') throw new Error('tunnelName not saved')
      if (result.accountId !== 'test-account-id-12345') throw new Error('accountId not saved')
    })

    // 3. Read back — token should be redacted
    await it('redacts secrets on read-back', async () => {
      const cfg = await api.get('/api/cloudflare/config')
      if (cfg.tunnelName !== 'e2e-test-tunnel') throw new Error('tunnelName lost')
      // apiToken should be null or masked ("••••••••xxxx")
      if (cfg.apiToken && !cfg.apiToken.startsWith('••')) {
        throw new Error('apiToken not redacted: ' + cfg.apiToken)
      }
    })

    // 4. Subdomain suggestion — deterministic
    await it('generates deterministic subdomain suggestion', async () => {
      const r1 = await api.get('/api/cloudflare/suggest-subdomain?domain=e2etest.loc')
      if (!r1.suggestion || !r1.suggestion.startsWith('e2etest-')) {
        throw new Error(`Unexpected suggestion: ${r1.suggestion}`)
      }
      // Must contain a 6-char hex hash after the stem
      const hash = r1.suggestion.replace('e2etest-e2e-', '')
      if (!/^[0-9a-f]{6}$/.test(hash)) {
        throw new Error(`Hash part is not 6 hex chars: '${hash}'`)
      }

      // 5. Same domain → same result
      const r2 = await api.get('/api/cloudflare/suggest-subdomain?domain=e2etest.loc')
      if (r1.suggestion !== r2.suggestion) {
        throw new Error(`Non-deterministic: ${r1.suggestion} vs ${r2.suggestion}`)
      }
    })

    // 6. Different domain → different hash
    await it('produces different hash for different domains', async () => {
      const r1 = await api.get('/api/cloudflare/suggest-subdomain?domain=alpha.loc')
      const r2 = await api.get('/api/cloudflare/suggest-subdomain?domain=bravo.loc')
      if (r1.suggestion === r2.suggestion) {
        throw new Error(`Same suggestion for different domains: ${r1.suggestion}`)
      }
    })

    // Cleanup — reset test values so next run starts fresh
    await api.put('/api/cloudflare/config', {
      tunnelName: null,
      accountId: null,
      subdomainTemplate: '{stem}-{hash}',
    })
  })
}
