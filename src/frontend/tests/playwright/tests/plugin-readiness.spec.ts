import { test, expect } from './_fixtures'

// Phase 7.4 #109-D1 — operator-facing readiness diagnostic.
// /api/admin/plugin-readiness reports whether it's safe to flip
// deploy.useLegacyHostHandlers=false. Today readyToFlip:false because
// phase D (plugin-only e2e) hasn't shipped — but the structured envelope
// is locked so future automation can poll this endpoint and gate the
// flip on readyToFlip:true.
//
// Iter 54-55 cleared phases B + C from the blocker list (plugin endpoint
// parity reached + ZIP/SSE/test-hook/Slack ports landed). Only phase D
// remains as the documented blocker. Iter 56 added bootLegacyHostHandlers
// + restartPending fields so the GUI can detect setting drift from the
// boot-time value (conditional handler registration honours the boot
// value, not the live setting).

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

    // Iter 56 (#258) — boot-time value + restartPending drift indicator.
    expect(typeof j.bootLegacyHostHandlers).toBe('boolean')
    expect(typeof j.restartPending).toBe('boolean')
    // Steady state: current setting matches boot, no drift.
    expect(j.restartPending).toBe(false)
    expect(j.bootLegacyHostHandlers).toBe(j.useLegacyHostHandlers)

    // readyToFlip MUST be false today (phase D not yet shipped).
    expect(j.readyToFlip).toBe(false)

    // Phase D is the remaining blocker (B + C cleared in iters 54-55).
    expect(Array.isArray(j.blockers)).toBe(true)
    expect(j.blockers.length).toBeGreaterThanOrEqual(1)
    const allBlockers = j.blockers.join(' | ')
    expect(allBlockers).toContain('phase D')

    expect(typeof j.recommendation).toBe('string')
    expect(j.recommendation.length).toBeGreaterThan(0)

    // Iter 17: default mode must NOT include blockerDetails — back-compat
    // guard for clients written against iter 5/6 envelope shape.
    expect(j.blockerDetails).toBeUndefined()
  })

  test('explain=true returns structured blockerDetails with phase + remediation', async ({ authedRequest }) => {
    const r = await authedRequest.get('/api/admin/plugin-readiness?explain=true')
    expect(r.status()).toBe(200)
    const j = await r.json()

    // Flat blockers[] still present alongside structured details.
    expect(Array.isArray(j.blockers)).toBe(true)
    expect(Array.isArray(j.blockerDetails)).toBe(true)
    expect(j.blockerDetails.length).toBe(j.blockers.length)

    // Each detail entry has the expected shape — operators get a
    // phase tag + concrete remediation step per blocker.
    for (const d of j.blockerDetails) {
      expect(typeof d.summary).toBe('string')
      expect(typeof d.phase).toBe('string')
      expect(typeof d.remediation).toBe('string')
      expect(d.remediation.length).toBeGreaterThan(0)
    }

    // Phase D remains as the documented gating blocker.
    const phases = j.blockerDetails.map((d: { phase: string }) => d.phase).sort()
    expect(phases).toContain('D')
  })

  // Iter 56-58 (#258) — restart-pending drift detection. The conditional
  // handler registration (3 host-only endpoints in iter 55) honours the
  // boot value of useLegacyHostHandlers. When the operator flips the
  // setting at runtime, the daemon's authority doesn't change until a
  // restart. The readiness endpoint must surface this drift so the GUI
  // can show the operator a "restart to apply" hint instead of silently
  // lying about which backend serves the requests.
  test('restartPending toggles when current setting drifts from boot value', async ({ authedRequest }) => {
    // Capture the current setting so we can restore it.
    const before = await authedRequest.get('/api/settings')
    const beforeJson = await before.json()
    const original = beforeJson['deploy.useLegacyHostHandlers'] === 'false' ? 'false' : 'true'

    // Steady-state baseline.
    const baseline = await authedRequest.get('/api/admin/plugin-readiness')
    const baselineJson = await baseline.json()
    expect(baselineJson.restartPending).toBe(false)

    // Flip to opposite of boot value to force drift.
    const flipTo = original === 'true' ? 'false' : 'true'
    const putR = await authedRequest.put('/api/settings', {
      data: { 'deploy.useLegacyHostHandlers': flipTo },
    })
    expect(putR.ok()).toBe(true)

    // Drift detected: restartPending=true + recommendation mentions restart.
    const drifted = await authedRequest.get('/api/admin/plugin-readiness')
    const driftedJson = await drifted.json()
    expect(driftedJson.restartPending).toBe(true)
    expect(driftedJson.recommendation).toContain('restart')

    // Restore so the suite is self-isolated.
    await authedRequest.put('/api/settings', {
      data: { 'deploy.useLegacyHostHandlers': original },
    })
    const restored = await authedRequest.get('/api/admin/plugin-readiness')
    const restoredJson = await restored.json()
    expect(restoredJson.restartPending).toBe(false)
  })
})
