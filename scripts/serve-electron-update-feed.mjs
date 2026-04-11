import http from 'node:http'
import { createReadStream, existsSync, statSync } from 'node:fs'
import { extname, join, normalize, resolve } from 'node:path'

function parseArgs(argv) {
  const args = { dir: '', host: '127.0.0.1', port: 8787 }
  for (let i = 2; i < argv.length; i++) {
    const arg = argv[i]
    if (arg === '--dir') args.dir = argv[++i] ?? ''
    else if (arg === '--host') args.host = argv[++i] ?? args.host
    else if (arg === '--port') args.port = Number.parseInt(argv[++i] ?? '', 10) || args.port
  }
  if (!args.dir) {
    console.error('Usage: node scripts/serve-electron-update-feed.mjs --dir <release-dir> [--host 127.0.0.1] [--port 8787]')
    process.exit(1)
  }
  return args
}

function contentTypeFor(filePath) {
  return {
    '.yml': 'text/yaml; charset=utf-8',
    '.yaml': 'text/yaml; charset=utf-8',
    '.json': 'application/json; charset=utf-8',
    '.exe': 'application/vnd.microsoft.portable-executable',
    '.blockmap': 'application/octet-stream',
  }[extname(filePath).toLowerCase()] ?? 'application/octet-stream'
}

const args = parseArgs(process.argv)
const rootDir = resolve(args.dir)

if (!existsSync(rootDir)) {
  console.error(`Feed directory not found: ${rootDir}`)
  process.exit(1)
}

const server = http.createServer((req, res) => {
  const reqPath = req.url?.split('?')[0] ?? '/'
  const relativePath = reqPath === '/' ? '/latest.yml' : reqPath
  const filePath = normalize(join(rootDir, relativePath))

  if (!filePath.startsWith(rootDir)) {
    res.writeHead(403)
    res.end('forbidden')
    return
  }

  if (!existsSync(filePath) || !statSync(filePath).isFile()) {
    res.writeHead(404)
    res.end('not found')
    return
  }

  res.writeHead(200, { 'Content-Type': contentTypeFor(filePath) })
  createReadStream(filePath).pipe(res)
})

server.listen(args.port, args.host, () => {
  console.log(`Electron update feed serving ${rootDir}`)
  console.log(`URL: http://${args.host}:${args.port}/latest.yml`)
  console.log('Set NKS_WDC_UPDATE_FEED_URL to the base URL when launching the packaged app.')
})
