import { test, expect } from './_fixtures'
import { readFileSync } from 'node:fs'
import { join, resolve } from 'node:path'
import { baseCompile } from '@intlify/message-compiler'

// Source-level contract: every locale message must compile. vue-i18n compiles
// a message the first time it is used, so a stray "@" (linked-message syntax,
// e.g. in "user@host") throws at render time and takes the whole component
// with it. Literal at-signs are written {'@'}.

const localesDir = resolve(process.cwd(), 'src', 'locales')

function collectMessages(node: unknown, path: string, out: Array<[string, string]>): void {
  if (typeof node === 'string') out.push([path, node])
  else if (node && typeof node === 'object') {
    for (const [key, value] of Object.entries(node)) collectMessages(value, path ? `${path}.${key}` : key, out)
  }
}

test.describe('Locale messages contract', () => {
  for (const locale of ['cs', 'en']) {
    test(`${locale}.json: every message compiles`, () => {
      const data: unknown = JSON.parse(readFileSync(join(localesDir, `${locale}.json`), 'utf-8'))
      const messages: Array<[string, string]> = []
      collectMessages(data, '', messages)

      const broken: string[] = []
      for (const [path, message] of messages) {
        try {
          baseCompile(message, {
            mode: 'arrow',
            location: false,
            sourceMap: false,
            onError: (err) => { broken.push(`${path}: ${err.message}`) },
          })
        } catch (err) {
          broken.push(`${path}: ${err instanceof Error ? err.message : String(err)}`)
        }
      }
      expect(broken).toEqual([])
    })
  }
})
