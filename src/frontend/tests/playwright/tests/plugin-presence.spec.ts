import { test, expect } from './_fixtures'

// Phase 7.4 #109-A3 — plugin migration smoke test, live-asserted.
//
// `nks.wdc.deploy` plugin DLL is staged in build/plugins/ and the
// daemon's PluginLoader picks it up at boot. The plugin's 15 endpoint
// declarations all conflict with Program.cs's host-side handlers and
// are correctly skipped by the WireEndpoints route-conflict guard
// (commit 6cdedce — canonicalize {param} on both sides).
//
// This spec asserts the plugin presence + version on /api/plugins so a
// regression that:
//   (a) breaks the plugin DLL load
//   (b) breaks PluginLoader discovery
//   (c) accidentally allows plugin endpoints to win over host handlers
// gets caught loud. Without this assertion, the migration progress
// checkpoint is invisible to CI.

test.describe('nks.wdc.deploy plugin presence', () => {
  test('plugin discovered + loaded from build/plugins', async ({ authedRequest }) => {
    const r = await authedRequest.get('/api/plugins')
    expect(r.status()).toBe(200)
    const plugins = await r.json()
    expect(Array.isArray(plugins)).toBe(true)

    const nksDeploy = plugins.find((p: { id: string }) => p.id === 'nks.wdc.deploy')
    expect(nksDeploy, 'nks.wdc.deploy plugin must be loaded').toBeDefined()
    expect(typeof nksDeploy.version).toBe('string')
    expect(nksDeploy.version.length).toBeGreaterThan(0)
    expect(nksDeploy.enabled).toBe(true)
  })

  test('host handlers still own the deploy routes (plugin endpoints skipped)', async ({ authedRequest }) => {
    // The plugin registers its own endpoints under /api/nks.wdc.deploy/*,
    // but PluginLoader.WireEndpoints skips any plugin endpoint whose
    // (METHOD, canonicalized path) matches a host route. These three
    // routes have different parameter-name shapes from the plugin's
    // declarations, exercising the canonicalization fix:
    //
    //   plugin path: /sites/{domain}/snapshots/{deployId}/restore
    //   host path:   /sites/{domain}/snapshots/{snapshotId}/restore
    //
    // Both match the same URL set; raw-text comparison would miss the
    // conflict and let plugin win, breaking the host's *restore* sniff
    // logic. Canonicalized comparison correctly skips the plugin route.
    //
    // We verify host wins by issuing requests that the plugin's variant
    // would handle differently (e.g. plugin's restore returns
    // {restored:true} on success; host returns a richer shape with
    // extractedTo + swappedTo + error). Bogus snapshot id → host returns
    // 404 with snapshot_not_found envelope. Plugin would return a
    // different not-found shape.

    const r = await authedRequest.post(
      '/api/nks.wdc.deploy/sites/blog.loc/snapshots/some-bogus-id/restore',
      { data: { confirm: true, host: 'production' } }
    )
    expect([404, 400]).toContain(r.status())
    const j = await r.json()
    // Host returns `error: snapshot_not_found` (404) or `error: confirm_required`
    // (without confirm:true). Either way, NOT plugin's `error: deploy_not_found`.
    expect(['snapshot_not_found', 'confirm_required', 'snapshot_has_no_backup']).toContain(j.error)
  })
})
