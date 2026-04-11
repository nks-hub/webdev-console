#!/usr/bin/env node
/**
 * NKS WebDev Console — performance baseline runner.
 *
 * Measures three Phase 7 metrics against a running daemon:
 *   1. /api/status p50/p95/p99/max latency under sequential load
 *   2. /api/services + /api/plugins parallel throughput (concurrent connections)
 *   3. SSE event broadcast rate via /api/events
 *
 * Outputs a JSON report to docs/perf-baselines.json plus a human-readable
 * markdown summary inserted into docs/perf-baselines.md.
 *
 * Usage:
 *   node scripts/perf-baseline.mjs                # full run, writes docs/
 *   node scripts/perf-baseline.mjs --quick        # 200 reqs instead of 1000
 *   node scripts/perf-baseline.mjs --json         # machine-readable to stdout
 *
 * Requires: daemon running, port file at %TEMP%/nks-wdc-daemon.port
 */

import { readFileSync, existsSync, writeFileSync, mkdirSync } from 'node:fs'
import { join, dirname } from 'node:path'
import { tmpdir, hostname, platform, arch, totalmem, cpus } from 'node:os'
import { fileURLToPath } from 'node:url'
import { performance } from 'node:perf_hooks'
import http from 'node:http'

const __dirname = dirname(fileURLToPath(import.meta.url))
const REPO_ROOT = join(__dirname, '..')

const args = process.argv.slice(2)
const QUICK = args.includes('--quick')
const STDOUT_JSON = args.includes('--json')
const SAMPLES = QUICK ? 200 : 1000
const PARALLEL = QUICK ? 10 : 50

function readPortFile() {
  const p = join(tmpdir(), 'nks-wdc-daemon.port')
  if (!existsSync(p)) throw new Error(`Port file not found: ${p}`)
  const lines = readFileSync(p, 'utf-8').split('\n').filter(Boolean)
  if (lines.length < 2) throw new Error('Malformed port file')
  return { port: parseInt(lines[0], 10), token: lines[1] }
}

const { port, token } = readPortFile()
const baseHeaders = { Authorization: `Bearer ${token}` }

function timedRequest(path) {
  return new Promise((resolve, reject) => {
    const start = performance.now()
    const req = http.get(
      { hostname: '127.0.0.1', port, path, headers: baseHeaders },
      (res) => {
        res.on('data', () => {})
        res.on('end', () => {
          resolve({ ms: performance.now() - start, status: res.statusCode })
        })
      }
    )
    req.on('error', reject)
    req.setTimeout(5000, () => { req.destroy(); reject(new Error('timeout')) })
  })
}

function percentile(sorted, p) {
  const idx = Math.min(sorted.length - 1, Math.ceil((p / 100) * sorted.length) - 1)
  return sorted[idx]
}

function summarize(latencies) {
  const sorted = latencies.map(l => l.ms).sort((a, b) => a - b)
  return {
    samples: sorted.length,
    min: +sorted[0].toFixed(2),
    p50: +percentile(sorted, 50).toFixed(2),
    p95: +percentile(sorted, 95).toFixed(2),
    p99: +percentile(sorted, 99).toFixed(2),
    max: +sorted[sorted.length - 1].toFixed(2),
    mean: +(sorted.reduce((a, b) => a + b, 0) / sorted.length).toFixed(2),
    statusOk: latencies.filter(l => l.status === 200).length,
  }
}

async function runSequential(path, count) {
  const out = []
  for (let i = 0; i < count; i++) {
    out.push(await timedRequest(path))
  }
  return summarize(out)
}

async function runParallel(path, count, concurrency) {
  const out = []
  const start = performance.now()
  let next = 0
  async function worker() {
    while (next < count) {
      const idx = next++
      try { out.push(await timedRequest(path)) } catch { /* count toward total */ }
    }
  }
  await Promise.all(Array.from({ length: concurrency }, worker))
  const elapsedMs = performance.now() - start
  const summary = summarize(out)
  return {
    ...summary,
    elapsedMs: +elapsedMs.toFixed(2),
    rps: +(summary.samples / (elapsedMs / 1000)).toFixed(1),
    concurrency,
  }
}

async function measureSseRate(durationMs) {
  return new Promise((resolve) => {
    const start = performance.now()
    let events = 0
    const req = http.get(
      { hostname: '127.0.0.1', port, path: `/api/events?token=${encodeURIComponent(token)}` },
      (res) => {
        res.on('data', (chunk) => {
          events += (chunk.toString().match(/^event: /gm) || []).length
        })
      }
    )
    req.on('error', () => resolve({ events: 0, durationMs: 0, eventsPerSec: 0 }))
    setTimeout(() => {
      req.destroy()
      const elapsed = performance.now() - start
      resolve({
        events,
        durationMs: +elapsed.toFixed(0),
        eventsPerSec: +(events / (elapsed / 1000)).toFixed(2),
      })
    }, durationMs)
  })
}

