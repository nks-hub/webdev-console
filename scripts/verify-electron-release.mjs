#!/usr/bin/env node
import { existsSync, readdirSync, statSync } from 'node:fs'
import { join, resolve } from 'node:path'

function parseArgs(argv) {
  const options = { dir: null, target: null }
  for (let i = 0; i < argv.length; i += 1) {
    const arg = argv[i]
    if (arg === '--dir') options.dir = argv[++i]
    else if (arg === '--target') options.target = argv[++i]
  }

  if (!options.dir || !options.target) {
    throw new Error('Usage: node scripts/verify-electron-release.mjs --dir <release-dir> --target <win|mac|linux>')
  }

  return options
}

function walkFiles(root) {
  const files = []

  function visit(dir) {
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      const fullPath = join(dir, entry.name)
      if (entry.isDirectory()) {
        visit(fullPath)
      } else if (entry.isFile()) {
        files.push(fullPath)
      }
    }
  }

  visit(root)
  return files
}

function assert(condition, message) {
  if (!condition) throw new Error(message)
}

function includesFile(files, predicate) {
  return files.some((file) => predicate(file))
}

function main() {
  const { dir, target } = parseArgs(process.argv.slice(2))
  const releaseDir = resolve(dir)

  assert(existsSync(releaseDir), `Release directory does not exist: ${releaseDir}`)
  const files = walkFiles(releaseDir)
  assert(files.length > 0, `Release directory is empty: ${releaseDir}`)

  if (target === 'win') {
    assert(includesFile(files, (file) => file.endsWith('-setup-x64.exe')),
      'Missing Windows NSIS setup .exe artifact')
    assert(includesFile(files, (file) => file.endsWith('-portable-x64.exe')),
      'Missing Windows portable .exe artifact')
    assert(includesFile(files, (file) => file.endsWith('.blockmap')),
      'Missing Windows .blockmap artifact for updater metadata')
    assert(includesFile(files, (file) => file.endsWith('latest.yml')),
      'Missing latest.yml updater metadata')
    assert(includesFile(files, (file) => file.endsWith(join('win-unpacked', 'NKS WebDev Console.exe'))),
      'Missing unpacked Electron app executable')
    assert(includesFile(files, (file) => file.endsWith(join('win-unpacked', 'resources', 'daemon', 'NKS.WebDevConsole.Daemon.exe'))),
      'Missing bundled daemon executable inside win-unpacked/resources/daemon')
  } else if (target === 'mac') {
    assert(includesFile(files, (file) => file.endsWith('.dmg')),
      'Missing macOS .dmg artifact')
    assert(includesFile(files, (file) => file.endsWith('.zip')),
      'Missing macOS .zip artifact required by electron-updater')
  } else if (target === 'linux') {
    assert(includesFile(files, (file) => file.endsWith('.AppImage')),
      'Missing Linux .AppImage artifact')
  } else {
    throw new Error(`Unknown target '${target}'`)
  }

  const totalBytes = files.reduce((sum, file) => sum + statSync(file).size, 0)
  console.log(`[verify-electron-release] OK target=${target} files=${files.length} bytes=${totalBytes}`)
}

main()
