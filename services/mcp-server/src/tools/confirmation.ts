// Human confirmation for destructive operations.
//
// The daemon gives the MCP server its own token. Requests made with it must
// carry a GUI-confirmed intent (X-Intent-Token) on every destructive route;
// the agent cannot approve an intent itself. This tool mints the intent for
// a given operation and asks the WDC app to show the Approve banner. The
// agent then calls the destructive tool again with the returned intentToken
// once the user has approved it (until then the daemon answers 425
// pending_confirmation).

import { z } from 'zod'
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js'

import { daemonClient, DaemonRequestError } from '../daemonClient.js'
import type { RegisterOptions } from '../index.js'
import { safe } from '../formatting.js'

// operation → the (kind, domain, host) triple the daemon's intent gate checks
// for that route. `target` is the route's subject (database, domain, service
// id, …); routes with a fixed subject ignore it.
export const CONFIRMABLE_OPERATIONS = {
  database_drop: { kind: 'database_drop', host: '*db*', describe: 'drop database' },
  database_execute: { kind: 'database_query', host: '*db*', describe: 'run SQL that can change data in' },
  database_import: { kind: 'database_import', host: '*db*', describe: 'import a dump into database' },
  site_delete: { kind: 'site_delete', host: '*site*', describe: 'delete site' },
  service_stop: { kind: 'service_restart', host: '*svc*', describe: 'stop service' },
  ssl_cert_delete: { kind: 'ssl_cert_delete', host: '*ssl*', describe: 'revoke the certificate of' },
  dns_record_delete: { kind: 'dns_record_delete', host: '*dns*', describe: 'delete a DNS record in zone' },
  binary_install: { kind: 'binary_install', host: '*binary*', describe: 'install binary' },
  plugin_install: { kind: 'plugin_install', host: '*plugin*', describe: 'install plugin' },
  plugin_uninstall: { kind: 'plugin_uninstall', host: '*plugin*', describe: 'uninstall plugin' },
  backup_restore: { kind: 'backup_restore', host: '*backup*', describe: 'restore a backup over the current state', fixedDomain: '*backup*' },
  mysql_user_drop: { kind: 'mysql_user_drop', host: '*mysql*', describe: 'drop the MySQL account' },
  mysql_root_reset: { kind: 'mysql_root_reset', host: '*mysql*', describe: 'reset the MySQL root password', fixedDomain: 'root' },
  // Capability groups the operator enables in WDC → Settings → MCP; each
  // call in them still needs an approved intent (host '*mcp*').
  cloudflare_credentials: { kind: 'cloudflare_credentials', host: '*mcp*', describe: 'change Cloudflare credentials or tunnel routing for' },
  node_process: { kind: 'node_process', host: '*mcp*', describe: 'set or run the Node.js start command of site' },
  service_config_write: { kind: 'service_config_write', host: '*mcp*', describe: 'rewrite the config file of service' },
  hosts_write: { kind: 'hosts_write', host: '*mcp*', describe: 'rewrite the system hosts file', fixedDomain: 'hosts' },
  daemon_admin: { kind: 'daemon_admin', host: '*mcp*', describe: 'run the daemon admin action' },
  // Deploy routes (target = site domain). A deploy itself is bound to one host,
  // so it is minted with wdc_deploy_create_intent, which takes the host.
  deploy_rollback: { kind: 'rollback', host: '*', describe: 'roll back the live release (deploy or group) of site' },
  deploy_cancel: { kind: 'cancel', host: '*', describe: 'cancel the running deploy of site' },
  deploy_restore: { kind: 'restore', host: '*restore*', describe: 'restore a pre-deploy database snapshot over the live data of site' },
} as const

export type ConfirmableOperation = keyof typeof CONFIRMABLE_OPERATIONS

export function intentFor(operation: ConfirmableOperation, target: string): { kind: string; domain: string; host: string } {
  const op = CONFIRMABLE_OPERATIONS[operation]
  const domain = 'fixedDomain' in op ? op.fixedDomain : target
  return { kind: op.kind, domain, host: op.host }
}

/** X-Intent-Token header for a destructive daemon call, when the agent has one. */
export function intentHeaders(intentToken: string | undefined): Record<string, string> | undefined {
  return intentToken ? { 'X-Intent-Token': intentToken } : undefined
}

export const IntentTokenSchema = z
  .string()
  .min(10)
  .max(512)
  .optional()
  .describe(
    'Intent token from wdc_request_confirmation, after the user approved it in the WDC app. ' +
      'Required for this operation when the daemon gave the MCP server its own token.',
  )

/** Normalised lifecycle of an intent, as wdc_get_confirmation_status reports it. */
export type ConfirmationStatus = 'pending' | 'ready' | 'consumed' | 'expired' | 'revoked' | 'unknown'

export interface IntentState {
  intentId: string
  status: ConfirmationStatus
  expiresAt?: string
  confirmedAt?: string | null
  usedAt?: string | null
  revokedAt?: string | null
  kind?: string
  domain?: string
  host?: string
}

