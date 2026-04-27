#!/usr/bin/env node
/*
 * Build nksdeploy.phar from source and emit it next to the WDC plugin
 * staging area, so the packaged daemon ships a working phar artifact
 * the NKS.WebDevConsole.Plugin.NksDeploy can shell out to at runtime.
 *
 * Resolution order for the source tree:
 *
 *   1. NKSDEPLOY_SRC env var — explicit override (CI: clone there).
 *   2. <repo>/../nksdeploy — sibling checkout next to nks-ws.
 *   3. <repo>/../gh-nks/nksdeploy — gh-nks/ subdir layout dev convention.
 *   4. Clone https://github.com/nks-hub/nksdeploy into a tmp dir.
 *
 * Build pipeline:
 *
 *   - php composer.phar install --no-dev (+ dump autoload)
 *   - box compile (Box 4.6+) with --composer-bin pointing at composer.phar
 *
 * Output: places nksdeploy.phar at:
 *   - argv[1] OR
 *   - <repo>/src/frontend/resources/daemon/plugins/nksdeploy.phar
 *
 * Skip via WDC_SKIP_PHAR_BUILD=1 when phar is already provided by another
 * step (e.g. an artifact downloaded from a prior CI run). The plugin's
 * ResolveNksDeployPhar() falls back to PATH lookup at runtime so missing
 * phar fails late, not at staging time.
 */

