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
})