interface DaemonIntentRow {
  intentId: string
  state?: string
  expiresAt?: string
  confirmedAt?: string | null
  usedAt?: string | null
  revokedAt?: string | null
  kind?: string
  domain?: string
  host?: string
}

export function normaliseState(state: string | undefined): ConfirmationStatus {
  switch (state) {
    case 'pending_confirmation':
    case 'pending':
      return 'pending'
    case 'ready':
    case 'consumed':
    case 'expired':
    case 'revoked':
      return state
    default:
      return 'unknown'
  }
}

const NEXT_BY_STATUS: Record<ConfirmationStatus, string> = {
  pending: 'The user has not decided yet. Ask them to approve the request in WDC; check again or retry the operation with the intentToken afterwards.',
  ready: 'Approved. Call the operation\'s tool now with the intentToken.',
  consumed: 'This intent was already used. Request a new confirmation for another run.',
  expired: 'The request expired before it was used. Request a new confirmation if the user still wants this.',
  revoked: 'The user revoked this request. Do not retry unless the user asks again.',
  unknown: 'The daemon reported a state this server does not know; check the WDC app.',
}

function toState(row: DaemonIntentRow): IntentState {
  return {
    intentId: row.intentId,
    status: normaliseState(row.state),
    expiresAt: row.expiresAt,
    confirmedAt: row.confirmedAt ?? null,
    usedAt: row.usedAt ?? null,
    revokedAt: row.revokedAt ?? null,
    kind: row.kind,
    domain: row.domain,
    host: row.host,
  }
}

/**
 * Reads one intent's state. Daemons without `GET /api/mcp/intents/{id}` answer
 * a bare 404; for those the intent is looked up in the inventory list instead.
 */
export async function fetchIntentState(intentId: string): Promise<IntentState> {
  try {
    const row = await daemonClient.get<DaemonIntentRow>(`/api/mcp/intents/${encodeURIComponent(intentId)}`)
    return toState(row)
  } catch (err) {
    if (!(err instanceof DaemonRequestError) || err.status !== 404 || err.code) throw err
  }
  const list = await daemonClient.get<{ entries?: DaemonIntentRow[] }>('/api/mcp/intents?limit=500')
  const row = list.entries?.find(e => e.intentId === intentId)
  if (!row) throw new Error(`Intent ${intentId} was not found (it may have been cleaned up). Request a new confirmation.`)
  return toState(row)
}

export interface WaitOptions {
  sleep?: (ms: number) => Promise<void>
  now?: () => number
  pollMs?: number
}

/**
 * Polls the intent until it leaves `pending` or `waitSeconds` pass. Returns
 * the last state seen; `waitSeconds` 0 reads it once.
 */
export async function waitForIntent(intentId: string, waitSeconds: number, opts: WaitOptions = {}): Promise<IntentState> {
  const sleep = opts.sleep ?? ((ms: number) => new Promise<void>(r => setTimeout(r, ms)))
  const now = opts.now ?? Date.now
  const pollMs = opts.pollMs ?? 2000
  const deadline = now() + Math.min(Math.max(waitSeconds, 0), MAX_WAIT_SECONDS) * 1000
  for (;;) {
    const state = await fetchIntentState(intentId)
    if (state.status !== 'pending') return state
    const left = deadline - now()
    if (left <= 0) return state
    await sleep(Math.min(pollMs, left))
  }
}

export const MAX_WAIT_SECONDS = 120

const WaitSecondsSchema = z
  .number()
  .int()
  .min(0)
  .max(MAX_WAIT_SECONDS)
  .default(0)
  .describe(
    `Wait up to this many seconds (max ${MAX_WAIT_SECONDS}) for the user to approve or reject before returning. ` +
      '0 returns immediately.',
  )

const IntentIdSchema = z
  .string()
  .min(1)
  .max(128)
  .regex(/^[A-Za-z0-9-]+$/, 'intentId is the UUID returned by wdc_request_confirmation')
  .describe('intentId returned by wdc_request_confirmation (not the intentToken).')

export interface ConfirmationRequest {
  kind: string
  domain: string
  host: string
  ttlSeconds: number
  prompt: string
  waitSeconds?: number
  releaseId?: string
}

/**
 * Mints an intent and asks WDC to show the approval banner. When the intent
 * exists but the banner request fails, the intent is returned with a
 * warning instead of an error, so the agent can still have it approved in
 * WDC → MCP → Intents rather than minting duplicates.
 */
