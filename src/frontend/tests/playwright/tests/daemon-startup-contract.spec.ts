import { test, expect } from './_fixtures'
import { readFileSync } from 'node:fs'
import { join } from 'node:path'

const main = readFileSync(join(process.cwd(), 'electron', 'main.ts'), 'utf-8')

test.describe('Daemon startup contract', () => {
  test('a port file nobody answers on is removed before the new daemon spawns', () => {
    const inner = main.slice(main.indexOf('async function spawnDaemonInner()'))
    const aliveCheck = inner.indexOf('await isDaemonAlive()')
    const removal = inner.indexOf('unlinkSync(PORT_FILE)')
    const spawnCall = inner.indexOf('daemon = spawn(')
    expect(aliveCheck).toBeGreaterThan(-1)
    expect(removal).toBeGreaterThan(aliveCheck)
    expect(spawnCall).toBeGreaterThan(removal)
  })

  test('the renderer never sends a tokenless request and retries a stale-token 401 once', () => {
    // Before the port file exists the base URL falls back to 17280 with no token; a
    // daemon that is already bound answered 401 and the UI showed it after every start.
    const api = readFileSync(join(process.cwd(), 'src', 'api', 'daemon.ts'), 'utf-8')
    const json = api.slice(api.indexOf('async function json<T>'), api.indexOf('// Status'))
    const guard = json.indexOf('if (window.daemonApi && !daemonToken())')
    const firstSend = json.indexOf('let r = await send()')
    expect(guard).toBeGreaterThan(-1)
    expect(firstSend).toBeGreaterThan(guard)
    // The thrown error must classify as "network" in the daemon store, not "auth".
    expect(json).toMatch(/throw new TypeError\('Failed to fetch/)
    expect(json).toMatch(/r\.status === 401 && window\.daemonApi[\s\S]*?if \(daemonToken\(\) !== stale\) r = await send\(\)/)
  })

  test('the tray redraws once the daemon answers and labels a start in progress', () => {
    expect(main).toMatch(/if \(!daemonConnected \|\| !trayShowsConnected\)/)
    expect(main).toMatch(/menuText\(starting \? 'daemonStarting' : 'daemonUnavailable'\)/)
    expect(main).toMatch(/if \(\+\+attempts > 120\) clearInterval\(check\)/)
  })
})