import { existsSync, mkdirSync, copyFileSync, rmSync, readFileSync, writeFileSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { execSync, spawnSync } from 'node:child_process'
import { tmpdir } from 'node:os'
import { createHash } from 'node:crypto'

// Pinned SHA-384 of composer 2.8.0 (verified upstream 2026-04-27).
// Box's compile step shells `composer --version`; an attacker-controlled
// composer phar at build time would let them inject arbitrary code into
// our nksdeploy.phar. Verifying upstream pin is a supply-chain guard.
//
// Rotating the pin (e.g. bump to 2.8.1):
//   1. Read upstream pubkeys.tags from https://composer.github.io/pubkeys.html
//      and verify the new release with gpg, OR cross-check the hash from
//      https://getcomposer.org/download/<ver>/composer.phar.sha384sum.
//   2. curl -sSL https://getcomposer.org/download/<ver>/composer.phar | openssl dgst -sha384
//   3. Update both COMPOSER_VERSION + COMPOSER_SHA384 below in the SAME commit.
//   4. Delete build/composer.phar locally so the next run re-downloads.
const COMPOSER_VERSION = '2.8.0'
const COMPOSER_SHA384 = '8f926f44a8a56be162768b91b5e4c5c6fe9fced6e384ff1dc65b1f92a1da629543a614a90c09d9acdc5cf8b393b5951c'

const __dirname = dirname(fileURLToPath(import.meta.url))
const repoRoot = resolve(__dirname, '..')
const defaultDest = join(repoRoot, 'src', 'frontend', 'resources', 'daemon', 'plugins', 'nksdeploy.phar')
const dest = process.argv[2] ? resolve(process.argv[2]) : defaultDest

if (process.env.WDC_SKIP_PHAR_BUILD === '1') {
  console.log('[build-phar] WDC_SKIP_PHAR_BUILD=1 — skipping')
  process.exit(0)
}

function log(msg) { console.log(`[build-phar] ${msg}`) }
function die(msg) { console.error(`[build-phar] ERROR: ${msg}`); process.exit(1) }

function which(cmd) {
  try {
    const r = spawnSync(process.platform === 'win32' ? 'where' : 'which', [cmd], { encoding: 'utf8' })
    if (r.status === 0) return r.stdout.split(/\r?\n/)[0].trim()
  } catch {}
  return null
}

function resolveSourceTree() {
  if (process.env.NKSDEPLOY_SRC && existsSync(process.env.NKSDEPLOY_SRC)) {
    log(`source from $NKSDEPLOY_SRC: ${process.env.NKSDEPLOY_SRC}`)
    return process.env.NKSDEPLOY_SRC
  }
  const sibling = resolve(repoRoot, '..', 'nksdeploy')
  if (existsSync(join(sibling, 'box.json'))) {
    log(`source from sibling checkout: ${sibling}`)
    return sibling
  }
  const ghNks = resolve(repoRoot, '..', '..', 'gh-nks', 'nksdeploy')
  if (existsSync(join(ghNks, 'box.json'))) {
    log(`source from gh-nks layout: ${ghNks}`)
    return ghNks
  }
  // Last resort: clone fresh into tmpdir.
  const tmp = join(tmpdir(), `nksdeploy-${Date.now()}`)
  log(`cloning https://github.com/nks-hub/nksdeploy → ${tmp}`)
  execSync(`git clone --depth 1 https://github.com/nks-hub/nksdeploy ${tmp}`, { stdio: 'inherit' })
  return tmp
}

function findPhp() {
  // Prefer PHP 8.4+ since vendor's box requires it. MAMP layout under
  // Windows is the dev fallback; CI must have php on PATH.
  const explicit = process.env.PHP_BIN
  if (explicit && existsSync(explicit)) return explicit
  const onPath = which('php')
  if (onPath) return onPath
  if (process.platform === 'win32') {
    const mamp = 'C:/MAMP/bin/php/php8.4.12/php.exe'
    if (existsSync(mamp)) return mamp
  }
  die('php not found — set PHP_BIN or install php on PATH (>=8.4 required)')
}

function findComposer(php) {
  // Box wants a working composer binary that prints a parseable
  // "Composer version X.Y" first line. PHP 8.4 emits deprecation
  // notices that older composers (<2.8) leak ahead of the version
  // string, breaking Box's regex. We always download a fresh 2.8.0
  // into the build dir to avoid this.
  const buildDir = join(repoRoot, 'build')
  mkdirSync(buildDir, { recursive: true })
  const composerPhar = join(buildDir, 'composer.phar')
  if (!existsSync(composerPhar)) {
    log(`downloading composer ${COMPOSER_VERSION} into build/composer.phar`)
    execSync(`curl -sSL https://getcomposer.org/download/${COMPOSER_VERSION}/composer.phar -o "${composerPhar}"`, { stdio: 'inherit' })
  }
  // Verify SHA-384 against pinned upstream hash. If composer.phar on
  // disk doesn't match (corrupt download, MITM, malicious replacement),
  // refuse to use it — Box would otherwise execute its bytecode while
  // building our phar, allowing arbitrary code injection.
  const composerBytes = readFileSync(composerPhar)
  const actualHash = createHash('sha384').update(composerBytes).digest('hex')
  if (actualHash !== COMPOSER_SHA384) {
    die(`composer.phar checksum mismatch — expected ${COMPOSER_SHA384}, got ${actualHash}. ` +
        `Refusing to use a tampered composer binary for phar build. ` +
        `Delete ${composerPhar} and re-run to re-download.`)
  }
  log(`composer.phar SHA-384 verified (pin: ${COMPOSER_SHA384.slice(0, 16)}…)`)
  // Wrapper script that runs composer via PHP with deprecations silenced
  // — Box will exec this when checking version.
  const isWin = process.platform === 'win32'
  const wrapper = join(buildDir, isWin ? 'composer-quiet.bat' : 'composer-quiet.sh')
  if (isWin) {
    writeFileSync(wrapper, `@echo off\r\n"${php}" -d error_reporting=0 "${composerPhar}" %*\r\n`)
  } else {
    writeFileSync(wrapper, `#!/bin/sh\nexec "${php}" -d error_reporting=0 "${composerPhar}" "$@"\n`)
    execSync(`chmod +x "${wrapper}"`)
  }
  return wrapper
}

const src = resolveSourceTree()
const php = findPhp()
log(`php: ${php}`)
log(`source: ${src}`)

if (!existsSync(join(src, 'box.json'))) die(`box.json not found in ${src}`)

const composerBin = findComposer(php)

// Ensure vendor is installed (CI needs this; sibling dev checkout often has it).
if (!existsSync(join(src, 'vendor', 'humbug', 'box', 'bin', 'box'))) {
  log('vendor missing humbug/box → composer install --no-dev=false')
  // Need dev dependencies because Box itself ships under require-dev.
  execSync(`"${php}" -d error_reporting=0 "${join(repoRoot, 'build', 'composer.phar')}" install`, {
    cwd: src, stdio: 'inherit', env: { ...process.env, COMPOSER_NO_INTERACTION: '1' },
  })
}

const boxBin = join(src, 'vendor', 'humbug', 'box', 'bin', 'box')
log(`running box compile (this takes ~20-60s)`)
execSync(`"${php}" -d error_reporting=0 "${boxBin}" compile --composer-bin="${composerBin}"`, {
  cwd: src, stdio: 'inherit',
})

const builtPhar = join(src, 'build', 'nksdeploy.phar')
if (!existsSync(builtPhar)) die(`expected build artifact not found at ${builtPhar}`)

mkdirSync(dirname(dest), { recursive: true })
copyFileSync(builtPhar, dest)
log(`✓ staged ${dest}`)
log(`  size: ${(readFileSync(dest).length / 1024 / 1024).toFixed(2)} MB`)
