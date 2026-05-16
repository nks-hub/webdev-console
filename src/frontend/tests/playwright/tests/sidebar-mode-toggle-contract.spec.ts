import { test, expect } from './_fixtures'
import { readFileSync } from 'node:fs'
import { join, resolve } from 'node:path'

// Source-level contract: sidebar must surface a one-click Simple ↔
// Advanced mode toggle. The earlier UX gap forced operators to drill
// into Settings → General → Mode to switch — fixed by commit 6d09c85
// adding `.nav-item-mode` in AppSidebar.vue. This spec regression-locks
// the toggle so a future sidebar refactor can't silently drop it.

const srcRoot = resolve(process.cwd(), 'src')

test.describe('Sidebar mode toggle contract', () => {
  test('AppSidebar.vue renders nav-item-mode with el-switch + toggle handler', () => {
    const source = readFileSync(join(srcRoot, 'components', 'layout', 'AppSidebar.vue'), 'utf-8')
    // The toggle row class lives only on this entry — guards against
    // accidental renames during a redesign.
    expect(source).toMatch(/class="nav-item nav-item-mode"/)
    // el-switch model-value must reflect `uiModeStore.isAdvanced` so the
    // pill state matches the active mode.
    expect(source).toMatch(/uiModeStore\.isAdvanced/)
    // Click on the row container must call toggleMode() — keyboard +
    // mouse parity comes from el-switch's built-in a11y.
    expect(source).toMatch(/uiModeStore\.toggleMode\(\)/)
    // Icon component swaps based on mode so the user sees the next-state
    // hint, not the current state.
    expect(source).toMatch(/uiModeStore\.isAdvanced \? Operation : MagicStick/)
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
