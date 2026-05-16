import { test, expect } from './_fixtures'
import { readFileSync } from 'node:fs'
import { join, resolve } from 'node:path'

// Source-level regression-lock for commit 801883d. The daemon's
// `{site, warnings, hints}` wrapper response (bind-IP NIC warning,
// framework auto-detect hint) MUST reach the operator. Before the
// fix, `sitesStore.create()` silently dropped the wrapper because the
// API helper typed the response as bare SiteInfo.
//
// Without this regression lock a future refactor that tightens the
// SiteInfo type or simplifies the store could re-introduce the bug.

const srcRoot = resolve(process.cwd(), 'src')

test.describe('Daemon warnings wrapper contract', () => {
  test('sitesStore.create unwraps { site, warnings, hints }', () => {
    const source = readFileSync(join(srcRoot, 'stores', 'sites.ts'), 'utf-8')
    // The unwrap line must check for nested .site before falling back
    // to the raw response. Catches a regression where someone simplifies
    // the unwrap and forgets the fallback.
    expect(source).toMatch(/\.site\s*\?\?\s*\(raw as SiteInfo\)/)
    // Side-channel refs must exist and be returned from the store.
    expect(source).toMatch(/lastCreateWarnings\s*=\s*ref/)
    expect(source).toMatch(/lastCreateHints\s*=\s*ref/)
    expect(source).toMatch(/return\s*{[\s\S]*lastCreateWarnings[\s\S]*lastCreateHints[\s\S]*}/)
  })

  test('Sites.vue createSite toasts each warning + hint', () => {
    const source = readFileSync(join(srcRoot, 'components', 'pages', 'Sites.vue'), 'utf-8')
    // The loop over lastCreateWarnings must call ElMessage.warning
    // for each entry. Without the loop, a daemon warning for a bogus
    // bind IP would silently disappear again.
    expect(source).toMatch(/for\s*\(const\s+w\s+of\s+sitesStore\.lastCreateWarnings\)/)
    expect(source).toMatch(/for\s*\(const\s+h\s+of\s+sitesStore\.lastCreateHints\)/)
    expect(source).toMatch(/ElMessage\.warning\(\{\s*message:\s*w/)
    expect(source).toMatch(/ElMessage\.info\(\{\s*message:\s*h/)
  })

  test('Daemon Program.cs emits wrapper for POST /api/sites with warnings', () => {
    // Read the daemon Program.cs from the workspace root (one up from
    // src/frontend/). The contract: the wrapped shape `Results.Created(
    // ..., new { site, warnings, hints })` is emitted when warnings or
    // hints exist. Source-level check keeps the API surface stable so
    // the frontend unwrap above doesn't need to change.
    const programPath = resolve(srcRoot, '..', '..', 'daemon', 'NKS.WebDevConsole.Daemon', 'Program.cs')
    const source = readFileSync(programPath, 'utf-8')
    // Look for the wrapped Results.Created call with `site`, `warnings`,
    // and `hints` keys.
    expect(source).toMatch(/Results\.Created\([^)]+,\s*new\s*\{\s*site\s*=\s*created\s*,\s*warnings\s*,\s*hints\s*,\s*\}\s*\)/)
    // CollectBindAddressWarnings must be invoked to populate `warnings`
    // — that's what produces the NIC-not-assigned message.
    expect(source).toMatch(/SiteManager\.CollectBindAddressWarnings\(created\)/)
  })
})
