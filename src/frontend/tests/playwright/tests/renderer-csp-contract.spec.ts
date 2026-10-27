import { test, expect } from './_fixtures'
import { readFileSync } from 'node:fs'
import { join, resolve } from 'node:path'

// Source-level contract: the renderer ships a Content Security Policy that
// only runs the app's own bundle. A token-holding desktop UI must not turn
// an HTML injection into script execution.

const srcRoot = resolve(process.cwd(), 'src')

test.describe('Renderer CSP contract', () => {
  const csp = () => {
    const html = readFileSync(join(srcRoot, 'index.html'), 'utf-8')
    const m = html.match(/http-equiv="Content-Security-Policy"\s+content="([^"]+)"/)
    expect(m, 'CSP meta tag present').not.toBeNull()
    return m![1]
  }

  test('scripts come only from the app itself', () => {
    const script = csp().match(/script-src ([^;]+)/)![1]
    expect(script.trim()).toBe("'self'")
  })

  test('no plugins, frames or foreign form posts', () => {
    const c = csp()
    expect(c).toMatch(/object-src 'none'/)
    expect(c).toMatch(/frame-src 'none'/)
    expect(c).toMatch(/form-action 'none'/)
    expect(c).toMatch(/base-uri 'self'/)
  })

  test('plain-HTTP network access is limited to loopback', () => {
    const connect = csp().match(/connect-src ([^;]+)/)![1].split(/\s+/)
    for (const src of connect.filter(s => s.startsWith('http:') || s.startsWith('ws:'))) {
      expect(src).toMatch(/^(http|ws):\/\/(127\.0\.0\.1|localhost):\*$/)
    }
  })
})
