import { test, expect } from './_fixtures'
import { readFileSync } from 'node:fs'
import { join, resolve } from 'node:path'
import { defaultWindowState, MIN_WINDOW_HEIGHT, MIN_WINDOW_WIDTH } from '../../../electron/windowBounds'

// The first window: sized from the primary display, centred, and never smaller than the
// layout can use. The minimum is checked against the CSS it protects.

const root = resolve(process.cwd())
const read = (...parts: string[]) => readFileSync(join(root, ...parts), 'utf-8')

test.describe('Window bounds', () => {
  test('a large display gets 1280x800 centred in its work area', () => {
    expect(defaultWindowState({ x: 0, y: 0, width: 1920, height: 1040 }))
      .toEqual({ width: 1280, height: 800, x: 320, y: 120 })
  })

  test('a laptop display gets what is there, and the taskbar offset is kept', () => {
    expect(defaultWindowState({ x: 0, y: 40, width: 1366, height: 728 }))
      .toEqual({ width: 1280, height: 728, x: 43, y: 40 })
  })

  test('a work area below the minimum never starts off-screen and never goes under the minimum', () => {
    const state = defaultWindowState({ x: 100, y: 50, width: 600, height: 400 })
    expect(state.width).toBe(MIN_WINDOW_WIDTH)
    expect(state.height).toBe(MIN_WINDOW_HEIGHT)
    expect(state.x).toBe(100)
    expect(state.y).toBe(50)
  })

  test('the minimum covers what the layout needs', () => {
    // The shell cuts off below 580px of page height, and the header drops the
    // Simple/Advanced switch at 640px of width; the window adds a title bar and borders.
    const app = read('src', 'App.vue')
    const minPageHeight = Number(app.match(/\.app-root \{[^}]*min-height: (\d+)px/)?.[1])
    expect(MIN_WINDOW_HEIGHT).toBeGreaterThan(minPageHeight)
    const header = read('src', 'components', 'layout', 'AppHeader.vue')
    const switchHidesAt = Number(header.match(/@media \(max-width: (\d+)px\) \{\s*\.tray \{ display: none/)?.[1])
    expect(MIN_WINDOW_WIDTH).toBeGreaterThan(switchHidesAt)
  })

  test('the main process uses them', () => {
    const main = read('electron', 'main.ts')
    expect(main).toMatch(/defaultWindowState\(screen\.getPrimaryDisplay\(\)\.workArea\)/)
    expect(main).toMatch(/minWidth: MIN_WINDOW_WIDTH,\s*minHeight: MIN_WINDOW_HEIGHT/)
    // The old fixed first-run size.
    expect(main).not.toMatch(/width: 960, height: 640/)
  })
})
