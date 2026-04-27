import { test, expect } from './_fixtures'

// Phase 7.4 #109-D1 — operator-facing readiness diagnostic.
// /api/admin/plugin-readiness reports whether it's safe to flip
// deploy.useLegacyHostHandlers=false. Today always readyToFlip:false
// because phases B/C/D haven't shipped — but the structured envelope
// is locked so future automation can poll this endpoint and gate the
// flip on readyToFlip:true.

test.describe('Plugin readiness diagnostic (#109-D1)', () => {
  test('returns structured envelope with mode + blockers', async ({ authedRequest }) => {
    const r = await authedRequest.get('/api/admin/plugin-readiness')
    expect(r.status()).toBe(200)
    const j = await r.json()

    // Mode mirrors the X-Wdc-Backend-Mode header — same source of truth.
    expect(['built-in', 'plugin']).toContain(j.mode)

    // Plugin presence + version pulled from PluginLoader. Today's
    // staged plugin is 0.1.0 (from webdev-console-plugins/NksDeploy).
    expect(typeof j.pluginLoaded).toBe('boolean')
    expect(j.pluginLoaded).toBe(true)
    expect(typeof j.pluginVersion).toBe('string')

    // Setting echoes — for round-trip clarity.
    expect(typeof j.useLegacyHostHandlers).toBe('boolean')
    expect(j.useLegacyHostHandlers).toBe(true)

    // readyToFlip MUST be false today (phase B/C/D not yet shipped).
    // When future operator/automation sees readyToFlip=true here, that
    // is the green light to flip the setting.
    expect(j.readyToFlip).toBe(false)

    // Blockers list explains why. Test that the 3 known blockers are
    // present so a refactor doesn't silently flip readyToFlip without
    // shipping the underlying work.
    expect(Array.isArray(j.blockers)).toBe(true)
    expect(j.blockers.length).toBeGreaterThanOrEqual(3)
    const allBlockers = j.blockers.join(' | ')
    expect(allBlockers).toContain('phase B')
    expect(allBlockers).toContain('phase C')
    expect(allBlockers).toContain('phase D')

    expect(typeof j.recommendation).toBe('string')
    expect(j.recommendation.length).toBeGreaterThan(0)
  })
})