export async function requestConfirmation(req: ConfirmationRequest, wait?: WaitOptions): Promise<Record<string, unknown>> {
  const created = await daemonClient.post<{ intentId: string; intentToken: string; expiresAt: string }>(
    '/api/mcp/intents',
    {
      domain: req.domain,
      host: req.host,
      kind: req.kind,
      expiresIn: req.ttlSeconds,
      ...(req.releaseId ? { releaseId: req.releaseId } : {}),
    },
  )
  const base = { ...created, kind: req.kind, domain: req.domain, host: req.host }
  try {
    await daemonClient.post('/api/mcp/intents/confirm-request', { intentId: created.intentId, prompt: req.prompt })
  } catch (err) {
    return {
      ...base,
      status: 'pending',
      warning:
        'The intent was created but WDC could not be asked to show the approval banner: ' +
        (err instanceof Error ? err.message : String(err)) +
        '. Ask the user to approve intent ' + created.intentId + ' in WDC → MCP → Intents, ' +
        'then call the operation with this intentToken. Do not request another confirmation for the same operation.',
    }
  }
  if (!req.waitSeconds) {
    return {
      ...base,
      status: 'pending',
      next:
        'Ask the user to approve the request in the WDC app, then call the operation again with this intentToken. ' +
        'wdc_get_confirmation_status(intentId) tells whether they have approved.',
    }
  }
  try {
    const state = await waitForIntent(created.intentId, req.waitSeconds, wait)
    return { ...base, status: state.status, expiresAt: state.expiresAt ?? created.expiresAt, next: NEXT_BY_STATUS[state.status] }
  } catch (err) {
    return {
      ...base,
      status: 'pending',
      warning:
        'The approval request was sent, but its status could not be read: ' +
        (err instanceof Error ? err.message : String(err)) +
        '. Retry the operation with this intentToken once the user has approved.',
    }
  }
}

export function registerConfirmationTools(server: McpServer, opts: RegisterOptions): void {
  server.registerTool(
    'wdc_get_confirmation_status',
    {
      title: 'Check whether the user approved a request',
      description:
        'Report the state of an approval request made with wdc_request_confirmation (or ' +
        'wdc_deploy_create_intent).\n\n' +
        'Args:\n  intentId: The intentId from that call.\n' +
        `  waitSeconds: Optionally wait up to ${MAX_WAIT_SECONDS}s for the user to decide.\n\n` +
        'Returns: { intentId, status, expiresAt, confirmedAt, usedAt, revokedAt, kind, domain, host, next } where ' +
        'status is pending (not approved yet), ready (approved, call the tool now), consumed (already used), ' +
        'expired, or revoked (the user withdrew it).',
      inputSchema: {
        intentId: IntentIdSchema,
        waitSeconds: WaitSecondsSchema,
      },
      annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false },
    },
    async ({ intentId, waitSeconds }) =>
      safe(async () => {
        const state = await waitForIntent(intentId, waitSeconds)
        return { ...state, next: NEXT_BY_STATUS[state.status] }
      }),
  )

  if (opts.readonly) return

  const operations = Object.keys(CONFIRMABLE_OPERATIONS) as [ConfirmableOperation, ...ConfirmableOperation[]]

  server.registerTool(
    'wdc_request_confirmation',
    {
      title: 'Ask the user to approve a destructive operation',
      description:
        'Destructive operations (dropping or changing databases, deleting sites, stopping services, ' +
        'revoking certificates, deleting DNS records, installing binaries or plugins, restoring backups, ' +
        'dropping MySQL accounts, resetting the MySQL root password) ' +
        'need the user\'s approval in the WDC app. Call this first, tell the user what you want to do and ' +
        'why, wait for them to approve the banner in WDC, then call the operation\'s tool with the ' +
        'returned intentToken. Until it is approved the daemon answers 425 pending_confirmation. ' +
        'An intentToken works once, for exactly this operation and target.\n\n' +
        'Args:\n  operation: One of ' + operations.join(', ') + '.\n' +
        '  target: What the operation acts on (database name, site domain, service id, zone id, ' +
        'binary app, plugin id, MySQL account as userName@host for mysql_user_drop, "config" or the ' +
        'tunnel id for cloudflare_credentials, site domain for node_process, service id for ' +
        'service_config_write, restart/shutdown/reset/uninstall for daemon_admin). ' +
        'Ignored for backup_restore, mysql_root_reset and hosts_write. When the daemon refuses a call ' +
        'with intent_rejected, its intentKind and intentDomain say what to request.\n' +
        '  reason: One sentence shown to the user.\n' +
        `  waitSeconds: Optionally wait up to ${MAX_WAIT_SECONDS}s for the user to decide before returning.\n\n` +
        'Returns: { intentId, intentToken, expiresAt, status, next } (status pending/ready/expired/revoked). ' +
        'If the approval banner could not be shown the intent is still returned, with a warning.',
      inputSchema: {
        operation: z.enum(operations),
        target: z.string().min(1).max(255),
        reason: z.string().min(1).max(500),
        ttlSeconds: z.number().int().min(60).max(3600).default(600),
        waitSeconds: WaitSecondsSchema,
      },
      annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false },
    },
    async ({ operation, target, reason, ttlSeconds, waitSeconds }) =>
      safe(() => {
        const { kind, domain, host } = intentFor(operation, target)
        return requestConfirmation({
          kind,
          domain,
          host,
          ttlSeconds,
          waitSeconds,
          prompt: `AI wants to ${CONFIRMABLE_OPERATIONS[operation].describe}${'fixedDomain' in CONFIRMABLE_OPERATIONS[operation] ? '' : ` ${target}`}: ${reason}`,
        })
      }),
  )
}
