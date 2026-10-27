import { test, expect } from './_fixtures'
import { readFileSync } from 'node:fs'
import { join, resolve } from 'node:path'

// Source contract for the tool pages (Binaries, Hosts, SSL, Databases, PHP,
// Plugins, Composer, Cloudflare): the daemon/UI agreements that a headless
// render cannot reach without a running daemon.

const srcRoot = resolve(process.cwd(), 'src')
const daemonRoot = resolve(process.cwd(), '..', 'daemon', 'NKS.WebDevConsole.Daemon')
const page = (name: string) => readFileSync(join(srcRoot, 'components', 'pages', name), 'utf-8')
const daemon = (path: string) => readFileSync(join(daemonRoot, path), 'utf-8')

test.describe('Tool pages — source contract', () => {
  test('binary install streams real progress and can be cancelled', () => {
    const endpoint = daemon('Binaries/BinaryEndpoints.cs')
    expect(endpoint).toContain('new BinaryInstallProgress(sse.BroadcastAsync)')
    expect(endpoint).toContain('"binary:progress"')
    const vue = page('Binaries.vue')
    expect(vue).toContain("'binary:progress'")
    expect(vue).toMatch(/installBinary\(app, version, installAbort\.signal\)/)
    expect(vue).not.toMatch(/progressPercent\.value = (10|30|90)\b/)
  })

  test('binary install errors are worded from the daemon code, not raw HTTP text', () => {
    expect(daemon('Binaries/BinaryEndpoints.cs')).toMatch(/new \{ error = ex\.Message, code,/)
    const util = readFileSync(join(srcRoot, 'utils', 'binaries.ts'), 'utf-8')
    for (const code of ['download_failed', 'not_an_archive', 'sha_mismatch', 'no_build_for_platform']) {
      expect(util).toContain(code)
    }
    expect(page('Binaries.vue')).toContain('installErrorMessage(')
  })
})
