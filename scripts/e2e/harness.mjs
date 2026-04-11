/**
 * NKS WebDev Console — E2E integration test harness (library).
 *
 * Shared utilities for running the 15 scenarios from docs/e2e-scenarios.md
 * against a real, running daemon. Pure Node built-ins only (no deps).
 *
 * Usage (from a scenario module):
 *
 *   import { scenario, api, assert, tmpDir } from './harness.mjs'
 *   export default scenario('3', 'Static HTML', 'P0', async (ctx) => {
 *     const res = await api.post('/api/sites', { body: {...} })
 *     assert.eq(res.status, 201, 'site create')
 *     ctx.cleanup(() => api.delete('/api/sites/static-e2e.loc'))
 *   })
 */

import { readFileSync, existsSync, mkdirSync, writeFileSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import http from 'node:http'

// ---------- port file + auth ----------
function readPortFile() {
  const p = join(tmpdir(), 'nks-wdc-daemon.port')
  if (!existsSync(p)) throw new Error(`Port file not found: ${p} — is the daemon running?`)
  const lines = readFileSync(p, 'utf-8').split('\n').filter(Boolean)
  if (lines.length < 2) throw new Error('Malformed port file (expected port\\ntoken)')
  return { port: parseInt(lines[0], 10), token: lines[1] }
}

let _conn = null
export function getConnection() {
  if (!_conn) _conn = readPortFile()
  return _conn
}

// Forces a fresh port-file read after daemon restart (scenario 13).
export function refreshConnection() {
  _conn = null
  return getConnection()
}

// ---------- HTTP client ----------
function doRequest(method, path, { body, headers, timeoutMs = 30000, rawBody } = {}) {
  const { port, token } = getConnection()
  return new Promise((resolve, reject) => {
    const payload =
      rawBody !== undefined
        ? rawBody
        : body !== undefined
          ? JSON.stringify(body)
          : undefined
    const reqHeaders = {
      Authorization: `Bearer ${token}`,
      Accept: 'application/json',
      ...(headers ?? {}),
    }
    if (payload !== undefined && !reqHeaders['Content-Type']) {
      reqHeaders['Content-Type'] = 'application/json'
    }
    if (payload !== undefined) {
      reqHeaders['Content-Length'] = Buffer.byteLength(payload)
    }
    const req = http.request(
      { hostname: '127.0.0.1', port, path, method, headers: reqHeaders },
      (res) => {
        const chunks = []
        res.on('data', (c) => chunks.push(c))
        res.on('end', () => {
          const raw = Buffer.concat(chunks).toString('utf-8')
          let parsed = raw
          if (res.headers['content-type']?.includes('application/json') && raw.length) {
            try { parsed = JSON.parse(raw) } catch { /* keep raw */ }
          }
          resolve({ status: res.statusCode ?? 0, body: parsed, raw, headers: res.headers })
        })
      }
    )
    req.on('error', reject)
    req.setTimeout(timeoutMs, () => {
      req.destroy(new Error(`${method} ${path} timed out after ${timeoutMs}ms`))
    })
    if (payload !== undefined) req.write(payload)
    req.end()
  })
}

export const api = {
  get: (path, opts) => doRequest('GET', path, opts),
  post: (path, opts) => doRequest('POST', path, opts),
  put: (path, opts) => doRequest('PUT', path, opts),
  delete: (path, opts) => doRequest('DELETE', path, opts),
  // Unauthenticated — for /healthz probing during crash recovery.
  healthz: () =>
    new Promise((resolve, reject) => {
      const { port } = getConnection()
      const req = http.get({ hostname: '127.0.0.1', port, path: '/healthz', timeout: 3000 }, (res) => {
        res.on('data', () => {})
        res.on('end', () => resolve(res.statusCode ?? 0))
      })
      req.on('error', reject)
      req.on('timeout', () => req.destroy(new Error('healthz timeout')))
    }),
}

// ---------- Assertions ----------
class AssertionError extends Error {
  constructor(msg) { super(msg); this.name = 'AssertionError' }
}

export const assert = {
  eq(actual, expected, msg) {
    if (actual !== expected) {
      throw new AssertionError(`${msg}: expected ${JSON.stringify(expected)}, got ${JSON.stringify(actual)}`)
    }
  },
  ok(cond, msg) {
    if (!cond) throw new AssertionError(`${msg}: expected truthy, got ${JSON.stringify(cond)}`)
  },
  statusOk(res, msg) {
    if (res.status < 200 || res.status >= 300) {
      throw new AssertionError(`${msg}: HTTP ${res.status} — ${JSON.stringify(res.body).slice(0, 200)}`)
    }
  },
  contains(haystack, needle, msg) {
    if (typeof haystack !== 'string' || !haystack.includes(needle)) {
      throw new AssertionError(`${msg}: expected to contain ${JSON.stringify(needle)}, got ${JSON.stringify(haystack).slice(0, 200)}`)
    }
  },
  notContains(haystack, needle, msg) {
    if (typeof haystack === 'string' && haystack.includes(needle)) {
      throw new AssertionError(`${msg}: expected NOT to contain ${JSON.stringify(needle)}`)
    }
  },
}

// ---------- Temp dirs ----------
export function tmpDir(prefix) {
  const d = join(tmpdir(), `wdc-e2e-${prefix}-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`)
  mkdirSync(d, { recursive: true })
  return d
}
export function writeFile(path, content) { writeFileSync(path, content) }
export function rmTree(path) {
  try { rmSync(path, { recursive: true, force: true }) } catch { /* ignore */ }
}

// ---------- Scenario definition ----------
/**
 * @param {string} id  e.g. '3'
 * @param {string} name  e.g. 'Static HTML (no PHP)'
 * @param {'P0'|'P1'|'P2'} priority
 * @param {(ctx: { cleanup: (fn: () => any) => void, skip: (reason: string) => never }) => Promise<void>} run
 */
export function scenario(id, name, priority, run) {
  return { id, name, priority, run }
}

export class SkipError extends Error {
  constructor(reason) { super(reason); this.name = 'SkipError' }
}

export function sleep(ms) { return new Promise((r) => setTimeout(r, ms)) }
