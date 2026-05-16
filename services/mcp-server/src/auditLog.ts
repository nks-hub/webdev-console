/**
 * Phase 8 — MCP tool call audit log integration.
 *
 * Wraps every tool registered with the MCP server so that on each call
 * we POST a row to `POST /api/mcp/tool-calls` after the daemon-side
 * handler returns. Logging is fire-and-forget — failures are written
 * to stderr but never bubble up to the AI client.
 *
 * Session id is generated once at MCP server boot and reused across
 * every call so the GUI can group calls into a single AI session
 * (the MCP transport itself is one process per AI connection).
 */

import { createHash, randomUUID } from 'node:crypto'
import { daemonClient } from './daemonClient.js'

const SESSION_ID = randomUUID()

/**
 * Static danger map — tool name → danger level. Read calls dominate
 * traffic so they default to "read"; mutation/destructive tools are
 * explicitly enumerated. Extend this as new tools land.
 */
const DANGER_OVERRIDES: Record<string, 'read' | 'mutate' | 'destructive'> = {
  // Mutating
  wdc_create_site: 'mutate',
  wdc_create_database: 'mutate',
  wdc_create_backup: 'mutate',
  wdc_create_cloudflare_dns: 'mutate',
  wdc_install_binary: 'mutate',
  wdc_install_ca: 'mutate',
  wdc_install_plugin_from_marketplace: 'mutate',
  wdc_enable_plugin: 'mutate',
  wdc_disable_plugin: 'mutate',
  wdc_start_service: 'mutate',
  wdc_stop_service: 'mutate',
  wdc_toggle_php_extension: 'mutate',
  wdc_generate_cert: 'mutate',
  wdc_save_cloudflare_config: 'mutate',
  wdc_update_settings: 'mutate',
  wdc_refresh_catalog: 'mutate',
  wdc_deploy_lock: 'mutate',
  wdc_deploy_unlock: 'mutate',
  wdc_deploy_create_intent: 'mutate',
  wdc_create_mysql_user: 'mutate',
  wdc_set_mysql_user_password: 'mutate',
  wdc_grant_mysql_user_database: 'mutate',
  wdc_change_mysql_root_password: 'mutate',

  // Destructive — irreversible or affects production
  wdc_delete_site: 'destructive',
  wdc_drop_database: 'destructive',
  wdc_drop_mysql_user: 'destructive',
  wdc_reset_mysql_root_password: 'destructive',
  wdc_delete_cloudflare_dns: 'destructive',
  wdc_uninstall_binary: 'destructive',
  wdc_revoke_cert: 'destructive',
  wdc_restore_backup: 'destructive',
  wdc_deploy_site: 'destructive',
  wdc_deploy_rollback: 'destructive',
  wdc_deploy_group_rollback: 'destructive',
  wdc_deploy_cancel: 'destructive',
  wdc_deploy_restore_snapshot: 'destructive',
  wdc_deploy_group_start: 'destructive',
  wdc_execute: 'destructive',
  wdc_query: 'destructive',
}

function classify(toolName: string): 'read' | 'mutate' | 'destructive' {
  return DANGER_OVERRIDES[toolName] ?? 'read'
}

function summarize(args: unknown): { summary: string; hash: string } {
  let json: string
  try {
    json = JSON.stringify(args ?? {})
  } catch {
    json = '<unserializable>'
  }
  const hash = createHash('sha256').update(json).digest('hex').slice(0, 16)
  const summary = json.length > 500 ? json.slice(0, 497) + '...' : json
  return { summary, hash }
}

/**
 * Wrap an MCP tool handler with audit logging. Returns a new function
 * with the same signature; the wrapped function handles every call as:
 *
 *   1. Capture start time
 *   2. Invoke original handler
 *   3. POST audit row (fire-and-forget) with duration + result
 *   4. Re-throw original error if any
 *
 * The MCP server.registerTool() expects a function returning a
 * `{ content: [...] }` shape (or throwing). Errors are still propagated
 * to the AI client — we just additionally log them.
 */
export function wrapHandler<TArgs extends Record<string, unknown>, TResult>(
  toolName: string,
  handler: (args: TArgs, extra?: unknown) => Promise<TResult>,
): (args: TArgs, extra?: unknown) => Promise<TResult> {
  const danger = classify(toolName)
  return async (args, extra) => {
    const start = Date.now()
    let resultCode = 'ok'
    let errorMessage: string | undefined
    try {
      const result = await handler(args, extra)
      // safe() in formatting.ts catches every thrown daemon error and
      // returns toolError({isError:true}) instead of rethrowing — so
      // every failure that flows through it would otherwise be logged
      // as resultCode='ok'. Inspect the MCP-protocol-level isError
      // flag and downgrade the audit row when it's set, and lift the
      // error text out of content[0].text into errorMessage so the
      // audit feed shows what actually went wrong.
      // Incident 2026-05-07: every MCP daemon error was logged as ok.
      const r = result as unknown as {
        isError?: boolean
        content?: { type?: string; text?: string }[]
      } | null | undefined
      if (r && r.isError === true) {
        resultCode = 'error'
        const text = r.content?.[0]?.text
        if (typeof text === 'string') {
          errorMessage = text.slice(0, 500)
        } else {
          errorMessage = 'tool returned isError without text body'
        }
      }
      return result
    } catch (err) {
      resultCode = 'error'
      errorMessage = err instanceof Error ? err.message.slice(0, 500) : String(err).slice(0, 500)
      throw err
    } finally {
      const duration = Date.now() - start
      const { summary, hash } = summarize(args)
      // Fire-and-forget — POST may fail because the daemon is being
      // restarted mid-call; do not block the tool response on it.
      daemonClient
        .post('/api/mcp/tool-calls', {
          toolName,
          caller: 'mcp-server',
          sessionId: SESSION_ID,
          dangerLevel: danger,
          durationMs: duration,
          resultCode,
          errorMessage,
          argsSummary: summary,
          argsHash: hash,
        })
        .catch(err => {
          process.stderr.write(`[audit] failed to log ${toolName}: ${err}\n`)
        })
    }
  }
}
