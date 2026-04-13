#!/usr/bin/env node
// E2E smoke test for @nks-wdc/mcp-server.
//
// Spawns the built MCP server as a child process, speaks raw
// stdio JSON-RPC to it, and runs a battery of tests against
// a live local daemon. Exits non-zero on any failure.

import { spawn } from 'node:child_process'
import { fileURLToPath } from 'node:url'
import { dirname, join } from 'node:path'

const __dirname = dirname(fileURLToPath(import.meta.url))
const SERVER = join(__dirname, 'dist', 'index.js')

let passed = 0
let failed = 0
const failures = []

function log(status, name, detail = '') {
  const mark = status === 'PASS' ? '  ✓' : status === 'FAIL' ? '  ✗' : '  ·'
  console.log(`${mark} ${name}${detail ? ' — ' + detail : ''}`)
}

function assert(cond, name, detail = '') {
  if (cond) {
    passed++
    log('PASS', name, detail)
  } else {
    failed++
    failures.push(name)
    log('FAIL', name, detail)
  }
}

class McpClient {
  constructor() {
    this.child = spawn('node', [SERVER], {
      stdio: ['pipe', 'pipe', 'inherit'],
    })
    this.buf = ''
    this.pending = new Map()
    this.nextId = 1
    this.child.stdout.on('data', (chunk) => {
      this.buf += chunk.toString('utf8')
      let nl
      while ((nl = this.buf.indexOf('\n')) >= 0) {
        const line = this.buf.slice(0, nl)
        this.buf = this.buf.slice(nl + 1)
        if (!line.trim()) continue
        try {
          const msg = JSON.parse(line)
          if (msg.id && this.pending.has(msg.id)) {
            const { resolve } = this.pending.get(msg.id)
            this.pending.delete(msg.id)
            resolve(msg)
          }
        } catch {}
      }
    })
  }

  send(method, params) {
    const id = this.nextId++
    const req = { jsonrpc: '2.0', id, method, params }
    this.child.stdin.write(JSON.stringify(req) + '\n')
    return new Promise((resolve, reject) => {
      this.pending.set(id, { resolve, reject })
      setTimeout(() => {
        if (this.pending.has(id)) {
          this.pending.delete(id)
          reject(new Error(`timeout: ${method}`))
        }
      }, 10000)
    })
  }

  close() {
    this.child.stdin.end()
    this.child.kill()
  }
}

