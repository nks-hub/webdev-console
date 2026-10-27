import { test, expect } from './_fixtures'
import { readFileSync } from 'node:fs'
import { join, resolve } from 'node:path'
import { classifyInstallFailure } from '../../../src/utils/installFailure'

// The onboarding wizard words a failed install from the daemon's error:
// by the machine-readable `code` when present, otherwise by the message text.

const srcRoot = resolve(process.cwd(), 'src')

test.describe('Install failure classification', () => {
  test('upstream 403 and 404 from the daemon message text', () => {
    expect(classifyInstallFailure(new Error(
      'HTTP 502 Bad Gateway: Download of mysql 9.7.1 failed: https://example.invalid/x.zip answered 403 Forbidden. Try another version.',
    ))).toMatchObject({ kind: 'forbidden', status: 403 })
    expect(classifyInstallFailure(new Error('Response status code does not indicate success: 404 (Not Found).')))
      .toMatchObject({ kind: 'notFound', status: 404 })
    expect(classifyInstallFailure(new Error('answered 503 Service Unavailable'))).toMatchObject({ kind: 'httpStatus', status: 503 })
  })

  test('a web page or broken archive instead of the download', () => {
    expect(classifyInstallFailure(new Error('https://x returned a web page instead of the download')).kind).toBe('notAnArchive')
    expect(classifyInstallFailure(new Error('End of Central Directory record could not be found.')).kind).toBe('notAnArchive')
  })

  test('the daemon code wins over the message text', () => {
    const coded = Object.assign(new Error('HTTP 502 Bad Gateway: whatever'), {
      code: 'download_failed',
      body: { upstreamStatus: 403 },
    })
    expect(classifyInstallFailure(coded)).toMatchObject({ kind: 'forbidden', status: 403 })
    expect(classifyInstallFailure(Object.assign(new Error('x'), { code: 'sha_mismatch' })).kind).toBe('shaMismatch')
    expect(classifyInstallFailure(Object.assign(new Error('x'), { code: 'no_build_for_platform' })).kind).toBe('noBuild')
  })

  test('cancelled, unreachable and unknown failures', () => {
    expect(classifyInstallFailure(Object.assign(new Error('The operation was aborted'), { name: 'AbortError' })).kind).toBe('cancelled')
    expect(classifyInstallFailure(new Error('Failed to fetch')).kind).toBe('unreachable')
    expect(classifyInstallFailure(new Error('HTTP 500 Internal Server Error: boom')))
      .toMatchObject({ kind: 'generic', detail: 'boom' })
  })

  test('wizard offers a version picker and a way out for every pickable app', () => {
    const wizard = readFileSync(join(srcRoot, 'components', 'shared', 'OnboardingWizard.vue'), 'utf-8')
    for (const app of ["'apache'", "'php'", "'mysql'"]) expect(wizard).toContain(`app: ${app}`)
    expect(wizard).toMatch(/onboarding\.err\.tryAnother/)
    expect(wizard).toMatch(/onboarding\.err\.openBinaries/)
    expect(wizard).toMatch(/'binary:installed'/)
    for (const locale of ['cs', 'en']) {
      const data = JSON.parse(readFileSync(join(srcRoot, 'locales', `${locale}.json`), 'utf-8')) as {
        onboarding: { err: Record<string, string> }
      }
      for (const key of ['forbidden', 'notFound', 'httpStatus', 'notAnArchive', 'shaMismatch', 'noBuild',
        'unreachable', 'cancelled', 'generic', 'details', 'tryAnother', 'openBinaries']) {
        expect(data.onboarding.err[key], `${locale} onboarding.err.${key}`).toBeTruthy()
      }
    }
  })
})
