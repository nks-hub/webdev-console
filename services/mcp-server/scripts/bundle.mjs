#!/usr/bin/env node
// Bundle the MCP server into one self-contained ESM file for the WDC
// installer: <outdir>/index.js plus a tiny package.json marking it as ESM.
//
// A single file (instead of dist/ + production node_modules) keeps the
// installer small, avoids electron-builder's node_modules handling for a
// second package tree, and runs with any Node >= 18 the user already has:
//   node "<resources>/mcp-server/index.js"
//
// Usage: node scripts/bundle.mjs [--outdir <dir>]   (default: dist-bundle)

import { build } from 'esbuild'
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const args = process.argv.slice(2)
const outIdx = args.indexOf('--outdir')
const outdir = resolve(process.cwd(), outIdx >= 0 && args[outIdx + 1] ? args[outIdx + 1] : join(root, 'dist-bundle'))
const pkg = JSON.parse(readFileSync(join(root, 'package.json'), 'utf8'))

mkdirSync(outdir, { recursive: true })
await build({
  entryPoints: [join(root, 'src', 'index.ts')],
  outfile: join(outdir, 'index.js'),
  bundle: true,
  platform: 'node',
  format: 'esm',
  // index.ts uses top-level await, so the output has to stay ESM.
  target: 'node18',
  // Some bundled dependencies are CommonJS and call require() for Node
  // built-ins; give the ESM bundle a real require so those calls work.
  banner: {
    js: "import { createRequire as __wdcCreateRequire } from 'node:module';\nconst require = __wdcCreateRequire(import.meta.url);",
  },
  legalComments: 'eof',
  logLevel: 'warning',
})
writeFileSync(
  join(outdir, 'package.json'),
  JSON.stringify({ name: 'nks-wdc-mcp-server', version: pkg.version, private: true, type: 'module', main: 'index.js' }, null, 2) + '\n',
)
process.stderr.write(`[bundle] ${join(outdir, 'index.js')}\n`)
