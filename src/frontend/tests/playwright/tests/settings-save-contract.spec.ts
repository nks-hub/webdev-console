import { test, expect } from './_fixtures'
import { readFileSync } from 'node:fs'
import { join, resolve } from 'node:path'

// Settings saves only what the user changed. Re-sending every key made the
// daemon regenerate all vhosts and reload services on each save, and the
// kept-alive page wrote back stale values (mcp.enabled) changed elsewhere.

const settings = () => readFileSync(join(resolve(process.cwd(), 'src'), 'components', 'pages', 'Settings.vue'), 'utf-8')

test.describe('Settings save contract', () => {
  test('save() sends the diff against the loaded values', () => {
    const s = settings()
    const save = s.slice(s.indexOf('async function save() {'))
    expect(save).toMatch(/const patch = changedValues\.value/)
    expect(save).toMatch(/await saveSettings\(patch\)/)
    expect(save).toMatch(/loadedValues\.value = \{ \.\.\.loadedValues\.value, \.\.\.patch \}/)
  })

  test('footer appears only with unsaved changes and leaving asks first', () => {
    const s = settings()
    expect(s).toMatch(/<template v-if="isDirty && activeTab !== 'mcp'" #footer>/)
    expect(s).toMatch(/onBeforeRouteLeave\(async \(\) => \{\s*if \(!isDirty\.value\) return true/)
    expect(s).toMatch(/@click="discardChanges"/)
  })
})
