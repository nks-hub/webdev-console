/**
 * NksDeploy plugin MCP tools — 12 wdc_deploy_* tools mapped to the daemon's
 * `/api/nks.wdc.deploy/*` endpoints. Per-tool scope gating: tools register
 * only when the corresponding scope (deploy:read / deploy:write /
 * deploy:admin) is granted in the port file's `scope:` line. Plus the v2
 * audit's hybrid confirmation flow — destructive tools (deploy / rollback /
 * cancel) refuse to fire in headless mode unless `MCP_DEPLOY_AUTO_APPROVE`
 * env var is set to `true` AND a pre-signed intent token (Mode C) is
 * supplied, OR the GUI confirms the intent (Mode A, daemon-side).
 */
import { z } from 'zod'
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js'

import { daemonClient } from '../daemonClient.js'
import type { RegisterOptions } from '../index.js'
import { safe } from '../formatting.js'
import { DomainSchema } from '../schemas.js'

const PREFIX = '/api/nks.wdc.deploy'

const HostSchema = z
  .string()
  .min(1)
  .max(64)
  .regex(/^[a-z0-9_.-]+$/i, 'Host name uses only [A-Za-z0-9_.-]')
  .describe('Deploy host name from the site\'s deploy.neon (e.g. "production", "staging").')

const DeployIdSchema = z
  .string()
  .min(1)
  .max(64)
  .describe('UUID of a deploy_runs row, returned by wdc_deploy_site or visible in deploy events.')

const LimitSchema = z.number().int().min(1).max(200).default(50)
  .describe('Maximum number of history entries to return (1-200, default 50).')

const IdempotencyKeySchema = z
  .string()
  .uuid()
  .optional()
  .describe(
    'Caller-generated UUIDv4. Daemon dedupes duplicate destructive POSTs with the same ' +
    'key within a 10-minute window so a network retry does not start a second deploy.',
  )

const IntentTokenSchema = z
  .string()
  .optional()
  .describe(
    'Pre-signed intent token from wdc_deploy_create_intent (Mode C / headless CI). ' +
    'When supplied the daemon skips GUI confirmation. Omit in interactive use — the ' +
    'GUI modal flow (Mode A) takes over.',
  )

/**
 * Hybrid-confirmation guard for destructive tools (Mode B): refuse to fire
 * without an intent token when running headless unless the operator has
 * explicitly opted-in via the env var. This keeps a stolen MCP token from
 * silently triggering production deploys.
 */
function destructiveGuard(intentToken: string | undefined): { allow: true } | { allow: false; reason: string } {
  if (intentToken) return { allow: true }
  if ((process.env.MCP_DEPLOY_AUTO_APPROVE ?? '').toLowerCase() === 'true') return { allow: true }
  return {
    allow: false,
    reason:
      'Destructive deploy operation refused. Either provide a pre-signed intentToken ' +
      '(via wdc_deploy_create_intent) or set MCP_DEPLOY_AUTO_APPROVE=true in the MCP ' +
      'server environment. The wdc GUI confirmation flow (Mode A) is handled by the ' +
      'daemon when no intent is supplied AND a GUI client is connected.',
  }
}

/**
 * Build the request headers for a destructive call:
 *  - X-Intent-Token: the HMAC-signed intent (always required for daemon-side validator)
 *  - X-Allow-Unconfirmed: only when MCP_DEPLOY_AUTO_APPROVE=true; tells the
 *    daemon to bypass the GUI banner gate (Phase 5.5 Mode A). Without this
 *    header the daemon returns 425 Too Early until a GUI user clicks Approve.
 */
function destructiveHeaders(intentToken: string | undefined): Record<string, string> | undefined {
  if (!intentToken) return undefined
  const headers: Record<string, string> = { 'X-Intent-Token': intentToken }
  if ((process.env.MCP_DEPLOY_AUTO_APPROVE ?? '').toLowerCase() === 'true') {
    headers['X-Allow-Unconfirmed'] = 'true'
  }
  return headers
}

