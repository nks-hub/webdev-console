import { test, expect } from './_fixtures'
import { readFileSync } from 'node:fs'
import { join, resolve } from 'node:path'

// Source-level contract: the tray, application and context menus (and the
// dialogs they raise) get their texts from the mainMenu locale section, in
// both languages, instead of English literals in the Electron main process.

const root = resolve(process.cwd())
const main = readFileSync(join(root, 'electron', 'main.ts'), 'utf-8')
const locale = (name: string) =>
  JSON.parse(readFileSync(join(root, 'src', 'locales', `${name}.json`), 'utf-8')) as { mainMenu: Record<string, unknown> }

function lookup(table: Record<string, unknown>, path: string): unknown {
  return path.split('.').reduce<unknown>((node, part) => (node as Record<string, unknown> | undefined)?.[part], table)
}

test.describe('Tray menu contract', () => {
  test('every menuText key used in main.ts exists in cs and en', () => {
    const keys = [...main.matchAll(/menuText\('([\w.]+)'/g)].map(m => m[1])
    expect(keys.length).toBeGreaterThan(30)
    for (const name of ['cs', 'en']) {
      const table = locale(name).mainMenu
      for (const key of keys) expect(typeof lookup(table, key), `${name} mainMenu.${key}`).toBe('string')
      // Keys built at runtime: service states and updater statuses.
      for (const state of ['stopped', 'starting', 'running', 'stopping', 'crashed', 'disabled', 'unknown']) {
        expect(typeof lookup(table, `state.${state}`), `${name} state.${state}`).toBe('string')
      }
      for (const status of ['idle', 'checking', 'none', 'checkFailed', 'disabled', 'downloadingVersion', 'upToDate',
        'downloadingPercent', 'ready', 'error', 'unavailable']) {
        expect(typeof lookup(table, `updaterState.${status}`), `${name} updaterState.${status}`).toBe('string')
      }
    }
  })

  test('no English literals are left in tray or context menu labels', () => {
    const literals = [...main.matchAll(/\blabel: '([^']+)'/g)].map(m => m[1])
    expect(literals).toEqual([])
    expect(main).not.toMatch(/title: 'Service action failed'|title: 'Update failed'/)
  })

  test('the menus are rebuilt when the UI language changes', () => {
    expect(main).toMatch(/onLocaleChange\(/)
    expect(main).toMatch(/function installApplicationMenu/)
  })
})
