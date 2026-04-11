#!/usr/bin/env node
/**
 * NKS WebDev Console — E2E integration test runner.
 *
 * Executes the scenarios from docs/e2e-scenarios.md against a running daemon.
 * Pure Node built-ins only (no external deps), matching scripts/perf-baseline.mjs.
 *
 * Usage:
 *   node scripts/e2e-runner.mjs              # run P0 smoke (CI default)
 *   node scripts/e2e-runner.mjs --priority P1   # P0 + P1
 *   node scripts/e2e-runner.mjs --priority P2   # P0 + P1 + P2 (full)
 *   node scripts/e2e-runner.mjs --only 3,8      # run specific scenarios
 *   node scripts/e2e-runner.mjs --json          # machine-readable output
 *
 * Prereq: daemon running, port file at %TEMP%/nks-wdc-daemon.port.
 *
 * Exit codes:
 *   0 — all selected scenarios passed (skips allowed)
 *   1 — one or more scenarios failed
 *   2 — harness error (daemon unreachable, bad args, etc.)
 */
import { readdirSync } from 'node:fs'
import { join, dirname } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'
import { performance } from 'node:perf_hooks'
import { SkipError, getConnection } from './e2e/harness.mjs'

const __dirname = dirname(fileURLToPath(import.meta.url))
const SCENARIOS_DIR = join(__dirname, 'e2e', 'scenarios')

const args = process.argv.slice(2)
function argValue(flag) {
  const i = args.indexOf(flag)
  return i >= 0 ? args[i + 1] : null
}
const PRIORITY = argValue('--priority') ?? 'P0'
const ONLY = argValue('--only')?.split(',').map((s) => s.trim()).filter(Boolean) ?? null
const JSON_OUT = args.includes('--json')

const PRIORITY_ORDER = ['P0', 'P1', 'P2']
const INCLUDED = new Set(PRIORITY_ORDER.slice(0, PRIORITY_ORDER.indexOf(PRIORITY) + 1))
if (INCLUDED.size === 0) {
  console.error(`Unknown --priority "${PRIORITY}" — expected P0, P1, or P2`)
  process.exit(2)
}

// ---------- Scenario discovery ----------
async function discoverScenarios() {
  const files = readdirSync(SCENARIOS_DIR).filter((f) => f.endsWith('.mjs')).sort()
  const loaded = []
  for (const f of files) {
    const mod = await import(pathToFileURL(join(SCENARIOS_DIR, f)).href)
    if (!mod.default) {
      console.warn(`[e2e] ${f}: no default export — skipping`)
      continue
    }
    loaded.push({ ...mod.default, file: f })
  }
  return loaded
}

function filter(scenarios) {
  return scenarios.filter((s) => {
    if (ONLY) return ONLY.includes(s.id)
    return INCLUDED.has(s.priority)
  })
}

// ---------- Runner ----------
async function runOne(s) {
  const cleanups = []
  const ctx = {
    cleanup: (fn) => cleanups.push(fn),
    skip: (reason) => { throw new SkipError(reason) },
  }
  const t0 = performance.now()
  let status = 'pass'
  let error = null
  try {
    await s.run(ctx)
  } catch (e) {
    if (e instanceof SkipError) {
      status = 'skip'
      error = e.message
    } else {
      status = 'fail'
      error = e.stack ?? String(e)
    }
  }
  // Cleanup runs in reverse order; failures here downgrade to warnings.
  for (const fn of cleanups.reverse()) {
    try { await fn() } catch (e) { console.warn(`[e2e] cleanup warning (${s.id}): ${e.message}`) }
  }
  return { id: s.id, name: s.name, priority: s.priority, status, error, ms: performance.now() - t0 }
}

// ---------- Main ----------
async function main() {
  // Probe daemon connection up-front.
  try {
    getConnection()
  } catch (e) {
    console.error(`[e2e] Cannot read daemon port file: ${e.message}`)
    console.error(`[e2e] Start the daemon first: cd src/daemon/NKS.WebDevConsole.Daemon && dotnet run`)
    process.exit(2)
  }

  const all = await discoverScenarios()
  const selected = filter(all)
  if (selected.length === 0) {
    console.error(`[e2e] No scenarios matched priority=${PRIORITY} only=${ONLY?.join(',') ?? '-'}`)
    process.exit(2)
  }

  if (!JSON_OUT) {
    console.log(`\nNKS WDC — E2E runner`)
    console.log(`  priority: ${PRIORITY}  (included: ${[...INCLUDED].join(', ')})`)
    console.log(`  selected: ${selected.length} scenario(s)`)
    console.log(`  daemon:   http://127.0.0.1:${getConnection().port}`)
    console.log('')
  }

  const results = []
  for (const s of selected) {
    if (!JSON_OUT) process.stdout.write(`  [${s.priority}] #${s.id.padEnd(2)} ${s.name.padEnd(48)} `)
    const r = await runOne(s)
    results.push(r)
    if (!JSON_OUT) {
      const badge =
        r.status === 'pass' ? 'PASS' :
        r.status === 'skip' ? 'SKIP' : 'FAIL'
      console.log(`${badge}  ${r.ms.toFixed(0)}ms${r.status !== 'pass' && r.error ? `\n        ${r.error.split('\n')[0]}` : ''}`)
    }
  }

  const passed = results.filter((r) => r.status === 'pass').length
  const skipped = results.filter((r) => r.status === 'skip').length
  const failed = results.filter((r) => r.status === 'fail').length

  if (JSON_OUT) {
    console.log(JSON.stringify({ priority: PRIORITY, passed, skipped, failed, results }, null, 2))
  } else {
    console.log('')
    console.log(`  ${passed} passed, ${skipped} skipped, ${failed} failed`)
    if (failed > 0) {
      console.log('\n  Failures:')
      for (const r of results.filter((x) => x.status === 'fail')) {
        console.log(`    #${r.id} ${r.name}`)
        console.log(`    ${r.error.split('\n').map((l) => '    ' + l).join('\n')}`)
      }
    }
  }

  process.exit(failed > 0 ? 1 : 0)
}

main().catch((e) => {
  console.error(`[e2e] fatal: ${e.stack ?? e.message}`)
  process.exit(2)
})
