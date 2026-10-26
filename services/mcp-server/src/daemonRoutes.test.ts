/**
 * Keeps daemon-routes.json in sync with the daemon calls the tools make.
 *
 * The daemon gives the MCP token an allow-list of routes
 * (McpTokenPolicy.AllowedRoutes in the daemon). McpTokenPolicyTests checks
 * that list against daemon-routes.json; this test checks daemon-routes.json
 * against every daemonClient call in src/tools/*.ts. A tool that starts
 * calling a new route fails here until the route is added to both lists.
 */

import { readFileSync, readdirSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'

const here = dirname(fileURLToPath(import.meta.url))
const toolsDir = join(here, 'tools')
const fixturePath = join(here, '..', 'daemon-routes.json')

const CALL = /daemonClient\.(get|post|put|delete)\s*(?:<[^>]*>)?\s*\(\s*(['`])([^'`]*)\2/g
const ANY_CALL = /daemonClient\.(get|post|put|delete)\s*(?:<[^>]*>)?\s*\(/g

function canonical(path: string): string {
  return path
    .split('?')[0]!
    .replace(/\$?\{[^}]*\}/g, '{*}')
    .replace(/\/+$/, '')
    .toLowerCase()
}

function routesCalledByTools(): { routes: Set<string>; unparsed: string[] } {
  const routes = new Set<string>()
  const unparsed: string[] = []
  const files = readdirSync(toolsDir).filter(f => f.endsWith('.ts') && !f.endsWith('.test.ts'))
  for (const file of files) {
    let src = readFileSync(join(toolsDir, file), 'utf8')
    const prefix = src.match(/const PREFIX = '([^']+)'/)
    if (prefix) src = src.replaceAll('${PREFIX}', prefix[1]!)
    const total = [...src.matchAll(ANY_CALL)].length
    const calls = [...src.matchAll(CALL)]
    if (calls.length !== total) unparsed.push(`${file}: ${total - calls.length} call(s) without a literal path`)
    for (const m of calls) routes.add(`${m[1]!.toUpperCase()} ${canonical(m[3]!)}`)
  }
  return { routes, unparsed }
}

describe('daemon-routes.json', () => {
  const fixture = JSON.parse(readFileSync(fixturePath, 'utf8')) as { routes: string[] }

  it('every daemonClient call in the tools uses a literal path', () => {
    expect(routesCalledByTools().unparsed).toEqual([])
  })

  it('lists exactly the routes the tools call', () => {
    const called = [...routesCalledByTools().routes].sort()
    const listed = fixture.routes.map(r => {
      const [method, path] = r.split(' ')
      return `${method!.toUpperCase()} ${canonical(path!)}`
    }).sort()
    expect(listed).toEqual(called)
  })
})
