import * as fs from 'fs'

// Iter 38 — defensive global setup that runs ONCE before any test file.
// Resets server-side settings that test specs mutate-and-restore so a
// killed-mid-test run from a prior invocation can't poison this one.
//
// Same rationale as `tools/e2e-mcp-deploy.sh` startup reset (iter 33-34):
// try/finally restore is fragile against signal kills, so pair with a
// startup wipe to a known-clean baseline. Idempotent.

export default async function globalSetup(): Promise<void> {
  // Read the daemon port + token written by the dev/electron daemon.
  const portFile = `${process.env.TEMP || 'C:\\Users\\LuRy\\AppData\\Local\\Temp'}\\nks-wdc-daemon.port`
  let port = 17280
  let token = ''
  try {
    const raw = fs.readFileSync(portFile, 'utf-8').split('\n')
    port = Number(raw[0]) || 17280
    token = (raw[1] || '').trim()
  } catch {
    // Daemon not running; tests will fail at fixture setup with a clearer message.
    console.warn(`[global-setup] could not read ${portFile} — daemon may not be running`)
    return
  }

  const baseline = {
    'mcp.always_confirm_kinds': '',
    'deploy.useLegacyHostHandlers': 'true',
    'deploy.enabled': 'true',
  }
  try {
    const r = await fetch(`http://localhost:${port}/api/settings`, {
      method: 'PUT',
      headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' },
      body: JSON.stringify(baseline),
    })
    if (!r.ok) {
      console.warn(`[global-setup] settings reset returned ${r.status}; tests may flake`)
    }
  } catch (e) {
    console.warn(`[global-setup] settings reset failed: ${(e as Error).message}`)
  }
}
