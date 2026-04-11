#!/usr/bin/env node
/**
 * Isolated daemon lifecycle harness.
 *
 * Starts a fresh daemon instance, runs the
 * shared Node e2e scenarios that exercise lifecycle behavior, then requests a
 * graceful shutdown and verifies the daemon removes the shared port file.
 *
 * Intended for CI and local deep verification where no other daemon instance
 * should be running.
 */
import { existsSync, readFileSync, renameSync, rmSync, writeFileSync } from 'node:fs'
import { spawn, spawnSync } from 'node:child_process'
import { join } from 'node:path'
import { tmpdir } from 'node:os'
import http from 'node:http'

const repoRoot = process.cwd()
const daemonProjectDir = join(repoRoot, 'src', 'daemon', 'NKS.WebDevConsole.Daemon')
const daemonDll = join(daemonProjectDir, 'bin', 'Release', 'net9.0', 'NKS.WebDevConsole.Daemon.dll')
const runnerScript = join(repoRoot, 'scripts', 'e2e-runner.mjs')
const portFile = join(tmpdir(), 'nks-wdc-daemon.port')
const backupPortFile = `${portFile}.bak-isolated`
const fixedDaemonPort = 5146
const daemonStdoutLog = join(repoRoot, 'daemon.isolated.out.log')
const daemonStderrLog = join(repoRoot, 'daemon.isolated.err.log')

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms))
}

function readPortInfo() {
  if (!existsSync(portFile)) return null
  const lines = readFileSync(portFile, 'utf-8').split('\n').filter(Boolean)
  if (lines.length < 2) return null
  return { port: Number(lines[0]), token: lines[1].trim() }
}

function readTail(path, lines = 40) {
  if (!existsSync(path)) return '(log file missing)'
  return readFileSync(path, 'utf-8').split(/\r?\n/).slice(-lines).join('\n')
}

async function probeStatus(info) {
  if (!info?.port || !info?.token) return false

  for (const hostname of ['127.0.0.1', 'localhost']) {
    const ok = await new Promise((resolve) => {
      const req = http.get({
        hostname,
        port: info.port,
        path: '/api/status',
        timeout: 1500,
        headers: { Authorization: `Bearer ${info.token}` },
      }, (res) => {
        res.resume()
        resolve(res.statusCode === 200)
      })
      req.on('error', () => resolve(false))
      req.on('timeout', () => {
        req.destroy()
        resolve(false)
      })
    })

    if (ok) return true
  }

  return false
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

async function runNode(script, args, env = {}) {
  const child = spawn(process.execPath, [script, ...args], {
    cwd: repoRoot,
    env: { ...process.env, ...env },
    stdio: 'inherit',
  })

  const exitCode = await new Promise((resolve, reject) => {
    child.on('error', reject)
    child.on('exit', (code) => resolve(code ?? 1))
  })

  if (exitCode !== 0) {
    throw new Error(`${script} exited with code ${exitCode}`)
  }
}

async function requestShutdown() {
  const info = readPortInfo()
  if (!info) throw new Error('Port file missing before shutdown request')

  const payload = await new Promise((resolve, reject) => {
    const req = http.request(
      {
        hostname: '127.0.0.1',
        port: info.port,
        path: '/api/admin/shutdown',
        method: 'POST',
        headers: {
          Authorization: `Bearer ${info.token}`,
          Accept: 'application/json',
        },
      },
      (res) => {
        const chunks = []
        res.on('data', (chunk) => chunks.push(chunk))
        res.on('end', () => resolve({ status: res.statusCode ?? 0, body: Buffer.concat(chunks).toString('utf-8') }))
      }
    )
    req.on('error', reject)
    req.end()
  })

  if (payload.status !== 202) {
    throw new Error(`Shutdown endpoint returned HTTP ${payload.status}: ${payload.body}`)
  }
}

async function main() {
  const existingDaemon = await probeDaemon()
  if (existingDaemon) {
    throw new Error('An existing daemon instance is already running. Stop it before using the isolated lifecycle harness.')
  }

  if (existsSync(portFile)) {
    renameSync(portFile, backupPortFile)
  }

  let daemonPid = null
  try {
    if (!existsSync(daemonDll)) {
      throw new Error(`Release daemon DLL not found: ${daemonDll}. Build the daemon first with dotnet build -c Release.`)
    }

    if (process.platform !== 'win32') {
      throw new Error('scripts/e2e-isolated-lifecycle.mjs currently supports Windows only.')
    }

    const cmdChain = [
      `set ASPNETCORE_URLS=http://127.0.0.1:${fixedDaemonPort}`,
      `set NKS_WDC_SKIP_HOSTS_UAC=${process.env.NKS_WDC_SKIP_HOSTS_UAC ?? '1'}`,
      `dotnet "${daemonDll}"`,
    ].join(' && ')

    const psCommand = [
      `$proc = Start-Process -FilePath 'cmd.exe' -ArgumentList @('/c', '${cmdChain.replace(/'/g, "''")}') -WorkingDirectory '${daemonProjectDir.replace(/'/g, "''")}' -PassThru -WindowStyle Hidden -RedirectStandardOutput '${daemonStdoutLog.replace(/'/g, "''")}' -RedirectStandardError '${daemonStderrLog.replace(/'/g, "''")}'`,
      'Write-Output $proc.Id',
    ].join('; ')

    const start = spawnSync('powershell', ['-NoProfile', '-Command', psCommand], {
      cwd: repoRoot,
      encoding: 'utf-8',
    })
    if (start.status !== 0) {
      throw new Error(start.stderr?.trim() || `Failed to start daemon via PowerShell (exit ${start.status})`)
    }

    daemonPid = Number((start.stdout ?? '').trim())
    if (!daemonPid) {
      throw new Error(`PowerShell did not return a daemon PID. stdout=${JSON.stringify(start.stdout)}`)
    }

    writeFileSync(join(repoRoot, 'daemon.isolated.pid'), String(daemonPid), 'utf-8')

    try {
      await waitFor(async () => {
        const info = readPortInfo()
        if (!info?.token) return false
        return await probeStatus(info)
      }, {
        timeoutMs: 60000,
        intervalMs: 500,
        label: 'daemon /api/status + port file',
      })
    } catch (error) {
      throw new Error(
        `${error.message}\n--- daemon stdout ---\n${readTail(daemonStdoutLog)}\n--- daemon stderr ---\n${readTail(daemonStderrLog)}`
      )
    }

    await runNode(runnerScript, ['--only', '13,16'])

    await requestShutdown()

    await waitFor(async () => !(await probeDaemon()) && !existsSync(portFile), {
      timeoutMs: 30000,
      intervalMs: 500,
      label: 'daemon shutdown + port file cleanup',
    })

  } finally {
    rmSync(join(repoRoot, 'daemon.isolated.pid'), { force: true })
    rmSync(daemonStdoutLog, { force: true })
    rmSync(daemonStderrLog, { force: true })

    if (existsSync(backupPortFile) && !existsSync(portFile)) {
      renameSync(backupPortFile, portFile)
    } else if (existsSync(backupPortFile)) {
      rmSync(backupPortFile, { force: true })
    }

    if (daemonPid) {
      spawnSync('powershell', ['-NoProfile', '-Command', `try { Stop-Process -Id ${daemonPid} -Force } catch {}`], {
        cwd: repoRoot,
        encoding: 'utf-8',
      })
    }
  }
}

main().catch((error) => {
  console.error('[isolated-e2e] fatal:', error?.stack ?? error?.message ?? error)
  process.exit(1)
})