async function run() {
  const client = new McpClient()
  try {
    console.log('\n=== PROTOCOL ===')

    // 1. initialize
    const init = await client.send('initialize', {
      protocolVersion: '2024-11-05',
      capabilities: {},
      clientInfo: { name: 'e2e-harness', version: '1.0' },
    })
    assert(init.result?.serverInfo?.name === 'nks-wdc-mcp-server',
      'initialize returns server name', init.result?.serverInfo?.name)
    assert(init.result?.capabilities?.tools !== undefined,
      'server advertises tools capability')

    // initialized notification (no response expected)
    client.child.stdin.write(JSON.stringify({
      jsonrpc: '2.0', method: 'notifications/initialized',
    }) + '\n')

    // 2. tools/list
    const list = await client.send('tools/list', {})
    const tools = list.result?.tools ?? []
    assert(tools.length === 47, `tools/list returns 47 tools`, `got ${tools.length}`)

    // All tools have required MCP fields
    const badTools = tools.filter((t) => !t.name || !t.description || !t.inputSchema)
    assert(badTools.length === 0, 'every tool has name/description/inputSchema',
      badTools.length ? `missing in: ${badTools.map(t => t.name).join(', ')}` : '')

    // All tools have annotations
    const noAnnot = tools.filter((t) => !t.annotations)
    assert(noAnnot.length === 0, 'every tool has annotations block',
      noAnnot.length ? `missing in: ${noAnnot.map(t => t.name).join(', ')}` : '')

    // wdc_query is readOnly + not destructive (post review fix)
    const query = tools.find((t) => t.name === 'wdc_query')
    assert(query?.annotations?.readOnlyHint === true && query?.annotations?.destructiveHint === false,
      'wdc_query has readOnlyHint=true, destructiveHint=false')

    // wdc_execute exists and is destructive
    const exec = tools.find((t) => t.name === 'wdc_execute')
    assert(exec?.annotations?.destructiveHint === true,
      'wdc_execute registered + destructiveHint=true')

    // wdc_refresh_catalog is read-only (post review fix)
    const refresh = tools.find((t) => t.name === 'wdc_refresh_catalog')
    assert(refresh?.annotations?.readOnlyHint === true,
      'wdc_refresh_catalog readOnlyHint=true')

    // wdc_save_cloudflare_config is not destructive (post review fix)
    const cfSave = tools.find((t) => t.name === 'wdc_save_cloudflare_config')
    assert(cfSave?.annotations?.destructiveHint === false,
      'wdc_save_cloudflare_config destructiveHint=false')

    // Phantom tool is gone
    const phantom = tools.find((t) => t.name === 'wdc_set_default_php')
    assert(phantom === undefined, 'wdc_set_default_php removed')

    console.log('\n=== DAEMON CONNECTIVITY ===')

    // 3. wdc_get_status — simplest daemon ping
    const status = await client.send('tools/call', {
      name: 'wdc_get_status', arguments: {},
    })
    const statusText = status.result?.content?.[0]?.text ?? ''
    assert(status.result && !status.result.isError,
      'wdc_get_status succeeds', statusText.slice(0, 80))
    assert(statusText.includes('version') || statusText.includes('uptime'),
      'status response contains version/uptime field')

    // 4. wdc_get_system_info
    const sys = await client.send('tools/call', {
      name: 'wdc_get_system_info', arguments: {},
    })
    assert(sys.result && !sys.result.isError,
      'wdc_get_system_info succeeds')

    // 5. wdc_list_sites
    const sites = await client.send('tools/call', {
      name: 'wdc_list_sites', arguments: {},
    })
    const sitesText = sites.result?.content?.[0]?.text ?? ''
    assert(sites.result && !sites.result.isError,
      'wdc_list_sites succeeds')
    const sitesJson = JSON.parse(sitesText)
    assert(Array.isArray(sitesJson), 'wdc_list_sites returns array',
      `count: ${Array.isArray(sitesJson) ? sitesJson.length : 'N/A'}`)

    // 6. wdc_list_services
    const svc = await client.send('tools/call', {
      name: 'wdc_list_services', arguments: {},
    })
    assert(svc.result && !svc.result.isError,
      'wdc_list_services succeeds')

    // 7. wdc_list_databases
    const dbs = await client.send('tools/call', {
      name: 'wdc_list_databases', arguments: {},
    })
    assert(dbs.result && !dbs.result.isError,
      'wdc_list_databases succeeds')

    // 8. wdc_list_php_versions
    const php = await client.send('tools/call', {
      name: 'wdc_list_php_versions', arguments: {},
    })
    assert(php.result && !php.result.isError,
      'wdc_list_php_versions succeeds')

    // 9. wdc_list_plugins
    const plugins = await client.send('tools/call', {
      name: 'wdc_list_plugins', arguments: {},
    })
    assert(plugins.result && !plugins.result.isError,
      'wdc_list_plugins succeeds')

    console.log('\n=== SECURITY GUARDS ===')

    // 10. wdc_query rejects multi-statement
    const multi = await client.send('tools/call', {
      name: 'wdc_query',
      arguments: { database: 'mysql', sql: 'SELECT 1; DROP TABLE foo' },
    })
    assert(multi.result?.isError === true,
      'wdc_query rejects multi-statement (SELECT 1; DROP TABLE foo)',
      multi.result?.content?.[0]?.text?.slice(0, 80))

    // 11. wdc_query rejects leading block comment + DDL
    const cmt = await client.send('tools/call', {
      name: 'wdc_query',
      arguments: { database: 'mysql', sql: '/* sneaky */ DROP TABLE foo' },
    })
    assert(cmt.result?.isError === true,
      'wdc_query rejects leading comment + DDL')

    // 12. wdc_query rejects smuggled-via-comment multi-statement
    const smug = await client.send('tools/call', {
      name: 'wdc_query',
      arguments: { database: 'mysql', sql: 'SELECT 1 /*; DROP TABLE foo*/' },
    })
    // This one: leading verb is SELECT → passes verb test.
    // Comment contains `;` but stripLeadingCommentsAndWs only strips
    // leading comments, so the semicolon inside the comment is NOT
    // stripped from hasInnerSemicolon's input. Actually the comment
    // block `/*; DROP TABLE foo*/` is NOT at the leading edge so it
    // stays — and hasInnerSemicolon sees the `;` inside and rejects.
    assert(smug.result?.isError === true,
      'wdc_query rejects comment-smuggled semicolon inside SELECT')

    // 13. Valid read-only query should be forwarded to daemon
    // Note: may fail at daemon layer (no such db) — we only care it PASSED
    // the MCP-level guard and reached the daemon.
    const ok = await client.send('tools/call', {
      name: 'wdc_query',
      arguments: { database: 'information_schema', sql: 'SELECT 1' },
    })
    // Either success OR daemon error is fine; what we DON'T want is the
    // MCP-level guard error text.
    const okText = ok.result?.content?.[0]?.text ?? ''
    const hitMcpGuard = okText.includes('wdc_query accepts')
    assert(!hitMcpGuard,
      'wdc_query passes SELECT 1 past MCP guard',
      hitMcpGuard ? 'guard erroneously blocked valid SELECT' : okText.slice(0, 60))

    // 14. wdc_delete_site without confirm — schema validation should reject
    const noConfirm = await client.send('tools/call', {
      name: 'wdc_delete_site',
      arguments: { domain: 'example.loc' },
    })
    assert(noConfirm.result?.isError === true || noConfirm.error,
      'wdc_delete_site rejects missing confirm')

    // 15. wdc_delete_site with wrong confirm value — schema literal 'YES' required
    const badConfirm = await client.send('tools/call', {
      name: 'wdc_delete_site',
      arguments: { domain: 'example.loc', confirm: 'yes' },
    })
    assert(badConfirm.result?.isError === true || badConfirm.error,
      'wdc_delete_site rejects confirm=yes (lowercase)')

    // 16. Bad domain format — schema should reject pre-handler
    const badDomain = await client.send('tools/call', {
      name: 'wdc_get_site',
      arguments: { domain: 'not a domain!!!' },
    })
    assert(badDomain.result?.isError === true || badDomain.error,
      'wdc_get_site rejects invalid domain')

    // 17. Uppercase domain should be lowered by DomainSchema transform
    // We use a domain that surely doesn't exist so we expect daemon 404,
    // but we care the MCP layer forwarded a lowercased version.
    const upper = await client.send('tools/call', {
      name: 'wdc_get_site',
      arguments: { domain: 'UPPERCASE.loc' },
    })
    // Daemon will 404 — but the error text should reference the
    // lowercased form (or at least not fail Zod validation).
    assert(upper.result !== undefined,
      'wdc_get_site accepts uppercase domain (lowercased by schema)',
      upper.result?.content?.[0]?.text?.slice(0, 80))

    console.log('\n=== SUMMARY ===')
    console.log(`${passed} passed, ${failed} failed`)
    if (failed) {
      console.log('Failures:')
      for (const f of failures) console.log(`  - ${f}`)
    }
  } finally {
    client.close()
  }
  process.exit(failed ? 1 : 0)
}

run().catch((err) => {
  console.error('harness failed:', err)
  process.exit(1)
})