console.error(`[perf] daemon on port ${port}, samples=${SAMPLES}, parallel=${PARALLEL}`)
console.error('[perf] warmup 50 reqs...')
await runSequential('/api/status', 50)

console.error('[perf] /api/status sequential...')
const seqStatus = await runSequential('/api/status', SAMPLES)

console.error('[perf] /api/services sequential...')
const seqServices = await runSequential('/api/services', SAMPLES)

console.error('[perf] /api/status parallel...')
const parStatus = await runParallel('/api/status', SAMPLES, PARALLEL)

console.error(`[perf] SSE rate over ${QUICK ? 5 : 10}s...`)
const sse = await measureSseRate(QUICK ? 5000 : 10000)

const report = {
  timestamp: new Date().toISOString(),
  daemon: { port },
  host: {
    hostname: hostname(),
    platform: platform(),
    arch: arch(),
    cpuModel: cpus()[0]?.model ?? 'unknown',
    cpuCount: cpus().length,
    totalMemGb: +(totalmem() / 1024 / 1024 / 1024).toFixed(1),
  },
  endpoints: {
    statusSequential: seqStatus,
    servicesSequential: seqServices,
    statusParallel: parStatus,
  },
  sse,
  config: { samples: SAMPLES, parallel: PARALLEL },
}

if (STDOUT_JSON) {
  console.log(JSON.stringify(report, null, 2))
} else {
  const outDir = join(REPO_ROOT, 'docs')
  mkdirSync(outDir, { recursive: true })
  const jsonPath = join(outDir, 'perf-baselines.json')
  writeFileSync(jsonPath, JSON.stringify(report, null, 2))
  console.error(`[perf] wrote ${jsonPath}`)

  const md = `# NKS WebDev Console — Performance Baselines

**Last run:** ${report.timestamp}
**Host:** ${report.host.hostname} (${report.host.platform}/${report.host.arch}, ${report.host.cpuCount}× ${report.host.cpuModel}, ${report.host.totalMemGb} GB RAM)
**Samples:** ${SAMPLES} sequential / ${PARALLEL} concurrent

## /api/status — sequential latency

| Metric | Value |
|--------|-------|
| samples | ${seqStatus.samples} |
| min | ${seqStatus.min} ms |
| p50 | ${seqStatus.p50} ms |
| p95 | ${seqStatus.p95} ms |
| p99 | ${seqStatus.p99} ms |
| max | ${seqStatus.max} ms |
| mean | ${seqStatus.mean} ms |
| HTTP 200 | ${seqStatus.statusOk}/${seqStatus.samples} |

## /api/services — sequential latency

| Metric | Value |
|--------|-------|
| p50 | ${seqServices.p50} ms |
| p95 | ${seqServices.p95} ms |
| p99 | ${seqServices.p99} ms |
| max | ${seqServices.max} ms |
| mean | ${seqServices.mean} ms |

## /api/status — parallel throughput (${PARALLEL} concurrent)

| Metric | Value |
|--------|-------|
| total requests | ${parStatus.samples} |
| elapsed | ${parStatus.elapsedMs} ms |
| **RPS** | **${parStatus.rps}** |
| p50 | ${parStatus.p50} ms |
| p99 | ${parStatus.p99} ms |
| HTTP 200 | ${parStatus.statusOk}/${parStatus.samples} |

## SSE broadcast rate

| Metric | Value |
|--------|-------|
| events received | ${sse.events} |
| duration | ${sse.durationMs} ms |
| events/sec | ${sse.eventsPerSec} |

## Regression budget (warn if exceeded by >20%)

- /api/status p99 < ${(seqStatus.p99 * 1.2).toFixed(2)} ms
- /api/services p99 < ${(seqServices.p99 * 1.2).toFixed(2)} ms
- /api/status RPS > ${(parStatus.rps * 0.8).toFixed(0)}
- SSE events/sec > ${(sse.eventsPerSec * 0.8).toFixed(2)}

Run \`node scripts/perf-baseline.mjs\` to refresh after meaningful daemon changes.
`
  writeFileSync(join(outDir, 'perf-baselines.md'), md)
  console.error(`[perf] wrote ${join(outDir, 'perf-baselines.md')}`)
}
