#!/usr/bin/env node
import { existsSync, readFileSync, renameSync, rmSync, writeFileSync } from 'node:fs'
import { spawn, spawnSync } from 'node:child_process'
import { join, resolve } from 'node:path'
import { tmpdir } from 'node:os'
import http from 'node:http'
import { fileURLToPath } from 'node:url'

const scriptDir = fileURLToPath(new URL('.', import.meta.url))
const repoRoot = resolve(scriptDir, '..')
const portFile = join(tmpdir(), 'nks-wdc-daemon.port')
const backupPortFile = `${portFile}.bak-packaged-smoke`

function parseArgs(argv) {
  const options = {
    exe: join(repoRoot, 'src', 'frontend', 'release', 'win-unpacked', 'NKS WebDev Console.exe'),
    replaceDevDaemon: false,
  }

  for (let i = 0; i < argv.length; i += 1) {
    if (argv[i] === '--exe') options.exe = argv[++i]
    else if (argv[i] === '--replace-dev-daemon') options.replaceDevDaemon = true
  }

  return { exe: resolve(options.exe), replaceDevDaemon: options.replaceDevDaemon }
}

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms))
}

function readPortInfo() {
  // Intentionally DO NOT pre-check existsSync — the daemon writes the port
  // file atomically via tmp+rename (F67) which briefly deletes the target
  // before creating it, so existsSync can race against File.Move and
  // readFileSync ends up with ENOENT one tick later. Treat every transient
  // filesystem error during boot the same way: "not ready yet, retry".
  // Real unrecoverable errors still surface because the outer waitFor
  // loop has its own timeoutMs budget.
  let content
  try {
    content = readFileSync(portFile, 'utf-8')
  } catch (err) {
    const code = err && err.code
    if (code === 'ENOENT' || code === 'EPERM' || code === 'EBUSY' || code === 'EACCES') {
      return null
    }
    throw err
  }
  const lines = content.split('\n').filter(Boolean)
  if (lines.length < 2) return null
  return { port: Number(lines[0]), token: lines[1].trim() }
}

async function probeStatus(info) {
  if (!info?.port || !info?.token) return false

  return await new Promise((resolve) => {
    let settled = false
    const finish = (value) => {
      if (settled) return
      settled = true
      resolve(value)
    }

    const req = http.get({
      hostname: '127.0.0.1',
      port: info.port,
      path: '/api/status',
      timeout: 1500,
      headers: { Authorization: `Bearer ${info.token}` },
    }, (res) => {
      res.resume()
      finish(res.statusCode === 200)
    })

    req.on('error', () => finish(false))
    req.on('timeout', () => {
      req.destroy(new Error('timeout'))
      finish(false)
    })
  })
}

async function probeDaemon() {
  const info = readPortInfo()
  return await probeStatus(info)
}

async function waitFor(predicate, { timeoutMs, intervalMs, label }) {
  const deadline = Date.now() + timeoutMs
  while (Date.now() < deadline) {
    const result = await predicate()
    if (result) return result
    await sleep(intervalMs)
  }

  throw new Error(`Timed out waiting for ${label} after ${timeoutMs}ms`)
}

async function requestShutdown() {
  const info = readPortInfo()
  if (!info) throw new Error('Port file missing before shutdown request')

  const payload = await new Promise((resolve, reject) => {
    const req = http.request({
      hostname: '127.0.0.1',
      port: info.port,
      path: '/api/admin/shutdown',
      method: 'POST',
      headers: { Authorization: `Bearer ${info.token}` },
    }, (res) => {
      const chunks = []
      res.on('data', (chunk) => chunks.push(chunk))
      res.on('end', () => resolve({ status: res.statusCode ?? 0, body: Buffer.concat(chunks).toString('utf-8') }))
    })

    req.on('error', reject)
    req.end()
  })

  if (payload.status !== 202) {
    throw new Error(`Shutdown endpoint returned HTTP ${payload.status}: ${payload.body}`)
  }
}

function startPackagedApp(exePath) {
  if (!existsSync(exePath)) {
    throw new Error(`Packaged Electron executable not found: ${exePath}`)
  }

  const start = spawnSync('powershell', ['-NoProfile', '-Command', [
    `$proc = Start-Process -FilePath '${exePath.replace(/'/g, "''")}' -PassThru`,
    'Write-Output $proc.Id',
  ].join('; ')], {
    cwd: repoRoot,
    encoding: 'utf-8',
  })

  if (start.status !== 0) {
    throw new Error(start.stderr?.trim() || `Failed to start packaged app (exit ${start.status})`)
  }

  const pid = Number((start.stdout ?? '').trim())
  if (!pid) {
    throw new Error(`PowerShell did not return an app PID. stdout=${JSON.stringify(start.stdout)}`)
  }

  return pid
}

function killProcess(pid) {
  spawnSync('powershell', ['-NoProfile', '-Command', `try { Stop-Process -Id ${pid} -Force } catch {}`], {
    cwd: repoRoot,
    encoding: 'utf-8',
  })
}

