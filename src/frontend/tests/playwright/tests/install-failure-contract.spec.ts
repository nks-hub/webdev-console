import { test, expect } from './_fixtures'
import { readFileSync } from 'node:fs'
import { join, resolve } from 'node:path'

// The onboarding wizard words a failed install with the shared binaries helper
// and keeps the way out (another version, the Binaries page) next to the error.

const srcRoot = resolve(process.cwd(), 'src')

test.describe('Onboarding install failure', () => {
  test('wizard offers a version picker and a way out for every pickable app', () => {
    const wizard = readFileSync(join(srcRoot, 'components', 'shared', 'OnboardingWizard.vue'), 'utf-8')
    for (const app of ["'apache'", "'php'", "'mysql'"]) expect(wizard).toContain(`app: ${app}`)
    expect(wizard).toMatch(/installErrorMessage\(t, e, app, version\)/)
    expect(wizard).toMatch(/onboarding\.err\.tryAnother/)
    expect(wizard).toMatch(/onboarding\.err\.openBinaries/)
    expect(wizard).toMatch(/'binary:installed'/)
    for (const locale of ['cs', 'en']) {
      const data = JSON.parse(readFileSync(join(srcRoot, 'locales', `${locale}.json`), 'utf-8')) as {
        onboarding: { err: Record<string, string> }
      }
      for (const key of ['details', 'tryAnother', 'openBinaries']) {
        expect(data.onboarding.err[key], `${locale} onboarding.err.${key}`).toBeTruthy()
      }
    }
  })
})
