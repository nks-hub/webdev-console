import { test, expect } from './_fixtures'
import { readFileSync } from 'node:fs'
import { join, resolve } from 'node:path'

// Source-level contract: the shell must surface a one-click Simple ↔
// Advanced switch. It used to be the `.nav-item-mode` row in AppSidebar;
// the redesign moved it to a two-key tray in AppHeader so both states are
// visible at once. This spec regression-locks the tray so a future shell
// refactor can't silently drop it.

const srcRoot = resolve(process.cwd(), 'src')

test.describe('Shell mode toggle contract', () => {
  test('AppHeader.vue renders the Simple/Advanced tray bound to uiModeStore', () => {
    const source = readFileSync(join(srcRoot, 'components', 'layout', 'AppHeader.vue'), 'utf-8')
    // A labelled group of two toggle buttons.
    expect(source).toMatch(/class="tray" role="group"/)
    // Pressed state mirrors the store, one key per mode.
    expect(source).toMatch(/:aria-pressed="uiModeStore\.isSimple"/)
    expect(source).toMatch(/:aria-pressed="uiModeStore\.isAdvanced"/)
    // Each key sets its mode explicitly (idempotent, unlike a toggle).
    expect(source).toMatch(/uiModeStore\.setUiMode\('simple'\)/)
    expect(source).toMatch(/uiModeStore\.setUiMode\('advanced'\)/)
  })

  test('uiModeStore exposes toggleMode + setUiMode + isSimple/isAdvanced', () => {
    const source = readFileSync(join(srcRoot, 'stores', 'uiMode.ts'), 'utf-8')
    expect(source).toMatch(/toggleMode/)
    expect(source).toMatch(/setUiMode/)
    expect(source).toMatch(/isSimple/)
    expect(source).toMatch(/isAdvanced/)
    // useLocalStorage anchors the choice across reloads — critical for
    // the "remember my mode" UX promise.
    expect(source).toMatch(/useLocalStorage/)
  })

  test('i18n keys for mode label exist in both locales', () => {
    const cs = JSON.parse(
      readFileSync(join(srcRoot, 'locales', 'cs.json'), 'utf-8'),
    ) as { settings?: { mode?: { simple?: string; advanced?: string; description?: string } } }
    const en = JSON.parse(
      readFileSync(join(srcRoot, 'locales', 'en.json'), 'utf-8'),
    ) as { settings?: { mode?: { simple?: string; advanced?: string; description?: string } } }
    expect(cs.settings?.mode?.simple).toBeTruthy()
    expect(cs.settings?.mode?.advanced).toBeTruthy()
    expect(cs.settings?.mode?.description).toBeTruthy()
    expect(en.settings?.mode?.simple).toBeTruthy()
    expect(en.settings?.mode?.advanced).toBeTruthy()
    expect(en.settings?.mode?.description).toBeTruthy()
  })
})
