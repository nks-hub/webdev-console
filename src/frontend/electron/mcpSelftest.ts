import { spawn } from 'child_process'

/** Outcome of starting the MCP server the way an AI client does and calling one tool. */
export interface McpSelftestResult {
  /** The server process started and answered the MCP initialize handshake. */
  started: boolean
  /** wdc_get_status reached the daemon with the MCP token. */
  ok: boolean
  /** Tool error, server stderr or spawn error when something failed. */
  message: string
}

/**
 * Starts the MCP server with `command args` (env included), speaks the MCP
 * stdio protocol (newline-delimited JSON-RPC) like an AI client, calls
 * wdc_get_status and stops the server again.
 */
export function runMcpSelftest(
  command: string,
  args: string[],
  env: NodeJS.ProcessEnv,
  timeoutMs = 15_000,
): Promise<McpSelftestResult> {
  return new Promise((resolve) => {
    let started = false
    let settled = false
    let stderr = ''
    let stdout = ''
    const child = spawn(command, args, { env, stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true })
    const finish = (result: McpSelftestResult): void => {
      if (settled) return
      settled = true
      clearTimeout(timer)
      child.kill()
      resolve({ ...result, message: result.message.slice(0, 800) })
    }
    const timer = setTimeout(
      () => finish({ started, ok: false, message: stderr.trim() || `no answer within ${timeoutMs / 1000} s` }),
      timeoutMs,
    )
    const send = (msg: object): void => { child.stdin.write(JSON.stringify(msg) + '\n') }

    child.on('error', (err) => finish({ started: false, ok: false, message: err.message }))
    child.on('exit', (code) => finish({ started, ok: false, message: stderr.trim() || `exited with code ${code}` }))
    // A write after the process died must not crash the main process.
    child.stdin.on('error', () => {})
    child.stderr.on('data', (d: Buffer) => { stderr += d.toString() })
    child.stdout.on('data', (d: Buffer) => {
      stdout += d.toString()
      let nl: number
      while ((nl = stdout.indexOf('\n')) >= 0) {
        const line = stdout.slice(0, nl).trim()
        stdout = stdout.slice(nl + 1)
        let msg: {
          id?: number
          error?: { message?: string }
          result?: { isError?: boolean; content?: Array<{ text?: string }> }
        }
        try { msg = JSON.parse(line) } catch { continue }
        if (msg.id === 1) {
          if (msg.error) return finish({ started: false, ok: false, message: msg.error.message ?? 'initialize failed' })
          started = true
          send({ jsonrpc: '2.0', method: 'notifications/initialized' })
          send({ jsonrpc: '2.0', id: 2, method: 'tools/call', params: { name: 'wdc_get_status', arguments: {} } })
        } else if (msg.id === 2) {
          if (msg.error) return finish({ started, ok: false, message: msg.error.message ?? 'tools/call failed' })
          const text = (msg.result?.content ?? []).map((c) => c.text ?? '').join('\n').trim()
          return finish({ started, ok: msg.result?.isError !== true, message: text })
        }
      }
    })

    send({
      jsonrpc: '2.0',
      id: 1,
      method: 'initialize',
      params: { protocolVersion: '2024-11-05', capabilities: {}, clientInfo: { name: 'wdc-selftest', version: '1' } },
    })
  })
}
