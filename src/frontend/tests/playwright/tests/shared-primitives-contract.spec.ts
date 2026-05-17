import { test, expect } from './_fixtures'
import { readFileSync } from 'node:fs'
import { join, resolve } from 'node:path'

// Contract for plan §6 shared primitives (HealthStatusDot, BannerCallout,
// SettingsPanel, EditCard, SettingsCard). Source-level coverage catches
// API/markup regressions without a headless render — these primitives
// have many consumers, so silent API changes propagate widely.

const srcRoot = resolve(process.cwd(), 'src')
const sharedDir = join(srcRoot, 'components', 'shared')

test.describe('Plan §6 shared primitives — source contract', () => {
  test('HealthStatusDot exposes level + pulse + title props, role="img"', () => {
    const source = readFileSync(join(sharedDir, 'HealthStatusDot.vue'), 'utf-8')
    // Props contract — consumers depend on these names + level union.
    expect(source).toMatch(/level:\s*'ok'\s*\|\s*'warn'\s*\|\s*'err'\s*\|\s*'muted'/)
    expect(source).toMatch(/pulse\?:\s*boolean/)
    expect(source).toMatch(/title\?:\s*string/)
    // A11y attributes on the rendered span.
    expect(source).toMatch(/role="img"/)
    expect(source).toMatch(/:aria-label/)
  })

  test('BannerCallout exposes title + subtitle + tone + iconPulse', () => {
    const source = readFileSync(join(sharedDir, 'BannerCallout.vue'), 'utf-8')
    expect(source).toMatch(/title:\s*string/)
    expect(source).toMatch(/subtitle\?:\s*string/)
    expect(source).toMatch(/tone:\s*'warning'\s*\|\s*'info'\s*\|\s*'neutral'/)
    expect(source).toMatch(/iconPulse\?:\s*boolean/)
    // Named slots that consumers use.
    expect(source).toMatch(/name="icon"/)
    expect(source).toMatch(/name="action"/)
  })

  test('SettingsPanel exposes title + subtitle + icon + panelClass with slots', () => {
    const source = readFileSync(join(sharedDir, 'SettingsPanel.vue'), 'utf-8')
    expect(source).toMatch(/title\?:\s*string/)
    expect(source).toMatch(/subtitle\?:\s*string/)
    expect(source).toMatch(/icon\?:\s*Component/)
    expect(source).toMatch(/panelClass\?:\s*string/)
    expect(source).toMatch(/name="header"/)
    expect(source).toMatch(/name="actions"/)
  })

  test('EditCard exposes title + hint + flushBody with named slots', () => {
    const source = readFileSync(join(sharedDir, 'EditCard.vue'), 'utf-8')
    expect(source).toMatch(/title\?:\s*string/)
    expect(source).toMatch(/hint\?:\s*string/)
    expect(source).toMatch(/flushBody\?:\s*boolean/)
    expect(source).toMatch(/name="title"/)
    expect(source).toMatch(/name="hint"/)
  })

  test('SettingsCard exposes title + meta slot', () => {
    const source = readFileSync(
      resolve(srcRoot, 'components', 'settings', 'shared', 'SettingsCard.vue'),
      'utf-8',
    )
    expect(source).toMatch(/title:\s*string/)
    expect(source).toMatch(/name="meta"/)
    // settings-card classes remain in css for consumers using static class.
    expect(source).toMatch(/settings-card-title/)
    expect(source).toMatch(/settings-card-body/)
  })
})
