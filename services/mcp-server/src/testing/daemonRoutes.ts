// Route table of the .NET daemon, read from its minimal-API registrations
// (`app.MapGet("/api/...", ...)` and friends) under src/daemon. Contract
// tests check every daemon call a tool makes against it, so a tool that
// targets a route the daemon no longer has fails in CI instead of at runtime.

import { readdirSync, readFileSync, statSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

export interface DaemonRoute {
  method: string
  pattern: string
  regex: RegExp
}

const here = dirname(fileURLToPath(import.meta.url))
/** services/mcp-server/src/testing → repository root → src/daemon */
export const DAEMON_SRC = join(here, '..', '..', '..', '..', 'src', 'daemon')

const MAP_CALL = /\.Map(Get|Post|Put|Delete|Patch)\(\s*"([^"]+)"/g

function csFiles(dir: string): string[] {
  const out: string[] = []
  for (const name of readdirSync(dir)) {
    if (name === 'bin' || name === 'obj') continue
    const full = join(dir, name)
    if (statSync(full).isDirectory()) out.push(...csFiles(full))
    else if (name.endsWith('.cs')) out.push(full)
  }
  return out
}

function toRegex(pattern: string): RegExp {
  const body = pattern
    .split(/(\{[^}]+\})/)
    .map(part => {
      if (!part.startsWith('{')) return part.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
      return part.startsWith('{*') || part.startsWith('{**') ? '.+' : '[^/]+'
    })
    .join('')
  return new RegExp(`^${body}/?$`, 'i')
}

let cache: DaemonRoute[] | undefined

export function daemonRoutes(): DaemonRoute[] {
  if (cache) return cache
  const routes: DaemonRoute[] = []
  for (const file of csFiles(DAEMON_SRC)) {
    const src = readFileSync(file, 'utf8')
    for (const m of src.matchAll(MAP_CALL)) {
      routes.push({ method: m[1]!.toUpperCase(), pattern: m[2]!, regex: toRegex(m[2]!) })
    }
  }
  cache = routes
  return routes
}

/** The daemon route a call would reach, or undefined when there is none. */
export function matchRoute(method: string, path: string): DaemonRoute | undefined {
  const bare = path.split('?')[0]!
  return daemonRoutes().find(r => r.method === method.toUpperCase() && r.regex.test(bare))
}
