#!/usr/bin/env node
/*
 * Stage plugin DLLs into the packaged daemon layout.
 *
 * Copies everything from <repo>/build/plugins into
 * <repo>/src/frontend/resources/daemon/plugins so the daemon (running
 * from the packaged app at runtime) finds them next to its own exe.
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

const srcDir = join(repoRoot, 'build', 'plugins')
const destDir = join(repoRoot, 'src', 'frontend', 'resources', 'daemon', 'plugins')

if (!existsSync(srcDir)) {
  console.error(`[stage-plugins] Source directory not found: ${srcDir}`)
  console.error('[stage-plugins] Run `dotnet build WebDevConsole.sln -c Release` first.')
  process.exit(1)
}

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

copyRecursive(srcDir, destDir)

const count = readdirSync(destDir).filter((n) => n.startsWith('NKS.WebDevConsole.Plugin.') && n.endsWith('.dll')).length
console.log(`[stage-plugins] Copied ${count} plugin DLL(s) + support files → ${destDir}`)