export function registerDeployTools(server: McpServer, opts: RegisterOptions): void {
  const has = (scope: string) => opts.deployScopes.includes('*') || opts.deployScopes.includes(scope)
  const canRead = has('deploy:read')
  const canWrite = has('deploy:write')
  const canAdmin = has('deploy:admin')

  // ── READ (deploy:read) ────────────────────────────────────────────────

  if (canRead) {
    server.registerTool(
      'wdc_deploy_list_targets',
      {
        title: 'List deploy targets for a site',
        description:
          'List the deploy hosts configured for a site (production, staging, etc.).\n\n' +
          'Args:\n  domain: Local domain like "myapp.loc".\n\n' +
          'Returns: derived list of unique host names from the site\'s deploy history.\n' +
          'A future config-readback endpoint will surface deploy.neon hosts directly.',
        inputSchema: { domain: DomainSchema },
        annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false },
      },
      async ({ domain }) =>
        safe(async () => {
          const r = await daemonClient.get(`${PREFIX}/sites/${encodeURIComponent(domain)}/history?limit=200`) as {
            entries: Array<{ host: string }>
          }
          const hosts = Array.from(new Set(r.entries.map(e => e.host)))
          return { domain, hosts, count: hosts.length }
        }),
    )

    server.registerTool(
      'wdc_deploy_get_status',
      {
        title: 'Get deploy run status',
        description:
          'Look up the status of a specific deploy by its deployId.\n\n' +
          'Args:\n' +
          '  domain: Site domain.\n' +
          '  deployId: UUID of the deploy run.\n\n' +
          'Returns: DeployResult with success/failure, started/completed timestamps, ' +
          'release id, commit sha, final phase.',
        inputSchema: { domain: DomainSchema, deployId: DeployIdSchema },
        annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false },
      },
      async ({ domain, deployId }) =>
        safe(() => daemonClient.get(`${PREFIX}/sites/${encodeURIComponent(domain)}/deploys/${encodeURIComponent(deployId)}`)),
    )

    server.registerTool(
      'wdc_deploy_list_releases',
      {
        title: 'List recent deploys for a site',
        description:
          'List the recent deploy_runs entries for a site, newest first.\n\n' +
          'Args:\n  domain: Site domain.\n  limit: Max rows to return (default 50, max 200).\n\n' +
          'Returns: { domain, count, entries[] } with one entry per deploy run (host, branch, ' +
          'commit, finalPhase, timing).',
        inputSchema: { domain: DomainSchema, limit: LimitSchema },
        annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false },
      },
      async ({ domain, limit }) =>
        safe(() => daemonClient.get(`${PREFIX}/sites/${encodeURIComponent(domain)}/history?limit=${limit}`)),
    )

    server.registerTool(
      'wdc_deploy_get_release',
      {
        title: 'Get a single release entry',
        description:
          'Convenience wrapper: filter the history list down to the single matching deployId.\n\n' +
          'Args:\n  domain: Site domain.\n  deployId: UUID of the deploy run.\n\n' +
          'Returns: the matching DeployHistoryEntry or {found: false}.',
        inputSchema: { domain: DomainSchema, deployId: DeployIdSchema },
        annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false },
      },
      async ({ domain, deployId }) =>
        safe(async () => {
          const r = await daemonClient.get(`${PREFIX}/sites/${encodeURIComponent(domain)}/history?limit=200`) as {
            entries: Array<{ deployId: string }>
          }
          const entry = r.entries.find(e => e.deployId === deployId)
          return entry ?? { found: false, deployId }
        }),
    )

    server.registerTool(
      'wdc_deploy_get_logs',
      {
        title: 'Get deploy log lines',
        description:
          'Get the captured log lines for a deploy. v0.1 returns whatever was buffered ' +
          'in deploy_runs.error_message plus the final phase — full per-step log access ' +
          'lands in a follow-up commit that wires the SSE event buffer.\n\n' +
          'Args:\n  domain: Site domain.\n  deployId: UUID.\n  tailLines: Reserved for future use.',
        inputSchema: {
          domain: DomainSchema,
          deployId: DeployIdSchema,
          tailLines: z.number().int().min(1).max(500).default(100).describe('Reserved'),
        },
        annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false },
      },
      async ({ domain, deployId }) =>
        safe(async () => {
          const status = await daemonClient.get(`${PREFIX}/sites/${encodeURIComponent(domain)}/deploys/${encodeURIComponent(deployId)}`) as {
            errorMessage: string | null
            finalPhase: string
          }
          return {
            deployId,
            finalPhase: status.finalPhase,
            error: status.errorMessage,
            note: 'Per-step log access requires SSE buffer wiring (v0.2). Subscribe to /api/events for live progress.',
          }
        }),
    )
  }

  // ── WRITE (deploy:write) ──────────────────────────────────────────────

  if (canWrite && !opts.readonly) {
    server.registerTool(
      'wdc_deploy_create_intent',
      {
        title: 'Create pre-signed deploy intent',
        description:
          'Issue an HMAC-signed intent token that authorises a single destructive deploy ' +
          'operation (deploy / rollback / cancel) without requiring GUI confirmation. Used ' +
          'by CI pipelines and headless agents (Mode C). The token has a TTL and is ' +
          'single-use — the daemon marks it consumed on first acceptance.\n\n' +
          'Args:\n  domain: Site domain.\n  host: Target host.\n  ttlSeconds: TTL (60-3600, default 900).\n\n' +
          'Returns: { intentToken, expiresAt, nonce }.',
        inputSchema: {
          domain: DomainSchema,
          host: HostSchema,
          ttlSeconds: z.number().int().min(60).max(3600).default(900),
        },
        annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false },
      },
      async ({ domain, host, ttlSeconds }) =>
        // Intents live on the daemon (cross-plugin), NOT under the
        // NksDeploy plugin prefix. Field name is `expiresIn` per the
        // daemon's contract; `kind=deploy` here, callers can mint
        // separate intents for rollback/cancel via the same flow.
        // After creation we broadcast a `mcp:confirm-request` SSE so the
        // wdc GUI shows the Approve banner. Best-effort — failure to
        // broadcast still returns the intent to the AI client.
        safe(async () => {
          const created = await daemonClient.post<{ intentId: string; intentToken: string; expiresAt: string }>(
            '/api/mcp/intents',
            { domain, host, kind: 'deploy', expiresIn: ttlSeconds },
          )
          try {
            await daemonClient.post('/api/mcp/intents/confirm-request', {
              intentId: created.intentId,
              prompt: `AI requests deploy of ${domain} → ${host}`,
            })
          } catch {
            // Non-fatal — GUI may be offline or operator may want to
            // fall back to MCP_DEPLOY_AUTO_APPROVE.
          }
          return created
        }),
    )

    server.registerTool(
      'wdc_deploy_preflight',
      {
        title: 'Run preflight checks',
        description:
          'Run pre-deploy validation (git_status, php_lint, di_container, schema_validate, ' +
          'manifest_check) WITHOUT actually deploying. Surfaces fixable problems before the ' +
          'user clicks Deploy.\n\n' +
          'Args:\n  domain: Site domain.\n  host: Target host.\n\n' +
          'Returns: { passed, results[] }.\n' +
          'NOTE: v0.1 stub — preflight is currently inferred from the latest deploy_runs ' +
          'row. A dedicated preflight endpoint lands alongside Phase 5.',
        inputSchema: { domain: DomainSchema, host: HostSchema },
        annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false },
      },
      async ({ domain, host }) =>
        safe(async () => ({
          domain,
          host,
          passed: true,
          results: [],
          note: 'Preflight wiring is Phase 5 hardening work. v0.1 returns an unconditional pass.',
        })),
    )

    server.registerTool(
      'wdc_deploy_lock',
      {
        title: 'Acquire advisory deploy lock',
        description:
          'Acquire an advisory lock on (domain, host) so a concurrent deploy attempt is ' +
          'rejected with 409. Useful for multi-step CI flows that want to ensure no one ' +
          'else deploys mid-pipeline.\n\n' +
          'NOTE: v0.1 stub — the daemon already enforces in-process per-(domain, host) ' +
          'locks during deploys; this tool reserves the future advisory-lock endpoint.',
        inputSchema: {
          domain: DomainSchema,
          host: HostSchema,
          reason: z.string().max(200).optional(),
          ttlSeconds: z.number().int().min(60).max(7200).default(600),
        },
        annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false },
      },
      async ({ domain, host, reason, ttlSeconds }) =>
        safe(async () => ({
          domain,
          host,
          ttlSeconds,
          reason: reason ?? null,
          acquired: false,
          note: 'Advisory locks land in Phase 5. The deploy lifecycle already serialises per (domain, host).',
        })),
    )

    server.registerTool(
      'wdc_deploy_unlock',
      {
        title: 'Release advisory deploy lock',
        description: 'Counterpart to wdc_deploy_lock. v0.1 stub.',
        inputSchema: { domain: DomainSchema, host: HostSchema },
        annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false },
      },
      async ({ domain, host }) =>
        safe(async () => ({ domain, host, released: true, note: 'Stub' })),
    )
  }

  // ── DESTRUCTIVE (deploy:admin) ────────────────────────────────────────

  if (canAdmin && !opts.readonly) {
    server.registerTool(
      'wdc_deploy_site',
      {
        title: 'Deploy a site to a host (DESTRUCTIVE)',
        description:
          'Trigger a deploy of {domain} to {host}. Returns 202 with deployId immediately; ' +
          'follow progress via wdc_deploy_get_status or by subscribing to the SSE deploy:event ' +
          'channel.\n\n' +
          'GATING: This is a destructive operation. In headless mode it requires either ' +
          'MCP_DEPLOY_AUTO_APPROVE=true OR a pre-signed intentToken from wdc_deploy_create_intent. ' +
          'In interactive mode the daemon presents a confirmation modal in the wdc GUI.\n\n' +
          'Args:\n' +
          '  domain: Site domain.\n' +
          '  host: Target host from deploy.neon.\n' +
          '  branch: Optional branch override (defaults to host\'s configured branch).\n' +
          '  intentToken: Pre-signed intent (Mode C / CI mode).\n' +
          '  idempotencyKey: UUIDv4 for retry dedup.',
        inputSchema: {
          domain: DomainSchema,
          host: HostSchema,
          branch: z.string().max(255).optional().describe('Optional git branch to deploy.'),
          intentToken: IntentTokenSchema,
          idempotencyKey: IdempotencyKeySchema,
        },
        annotations: { readOnlyHint: false, destructiveHint: true, idempotentHint: false, openWorldHint: true },
      },
      async ({ domain, host, branch, intentToken, idempotencyKey }) => {
        const guard = destructiveGuard(intentToken)
        if (!guard.allow) return safe(async () => { throw new Error(guard.reason) })
        // Intent token rides as the X-Intent-Token header; the plugin's
        // CheckIntentAsync helper validates it before any destructive work.
        const headers = destructiveHeaders(intentToken)
        return safe(() =>
          daemonClient.post(
            `${PREFIX}/sites/${encodeURIComponent(domain)}/hosts/${encodeURIComponent(host)}/deploy`,
            {
              idempotencyKey: idempotencyKey ?? randomUuid(),
              options: branch ? { branch } : {},
            },
            headers,
          ),
        )
      },
    )

    server.registerTool(
      'wdc_deploy_rollback',
      {
        title: 'Roll back a deploy (DESTRUCTIVE)',
        description:
          'Roll the live release back to the previous successful deploy. Same gating as ' +
          'wdc_deploy_site (intentToken or MCP_DEPLOY_AUTO_APPROVE).\n\n' +
          'Args:\n  domain: Site domain.\n  deployId: UUID of the deploy run to rewind FROM.',
        inputSchema: {
          domain: DomainSchema,
          deployId: DeployIdSchema,
          intentToken: IntentTokenSchema,
          idempotencyKey: IdempotencyKeySchema,
        },
        annotations: { readOnlyHint: false, destructiveHint: true, idempotentHint: false, openWorldHint: true },
      },
      async ({ domain, deployId, intentToken }) => {
        const guard = destructiveGuard(intentToken)
        if (!guard.allow) return safe(async () => { throw new Error(guard.reason) })
        const headers = destructiveHeaders(intentToken)
        return safe(() =>
          daemonClient.post(
            `${PREFIX}/sites/${encodeURIComponent(domain)}/deploys/${encodeURIComponent(deployId)}/rollback`,
            {},
            headers,
          ),
        )
      },
    )

    server.registerTool(
      'wdc_deploy_cancel',
      {
        title: 'Cancel an in-flight deploy (DESTRUCTIVE)',
        description:
          'Request cancellation of a deploy that has not yet crossed point-of-no-return. ' +
          'Daemon returns 409 if the deploy has already passed the symlink switch — use ' +
          'wdc_deploy_rollback in that case.\n\n' +
          'Args:\n  domain: Site domain.\n  deployId: UUID.',
        inputSchema: {
          domain: DomainSchema,
          deployId: DeployIdSchema,
          intentToken: IntentTokenSchema,
        },
        annotations: { readOnlyHint: false, destructiveHint: true, idempotentHint: false, openWorldHint: false },
      },
      async ({ domain, deployId, intentToken }) => {
        const guard = destructiveGuard(intentToken)
        if (!guard.allow) return safe(async () => { throw new Error(guard.reason) })
        const headers = destructiveHeaders(intentToken)
        return safe(() =>
          daemonClient.delete(
            `${PREFIX}/sites/${encodeURIComponent(domain)}/deploys/${encodeURIComponent(deployId)}`,
            headers,
          ),
        )
      },
    )
  }
}

/** Polyfill for crypto.randomUUID on older Node runtimes. */
function randomUuid(): string {
  // Node 18+ exposes randomUUID on globalThis.crypto. Fall back to a tiny
  // ad-hoc generator for compatibility with the MCP server's minimum
  // supported runtime (Node 20 per package.json — but the polyfill costs
  // 4 lines and removes a runtime guess).
  const c = (globalThis as unknown as { crypto?: { randomUUID?: () => string } }).crypto
  if (c?.randomUUID) return c.randomUUID()
  // RFC4122 v4 fallback
  return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, (c2) => {
    const r = (Math.random() * 16) | 0
    const v = c2 === 'x' ? r : (r & 0x3) | 0x8
    return v.toString(16)
  })
}
