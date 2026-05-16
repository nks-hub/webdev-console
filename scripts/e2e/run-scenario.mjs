/**
 * Ad-hoc runner: `node scripts/e2e/run-scenario.mjs <scenario-file>`.
 * Imports the default export and invokes its `.run(ctx)` with a tiny
 * shim that collects cleanup callbacks and runs them on success or
 * failure. Mirrors what the canonical suite runner would do — kept
 * minimal so a developer can sanity-check a single scenario without
 * pulling the full harness around it.
 */
import { pathToFileURL } from 'node:url'
import { resolve } from 'node:path'

const arg = process.argv[2]
if (!arg) {
  console.error('usage: node scripts/e2e/run-scenario.mjs <scenario-file>')
  process.exit(2)
}

const url = pathToFileURL(resolve(arg)).href
const mod = await import(url)
const scenario = mod.default
if (!scenario || typeof scenario.run !== 'function') {
  console.error(`Scenario module ${arg} does not export a runnable default`)
  process.exit(2)
}

console.log(`[run] scenario ${scenario.id} — ${scenario.name} (${scenario.priority})`)

const cleanups = []
const ctx = {
  cleanup(fn) { cleanups.push(fn) },
  skip(reason) {
    console.log(`[skip] ${reason}`)
    process.exit(0)
  },
}

let exitCode = 0
const start = Date.now()
try {
  await scenario.run(ctx)
  console.log(`[pass] ${scenario.name} in ${Date.now() - start}ms`)
} catch (err) {
  exitCode = 1
  console.error(`[fail] ${scenario.name}: ${err?.message ?? err}`)
  if (err?.stack) console.error(err.stack)
} finally {
  for (const fn of cleanups.reverse()) {
    try { await fn() } catch (e) { console.warn(`[cleanup] ${e?.message ?? e}`) }
  }
}
process.exit(exitCode)
