#!/usr/bin/env node
/*
 * Stage plugin DLLs into the packaged daemon layout.
 *
 * Copies plugin build artifacts into
 * <repo>/src/frontend/resources/daemon/plugins so the daemon (running
 * from the packaged app at runtime) finds them next to its own exe.
 *
 * F99 preparation: src/plugins/ will eventually be removed from this
 * monorepo in favour of nks-hub/webdev-console-plugins as the source
 * of truth. This script now searches multiple locations in priority
 * order so it keeps working during the migration:
 *
 *   1. <repo>/build/plugins                          — current layout
 *      (monorepo dotnet build output, written by each plugin's
 *      Directory.Build.props OutputPath)
 *   2. <repo>/../webdev-console-plugins/NKS.WebDevConsole.Plugin.*\
 *        /bin/Release/net9.0                         — sibling checkout
 *      of the extracted plugins repo; active when the dev has cloned
 *      nks-hub/webdev-console-plugins alongside nks-ws and run
 *      `dotnet build -c Release` in there.
 *   3. empty (no plugins staged) — daemon F95 runtime fallback will
 *      download from catalog-api into ~/.wdc/plugins on first run.
 *
 * Runs AFTER `dotnet publish` in the `stage:daemon:<os>` npm scripts.
 * Safe to re-run: target dir is wiped first so removed plugins don't
 * linger.
 */
import { existsSync, mkdirSync, readdirSync, copyFileSync, rmSync, statSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const __dirname = dirname(fileURLToPath(import.meta.url))
const repoRoot = resolve(__dirname, '..')

const destDir = join(repoRoot, 'src', 'frontend', 'resources', 'daemon', 'plugins')

if (existsSync(destDir)) {
  // Wipe so a removed plugin doesn't stay in the packaged bundle.
  rmSync(destDir, { recursive: true, force: true })
}
mkdirSync(destDir, { recursive: true })

function copyRecursive(from, to) {
  mkdirSync(to, { recursive: true })
  for (const name of readdirSync(from)) {
    const src = join(from, name)
    const dst = join(to, name)
    const s = statSync(src)
    if (s.isDirectory()) {
      copyRecursive(src, dst)
    } else {
      copyFileSync(src, dst)
    }
  }
}

/** Collect plugin DLL file-names that actually appear in a candidate dir. */
function listPluginDlls(dir) {
  if (!existsSync(dir)) return []
  try {
    return readdirSync(dir).filter(n =>
      n.startsWith('NKS.WebDevConsole.Plugin.') && n.endsWith('.dll'))
  } catch {
    return []
  }
}

let sourceUsed = null

// Source 1: monorepo dotnet build output (current default).
const monorepoBuildDir = join(repoRoot, 'build', 'plugins')
if (listPluginDlls(monorepoBuildDir).length > 0) {
  copyRecursive(monorepoBuildDir, destDir)
  sourceUsed = monorepoBuildDir
}

// Source 2: sibling checkout of webdev-console-plugins repo. Walk each
// plugin project's Release output and copy the whole folder — matches
// what `dotnet build` on the plugins sln would have emitted for each.
if (!sourceUsed) {
  const siblingRoot = resolve(repoRoot, '..', 'webdev-console-plugins')
  if (existsSync(siblingRoot)) {
    const pluginProjectDirs = readdirSync(siblingRoot)
      .filter(n => n.startsWith('NKS.WebDevConsole.Plugin.'))
      .map(n => join(siblingRoot, n, 'bin', 'Release', 'net9.0'))
      .filter(p => listPluginDlls(p).length > 0)
    if (pluginProjectDirs.length > 0) {
      for (const p of pluginProjectDirs) copyRecursive(p, destDir)
      sourceUsed = siblingRoot
    }
  }
}

if (!sourceUsed) {
  console.warn('[stage-plugins] No plugin build output found — packaged daemon will ship with zero bundled plugins.')
  console.warn('[stage-plugins] Runtime fallback: daemon will download from catalog-api into ~/.wdc/plugins on first run.')
  console.warn('[stage-plugins] Tried: ' + monorepoBuildDir)
  console.warn('[stage-plugins] Tried: ' + resolve(repoRoot, '..', 'webdev-console-plugins'))
  process.exit(0)
}

const count = listPluginDlls(destDir).length
console.log(`[stage-plugins] Copied ${count} plugin DLL(s) from ${sourceUsed} → ${destDir}`)