async function shutdownExistingDaemon() {
  const info = readPortInfo()
  if (!info) return

  try {
    await requestShutdown()
  } catch {
    // If the daemon ignores the shutdown request or the port file is stale,
    // process cleanup below still gives the smoke test a clean slate.
  }

  await sleep(2000)
}

function stopRepoDevDaemonProcesses() {
  const cmd = [
    '$daemon = Get-CimInstance Win32_Process | Where-Object {',
    "  $_.CommandLine -match 'C:\\\\work\\\\sources\\\\nks-ws\\\\src\\\\daemon\\\\NKS\\.WebDevConsole\\.Daemon'",
    '}',
    'if ($daemon) {',
    "  $daemon | ForEach-Object { try { cmd /c ('taskkill /PID ' + $_.ProcessId + ' /T /F') | Out-Null } catch {} }",
    '  ($daemon | Select-Object -ExpandProperty ProcessId) -join \",\"',
    '}',
  ].join(' ')

  const result = spawnSync('powershell', ['-NoProfile', '-Command', cmd], {
    cwd: repoRoot,
    encoding: 'utf-8',
  })

  return result.stdout?.trim() || ''
}

function stopDaemonPortOwner(port) {
  const cmd = [
    `$connections = Get-NetTCPConnection -LocalPort ${port} -ErrorAction SilentlyContinue | Select-Object -ExpandProperty OwningProcess -Unique`,
    'if ($connections) {',
    "  $connections | ForEach-Object { try { cmd /c ('taskkill /PID ' + $_ + ' /T /F') | Out-Null } catch {} }",
    "  ($connections | ForEach-Object { $_.ToString() }) -join ','",
    '}',
  ].join(' ')

  const result = spawnSync('powershell', ['-NoProfile', '-Command', cmd], {
    cwd: repoRoot,
    encoding: 'utf-8',
  })

  return result.stdout?.trim() || ''
}

async function restartRepoDevDaemon() {
  const start = spawnSync('powershell', ['-NoProfile', '-Command', [
    "$proc = Start-Process -FilePath 'dotnet' -ArgumentList @('run','--project','C:\\work\\sources\\nks-ws\\src\\daemon\\NKS.WebDevConsole.Daemon') -WorkingDirectory 'C:\\work\\sources\\nks-ws' -PassThru -WindowStyle Hidden",
    'Write-Output $proc.Id',
  ].join('; ')], {
    cwd: repoRoot,
    encoding: 'utf-8',
  })

  if (start.status !== 0) {
    throw new Error(start.stderr?.trim() || `Failed to restart repo debug daemon (exit ${start.status})`)
  }

  await waitFor(async () => {
    const info = readPortInfo()
    if (!info?.token) return false
    return await probeStatus(info)
  }, {
    timeoutMs: 30000,
    intervalMs: 500,
    label: 'repo debug daemon restart',
  })
}

async function main() {
  const { exe, replaceDevDaemon } = parseArgs(process.argv.slice(2))

  if (process.platform !== 'win32') {
    throw new Error('scripts/smoke-packaged-electron.mjs currently supports Windows only.')
  }

  let restartRepoDaemon = false
  if (await probeDaemon() && replaceDevDaemon) {
    restartRepoDaemon = true
    const currentPort = readPortInfo()?.port ?? 5146
    await shutdownExistingDaemon()
    stopRepoDevDaemonProcesses()
    stopDaemonPortOwner(currentPort)
    await sleep(1500)
  }

  if (await probeDaemon()) {
    throw new Error('An existing daemon instance is already running. Stop it before running the packaged smoke test.')
  }

  if (existsSync(portFile) && !(await probeDaemon())) {
    rmSync(portFile, { force: true })
  }

  if (existsSync(portFile)) {
    renameSync(portFile, backupPortFile)
  }

  let appPid = null
  try {
    appPid = startPackagedApp(exe)
    writeFileSync(join(repoRoot, 'packaged.smoke.pid'), String(appPid), 'utf-8')

    await waitFor(async () => {
      const info = readPortInfo()
      if (!info?.token) return false
      return await probeStatus(info)
    }, {
      timeoutMs: 60000,
      intervalMs: 500,
      label: 'packaged app daemon bootstrap',
    })

    await requestShutdown()

    await waitFor(async () => !(await probeDaemon()) && !existsSync(portFile), {
      timeoutMs: 30000,
      intervalMs: 500,
      label: 'packaged daemon shutdown + port file cleanup',
    })

    console.log(`[smoke-packaged-electron] OK exe=${exe}`)
  } finally {
    rmSync(join(repoRoot, 'packaged.smoke.pid'), { force: true })

    if (appPid) {
      killProcess(appPid)
    }

    if (existsSync(backupPortFile) && !existsSync(portFile)) {
      renameSync(backupPortFile, portFile)
    } else if (existsSync(backupPortFile)) {
      rmSync(backupPortFile, { force: true })
    }

    if (restartRepoDaemon) {
      await restartRepoDevDaemon()
    }
  }
}

main().catch((error) => {
  console.error('[smoke-packaged-electron] fatal:', error?.stack ?? error?.message ?? error)
  process.exit(1)
})
