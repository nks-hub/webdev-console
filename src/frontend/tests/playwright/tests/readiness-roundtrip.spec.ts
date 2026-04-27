import { test, expect } from './_fixtures'

// Phase 7.4 #109-D1+ iter 11 — readiness ↔ settings round-trip contract.
//
// Settings.vue's locked toggle binds disabled state to readiness.readyToFlip,
// and DeploySettingsPanel.vue's popover surfaces readiness.blockers. Both
// trust the daemon to echo the current `deploy.useLegacyHostHandlers` value
// in the readiness payload so the UI never drifts from the persisted
// setting.
//
// This spec exercises that contract end-to-end:
//   1. PUT /api/settings flips useLegacyHostHandlers to a non-default value
//   2. GET /api/admin/plugin-readiness now echoes that value back
//   3. Restore original value (always, even if assertion fails)
//
// If a future refactor decouples the readiness echo from SettingsStore,
// the UI's lock state will drift silently — this test catches that loud.

test.describe('Readiness ↔ settings round-trip (#109-D1+)', () => {
  test('useLegacyHostHandlers flip echoes through plugin-readiness', async ({ authedRequest }) => {
    // Iter 34 — defensive baseline reset. If a previous test run was
    // killed mid-test (signal interrupt), the setting may still be 'false'
    // in daemon SQLite. Force baseline to default before our assertions
    // so the test is hermetic against prior-run bleed. Idempotent.
    await authedRequest.put('/api/settings', {
      data: { 'deploy.useLegacyHostHandlers': 'true' },
    })

    const before = await authedRequest.get('/api/settings')
    const beforeJson = await before.json()
    const original = beforeJson['deploy.useLegacyHostHandlers']

    try {
      // Baseline: readiness should echo whatever the current setting is.
      const baselineReadiness = await authedRequest.get('/api/admin/plugin-readiness')
      expect(baselineReadiness.status()).toBe(200)
      const baselineJson = await baselineReadiness.json()
      // Default value is true (locked-on legacy mode).
      expect(baselineJson.useLegacyHostHandlers).toBe(true)

      // Flip to false (informational today — middleware doesn't gate yet,
      // but the setting must round-trip + readiness must echo).
      const flipOff = await authedRequest.put('/api/settings', {
        data: { 'deploy.useLegacyHostHandlers': 'false' },
      })
      expect(flipOff.status()).toBe(200)

      // Readiness now reflects the flipped value.
      const afterFlip = await authedRequest.get('/api/admin/plugin-readiness')
      expect(afterFlip.status()).toBe(200)
      const afterFlipJson = await afterFlip.json()
      expect(afterFlipJson.useLegacyHostHandlers).toBe(false)

      // readyToFlip is STILL false today — flipping the setting alone
      // doesn't satisfy phase D (plugin-only e2e) blocker. Phases B + C
      // were cleared in iters 54-55 (plugin endpoint parity + ZIP/SSE/
      // test-hook/Slack ports landed). The setting flip is the RESULT
      // of readiness, not a cause of it — verify the lock logic can't
      // be bypassed by writing the setting directly.
      expect(afterFlipJson.readyToFlip).toBe(false)
      expect(afterFlipJson.blockers.length).toBeGreaterThanOrEqual(1)

      // Flip back to true.
      const flipOn = await authedRequest.put('/api/settings', {
        data: { 'deploy.useLegacyHostHandlers': 'true' },
      })
      expect(flipOn.status()).toBe(200)

      // Readiness echoes again.
      const afterRestore = await authedRequest.get('/api/admin/plugin-readiness')
      const afterRestoreJson = await afterRestore.json()
      expect(afterRestoreJson.useLegacyHostHandlers).toBe(true)
    } finally {
      // Always restore the original value so tests stay isolated.
      const restoreValue = original === undefined ? 'true' : String(original)
      await authedRequest.put('/api/settings', {
        data: { 'deploy.useLegacyHostHandlers': restoreValue },
      })
    }
  })
})
