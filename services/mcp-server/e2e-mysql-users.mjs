#!/usr/bin/env node
// Live functional test for the MySQL user-management tools.
// Creates a throw-away user, exercises every mutation, then drops it.
// Exits non-zero on any failure. Cleans up on every exit path.

import { spawn } from 'node:child_process'
import { fileURLToPath } from 'node:url'
import { dirname, join } from 'node:path'

const __dirname = dirname(fileURLToPath(import.meta.url))
const SERVER = join(__dirname, 'dist', 'index.js')

const TEST_USER = `wdc_mcp_test_${Date.now()}`
const TEST_HOST = 'localhost'
const TEST_DB = 'mysql' // use system db that always exists for grant target

let passed = 0
let failed = 0
const failures = []

function log(status, name, detail = '') {
  const mark = status === 'PASS' ? '  +' : '  -'
  console.log(`${mark} ${name}${detail ? ' -- ' + detail : ''}`)
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
    this.child = spawn('node', [SERVER], { stdio: ['pipe', 'pipe', 'inherit'] })
    this.buf = ''
    this.pending = new Map()
    this.nextId = 1
    this.child.stdout.on('data', chunk => {
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
        } catch {
          /* ignore */
        }
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
      }, 15000)
    })
  }

  close() {
    this.child.stdin.end()
    this.child.kill()
  }
}

function parsePayload(resp) {
  const text = resp.result?.content?.[0]?.text ?? ''
  try {
    return JSON.parse(text)
  } catch {
    return { _raw: text }
  }
}

async function findUser(client, userName, host) {
  const resp = await client.send('tools/call', {
    name: 'wdc_list_mysql_users',
    arguments: {},
  })
  const payload = parsePayload(resp)
  const list = payload.users ?? []
  return list.find(u => u.userName === userName && u.host === host)
}

async function run() {
  const client = new McpClient()
  let cleanupNeeded = false
  try {
    await client.send('initialize', {
      protocolVersion: '2024-11-05',
      capabilities: {},
      clientInfo: { name: 'mysql-users-e2e', version: '1.0' },
    })
    client.child.stdin.write(
      JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n',
    )

    console.log(`\n=== MySQL user lifecycle: ${TEST_USER}@${TEST_HOST} ===`)

    // 1. Baseline — user does not exist yet
    const before = await findUser(client, TEST_USER, TEST_HOST)
    assert(before === undefined, '[1] test user does not exist before create')

    // 2. Create with initial readWrite grant on mysql
    cleanupNeeded = true
    const createResp = await client.send('tools/call', {
      name: 'wdc_create_mysql_user',
      arguments: {
        userName: TEST_USER,
        host: TEST_HOST,
        password: 'tmp_passw0rd!',
        database: TEST_DB,
        privileges: 'readWrite',
      },
    })
    const createPayload = parsePayload(createResp)
    assert(
      createResp.result && !createResp.result.isError && createPayload.success === true,
      '[2] create user (with initial grant)',
      JSON.stringify(createPayload).slice(0, 100),
    )

    // 3. List shows the new user
    const after = await findUser(client, TEST_USER, TEST_HOST)
    assert(after !== undefined, '[3] list shows newly created user')
    assert(
      after && after.accountLocked === false && after.passwordExpired === false,
      '[3b] new user not locked / not expired',
    )

    // 4. Change password
    const pwResp = await client.send('tools/call', {
      name: 'wdc_set_mysql_user_password',
      arguments: { userName: TEST_USER, host: TEST_HOST, password: 'rotated_pw!' },
    })
    const pwPayload = parsePayload(pwResp)
    assert(
      pwResp.result && !pwResp.result.isError && pwPayload.success === true,
      '[4] set user password',
      JSON.stringify(pwPayload).slice(0, 100),
    )

    // 5. Switch privileges to read-only
    const grantReadResp = await client.send('tools/call', {
      name: 'wdc_grant_mysql_user_database',
      arguments: {
        userName: TEST_USER,
        host: TEST_HOST,
        database: TEST_DB,
        privileges: 'read',
      },
    })
    const grantReadPayload = parsePayload(grantReadResp)
    assert(
      grantReadResp.result && !grantReadResp.result.isError &&
        grantReadPayload.success === true,
      '[5] grant read privileges',
      JSON.stringify(grantReadPayload).slice(0, 100),
    )

    // 6. Revoke — preset 'none'
    const grantNoneResp = await client.send('tools/call', {
      name: 'wdc_grant_mysql_user_database',
      arguments: {
        userName: TEST_USER,
        host: TEST_HOST,
        database: TEST_DB,
        privileges: 'none',
      },
    })
    const grantNonePayload = parsePayload(grantNoneResp)
    assert(
      grantNoneResp.result && !grantNoneResp.result.isError &&
        grantNonePayload.success === true,
      "[6] grant preset 'none' (no-op flush)",
      JSON.stringify(grantNonePayload).slice(0, 100),
    )

    // 7. Refuse to drop root — guard test
    const dropRootResp = await client.send('tools/call', {
      name: 'wdc_drop_mysql_user',
      arguments: { userName: 'root', host: 'localhost', confirm: 'YES' },
    })
    const dropRootPayload = parsePayload(dropRootResp)
    assert(
      (dropRootResp.result?.isError === true) ||
        (dropRootPayload.success === false &&
          /root/i.test(dropRootPayload.error ?? '')),
      "[7] daemon refuses to drop 'root' even with confirm=YES",
      JSON.stringify(dropRootPayload).slice(0, 120),
    )

    // 8. Drop the test user
    const dropResp = await client.send('tools/call', {
      name: 'wdc_drop_mysql_user',
      arguments: { userName: TEST_USER, host: TEST_HOST, confirm: 'YES' },
    })
    const dropPayload = parsePayload(dropResp)
    assert(
      dropResp.result && !dropResp.result.isError && dropPayload.success === true,
      '[8] drop test user',
      JSON.stringify(dropPayload).slice(0, 100),
    )
    cleanupNeeded = false

    // 9. List no longer shows it
    const final = await findUser(client, TEST_USER, TEST_HOST)
    assert(final === undefined, '[9] list no longer shows dropped user')

    console.log(`\n=== SUMMARY === ${passed} passed, ${failed} failed`)
    if (failed) {
      console.log('Failures:')
      for (const f of failures) console.log(`  - ${f}`)
    }
  } finally {
    // Best-effort cleanup if we crashed mid-flow.
    if (cleanupNeeded) {
      try {
        await client.send('tools/call', {
          name: 'wdc_drop_mysql_user',
          arguments: { userName: TEST_USER, host: TEST_HOST, confirm: 'YES' },
        })
        console.log(`[cleanup] dropped ${TEST_USER}@${TEST_HOST}`)
      } catch (err) {
        console.error(`[cleanup] failed to drop test user: ${err}`)
      }
    }
    client.close()
  }
  process.exit(failed ? 1 : 0)
}

run().catch(err => {
  console.error('harness failed:', err)
  process.exit(1)
})
