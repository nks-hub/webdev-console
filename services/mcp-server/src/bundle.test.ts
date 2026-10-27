/**
 * The WDC installer ships the MCP server as one esbuild bundle
 * (scripts/bundle.mjs → resources/mcp-server/index.js). Build it into a
 * temp dir and drive it over stdio the way an AI client does, so a bundling
 * regression (dynamic require, missing dependency, broken ESM output) fails
 * here instead of in a packaged build.
 */

import { describe, expect, it } from 'vitest'
import { execFileSync, spawn } from 'node:child_process'
import { mkdtempSync, readFileSync, readdirSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')

interface RpcMessage { id?: number; result?: Record<string, unknown>; error?: unknown }

function registeredToolCount(): number {
  const dir = join(root, 'src', 'tools')
  return readdirSync(dir)
    .filter((f) => f.endsWith('.ts') && !f.endsWith('.test.ts'))
    .reduce((n, f) => n + (readFileSync(join(dir, f), 'utf8').match(/server\.registerTool\(/g)?.length ?? 0), 0)
}

function talk(entry: string, home: string, messages: object[], args: string[] = []): Promise<RpcMessage[]> {
  return new Promise((resolvePromise, reject) => {
    // Empty temp/home dirs: no daemon port file, so nothing reaches a real WDC.
    const child = spawn(process.execPath, [entry, ...args], {
      cwd: home,
      env: { ...process.env, TMPDIR: home, TEMP: home, TMP: home, HOME: home, USERPROFILE: home },
      stdio: ['pipe', 'pipe', 'pipe'],
    })
    const replies: RpcMessage[] = []
    let buffer = ''
    const timer = setTimeout(() => { child.kill(); reject(new Error('MCP server did not answer in time')) }, 30_000)
    child.stdout.on('data', (chunk: Buffer) => {
      buffer += chunk.toString('utf8')
      let nl: number
      while ((nl = buffer.indexOf('\n')) >= 0) {
        const line = buffer.slice(0, nl).trim()
        buffer = buffer.slice(nl + 1)
        if (!line) continue
        replies.push(JSON.parse(line) as RpcMessage)
        if (replies.filter((r) => r.id !== undefined).length === messages.filter((m) => 'id' in m).length) {
          clearTimeout(timer)
          child.kill()
          resolvePromise(replies)
        }
      }
    })
    child.on('error', reject)
    for (const m of messages) child.stdin.write(JSON.stringify(m) + '\n')
  })
}

describe('bundled MCP server', () => {
  it('builds into one file that answers initialize and lists every tool', async () => {
    const out = mkdtempSync(join(tmpdir(), 'wdc-mcp-bundle-'))
    const home = mkdtempSync(join(tmpdir(), 'wdc-mcp-home-'))
    try {
      execFileSync(process.execPath, [join(root, 'scripts', 'bundle.mjs'), '--outdir', out], { stdio: 'pipe' })
      expect(readdirSync(out).sort()).toEqual(['index.js', 'package.json'])
      expect(JSON.parse(readFileSync(join(out, 'package.json'), 'utf8')).type).toBe('module')

      const replies = await talk(join(out, 'index.js'), home, [
        { jsonrpc: '2.0', id: 1, method: 'initialize', params: { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'bundle-test', version: '1' } } },
        { jsonrpc: '2.0', method: 'notifications/initialized' },
        { jsonrpc: '2.0', id: 2, method: 'tools/list' },
      ])
      const init = replies.find((r) => r.id === 1)
      expect((init?.result?.serverInfo as { name?: string })?.name).toBe('nks-wdc-mcp-server')
      const tools = (replies.find((r) => r.id === 2)?.result?.tools ?? []) as { name: string }[]
      expect(tools.length).toBe(registeredToolCount())
      expect(tools.every((t) => t.name.startsWith('wdc_'))).toBe(true)

      // The set the Cursor snippet uses stays under Cursor's 40-tool warning
      // and still lets the agent ask for approval.
      const essential = await talk(join(out, 'index.js'), home, [
        { jsonrpc: '2.0', id: 1, method: 'initialize', params: { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'bundle-test', version: '1' } } },
        { jsonrpc: '2.0', method: 'notifications/initialized' },
        { jsonrpc: '2.0', id: 2, method: 'tools/list' },
      ], ['--modules=essential'])
      const essentialTools = ((essential.find((r) => r.id === 2)?.result?.tools ?? []) as { name: string }[]).map((t) => t.name)
      expect(essentialTools.length).toBeLessThanOrEqual(40)
      expect(essentialTools).toContain('wdc_request_confirmation')
      expect(essentialTools).toContain('wdc_create_database')
    } finally {
      rmSync(out, { recursive: true, force: true })
      rmSync(home, { recursive: true, force: true })
    }
  }, 60_000)
})
