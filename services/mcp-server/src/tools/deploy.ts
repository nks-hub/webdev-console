/**
 * Deploy tools — wdc_deploy_* mapped to the daemon's `/api/nks.wdc.deploy/*`
 * routes (src/daemon/NKS.WebDevConsole.Daemon/Deploy/DeployEndpoints.cs).
 *
 * Tools register by scope (deploy:read / deploy:write / deploy:admin, from
 * the port file's `scope:` line). Every destructive deploy route is behind
 * the daemon's intent gate: with the MCP token it refuses the call unless it
 * carries an intent the user approved in the WDC app. The MCP server never
 * bypasses that gate and never sends X-Allow-Unconfirmed.
 *
 * Intent kinds and hosts the daemon checks per route:
 *   deploy          POST /sites/{d}/deploy                  kind deploy,   host = the host (token in body)
 *   group deploy    POST /sites/{d}/groups                  kind deploy,   host = hosts[0]
 *   rollback        POST /sites/{d}/deploys/{id}/rollback   kind rollback, host *
 *   group rollback  POST /sites/{d}/groups/{id}/rollback    kind rollback, host *
 *   cancel          DELETE /sites/{d}/deploys/{id}          kind cancel,   host *
 *   snapshot restore POST /sites/{d}/snapshots/{id}/restore kind restore,  host *restore*
 */
import { z } from 'zod'
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js'

import { daemonClient } from '../daemonClient.js'
import type { RegisterOptions } from '../index.js'
import { safe } from '../formatting.js'
import { DomainSchema } from '../schemas.js'
import { NEXT_AUTO_APPROVED } from './confirmation.js'

const PREFIX = '/api/nks.wdc.deploy'

const HostSchema = z
  .string()
  .min(1)
  .max(64)
  .regex(/^[a-z0-9_.-]+$/i, 'Host name uses only [A-Za-z0-9_.-]')
  .describe('Deploy host name from the site\'s deploy settings (e.g. "production", "staging"); see wdc_deploy_list_targets.')

const DeployIdSchema = z
  .string()
  .min(1)
  .max(64)
  .describe('UUID of a deploy run, returned by wdc_deploy_site or listed by wdc_deploy_list_releases.')

const GroupIdSchema = z
  .string()
  .min(1)
  .max(64)
  .describe('UUID of a deploy group, returned by wdc_deploy_group_start or listed by wdc_deploy_list_groups.')

const LimitSchema = z.number().int().min(1).max(200).default(50)
  .describe('Maximum number of entries to return (1-200, default 50).')

const IdempotencyKeySchema = z
  .string()
  .uuid()
  .optional()
  .describe('Caller-generated UUIDv4 sent with the deploy request, for correlating retries.')

function intentTokenSchema(howToMint: string) {
  return z
    .string()
    .min(10)
    .max(512)
    .describe(`intentToken of a request the user approved in the WDC app. ${howToMint}`)
}

/** Intent kinds wdc_deploy_create_intent can mint, with the host the daemon checks. */
export const DEPLOY_INTENT_OPERATIONS = {
  deploy: { kind: 'deploy', describe: 'deploy' },
  rollback: { kind: 'rollback', host: '*', describe: 'roll back the live release of' },
  cancel: { kind: 'cancel', host: '*', describe: 'cancel the running deploy of' },
  restore: { kind: 'restore', host: '*restore*', describe: 'restore a pre-deploy database snapshot over the live data of' },
} as const

export type DeployIntentOperation = keyof typeof DEPLOY_INTENT_OPERATIONS

/** The (kind, host) the daemon's intent gate checks for a deploy operation. */
export function deployIntentFor(operation: DeployIntentOperation, host: string | undefined): { kind: string; host: string } {
  const op = DEPLOY_INTENT_OPERATIONS[operation]
  if ('host' in op) return { kind: op.kind, host: op.host }
  if (!host) throw new Error('host is required to mint a deploy intent (for a group deploy, the first host of the group).')
  return { kind: op.kind, host }
}

const ANNOTATIONS_READ = { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false } as const

interface DeploySettingsHost {
  name?: string
  branch?: string
  sshHost?: string
  sshUser?: string
  sshPort?: number
  remotePath?: string
  localSourcePath?: string
  localTargetPath?: string
  healthCheckUrl?: string
  soakSeconds?: number
}

interface DeployGroupEntry {
  id: string
  [key: string]: unknown
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
          'List the deploy hosts configured for a site in its deploy settings.\n\n' +
          'Args:\n  domain: Local domain like "myapp.loc".\n\n' +
          'Returns: { domain, configured, count, hosts: [{ name, branch, sshHost, sshUser, sshPort, remotePath, ' +
          'localSourcePath, localTargetPath, healthCheckUrl, soakSeconds }] }. configured is false when no ' +
          'deploy settings were saved for the site yet (configure hosts in WDC → site → Deploy).\n\n' +
          'The deploy settings file also holds hooks, notification webhooks and env vars, so reading it needs ' +
          'the "Deploy configuration" capability enabled in WDC → Settings → AI clients (MCP).',
        inputSchema: { domain: DomainSchema },
        annotations: ANNOTATIONS_READ,
      },
      async ({ domain }) =>
        safe(async () => {
          let settings: { hosts?: DeploySettingsHost[] }
          try {
            settings = await daemonClient.get(`${PREFIX}/sites/${encodeURIComponent(domain)}/settings`)
          } catch (err) {
            if (err instanceof Error && /\b404\b/.test(err.message) && err.message.includes('no_settings_yet')) {
              return { domain, configured: false, count: 0, hosts: [] }
            }
            throw err
          }
          // Only the host fields; the settings file also holds hooks,
          // notification webhooks and env vars, which the agent does not need.
          const hosts = (settings.hosts ?? [])
            .filter(h => typeof h.name === 'string' && h.name)
            .map(h => ({
              name: h.name,
              branch: h.branch,
              sshHost: h.sshHost,
              sshUser: h.sshUser,
              sshPort: h.sshPort,
              remotePath: h.remotePath,
              localSourcePath: h.localSourcePath,
              localTargetPath: h.localTargetPath,
              healthCheckUrl: h.healthCheckUrl,
              soakSeconds: h.soakSeconds,
            }))
          return { domain, configured: true, count: hosts.length, hosts }
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
          'Returns: { deployId, domain, host, finalPhase, startedAt, completedAt, commitSha, releaseId, error, success }.',
        inputSchema: { domain: DomainSchema, deployId: DeployIdSchema },
        annotations: ANNOTATIONS_READ,
      },
      async ({ domain, deployId }) =>
        safe(() => daemonClient.get(`${PREFIX}/sites/${encodeURIComponent(domain)}/deploys/${encodeURIComponent(deployId)}`)),
    )

    server.registerTool(
      'wdc_deploy_list_releases',
      {
        title: 'List recent deploys for a site',
        description:
          'List the recent deploy runs of a site, newest first.\n\n' +
          'Args:\n  domain: Site domain.\n  limit: Max rows to return (default 50, max 200).\n\n' +
          'Returns: { domain, count, entries[] } with one entry per deploy run (deployId, host, branch, ' +
          'finalPhase, startedAt, completedAt, commitSha, releaseId, error, triggeredBy).',
        inputSchema: { domain: DomainSchema, limit: LimitSchema },
        annotations: ANNOTATIONS_READ,
      },
      async ({ domain, limit }) =>
        safe(() => daemonClient.get(`${PREFIX}/sites/${encodeURIComponent(domain)}/history?limit=${limit}`)),
    )

    server.registerTool(
      'wdc_deploy_get_release',
      {
        title: 'Get a single release entry',
        description:
          'Find one deploy run in the site\'s recent history (last 200 runs).\n\n' +
          'Args:\n  domain: Site domain.\n  deployId: UUID of the deploy run.\n\n' +
          'Returns: the matching history entry or { found: false }.',
        inputSchema: { domain: DomainSchema, deployId: DeployIdSchema },
        annotations: ANNOTATIONS_READ,
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
        title: 'Get the outcome of a deploy',
        description:
          'Get the final phase and error message of a deploy run. The daemon stores only the run\'s ' +
          'outcome, not a per-step log; live progress arrives as deploy:* events on /api/events.\n\n' +
          'Args:\n  domain: Site domain.\n  deployId: UUID.\n\n' +
          'Returns: { deployId, host, finalPhase, success, error, startedAt, completedAt }.',
        inputSchema: {
          domain: DomainSchema,
          deployId: DeployIdSchema,
        },
        annotations: ANNOTATIONS_READ,
      },
      async ({ domain, deployId }) =>
        safe(async () => {
          const status = await daemonClient.get(`${PREFIX}/sites/${encodeURIComponent(domain)}/deploys/${encodeURIComponent(deployId)}`) as {
            host?: string
            finalPhase: string
            success?: boolean
            error: string | null
            startedAt?: string
            completedAt?: string | null
          }
          return {
            deployId,
            host: status.host,
            finalPhase: status.finalPhase,
            success: status.success,
            error: status.error ?? null,
            startedAt: status.startedAt,
            completedAt: status.completedAt ?? null,
          }
        }),
    )

    server.registerTool(
      'wdc_deploy_list_snapshots',
      {
        title: 'List pre-deploy database snapshots',
        description:
          'List the database snapshots taken before deploys of a site. Each id is the deployId of the run ' +
          'that took it; pass it to wdc_deploy_restore_snapshot.\n\n' +
          'Args:\n  domain: Site domain.\n\n' +
          'Returns: { domain, count, entries: [{ id, createdAt, sizeBytes, path }] }.',
        inputSchema: { domain: DomainSchema },
        annotations: ANNOTATIONS_READ,
      },
      async ({ domain }) => safe(() => daemonClient.get(`${PREFIX}/sites/${encodeURIComponent(domain)}/snapshots`)),
    )

    server.registerTool(
      'wdc_deploy_list_groups',
      {
        title: 'List multi-host deploy groups',
        description:
          'List the multi-host deploy groups of a site, newest first.\n\n' +
          'Args:\n  domain: Site domain.\n  limit: Max rows (default 50, max 200).\n\n' +
          'Returns: { domain, count, entries: [{ id, hosts, hostDeployIds, phase, startedAt, completedAt, ' +
          'errorMessage, triggeredBy }] }.',
        inputSchema: { domain: DomainSchema, limit: LimitSchema },
        annotations: ANNOTATIONS_READ,
      },
      async ({ domain, limit }) =>
        safe(() => daemonClient.get(`${PREFIX}/sites/${encodeURIComponent(domain)}/groups?limit=${limit}`)),
    )

    server.registerTool(
      'wdc_deploy_group_status',
      {
        title: 'Get deploy group status',
        description:
          'Snapshot of one multi-host deploy group: phase, per-host deployIds, completion timestamp. ' +
          'Phase enum: initializing, preflight, deploying, awaiting_all_soak, all_succeeded, ' +
          'partial_failure, rolling_back_all, rolled_back, group_failed.\n\n' +
          'Args:\n  domain: Site domain.\n  groupId: UUID of the group.\n\n' +
          'Returns: the group entry, or an error when it is not among the site\'s last 200 groups.',
        inputSchema: {
          domain: DomainSchema,
          groupId: GroupIdSchema,
        },
        annotations: ANNOTATIONS_READ,
      },
      async ({ domain, groupId }) =>
        safe(async () => {
          // The daemon has no single-group GET; the list carries the same fields.
          const r = await daemonClient.get<{ entries?: DeployGroupEntry[] }>(
            `${PREFIX}/sites/${encodeURIComponent(domain)}/groups?limit=200`,
          )
          const group = r.entries?.find(g => g.id === groupId)
          if (!group) throw new Error(`Deploy group ${groupId} was not found among the last 200 groups of ${domain}.`)
          return group
        }),
    )
  }

  // ── WRITE (deploy:write) ──────────────────────────────────────────────

  if (canWrite && !opts.readonly) {
    const operations = Object.keys(DEPLOY_INTENT_OPERATIONS) as [DeployIntentOperation, ...DeployIntentOperation[]]
    server.registerTool(
      'wdc_deploy_create_intent',
      {
        title: 'Ask the user to approve a deploy operation',
        description:
          'Mint an intent for a deploy operation and ask the user to approve it in the WDC app. Once ' +
          'approved, pass the returned intentToken to the deploy tool. A token works once, for that operation ' +
          'on that site (and host, for deploys), and expires after ttlSeconds. It never skips the approval.\n\n' +
          'Args:\n' +
          '  domain: Site domain.\n' +
          '  operation: deploy (wdc_deploy_site, wdc_deploy_preflight; for wdc_deploy_group_start use the ' +
          'FIRST host of the group), rollback (wdc_deploy_rollback, wdc_deploy_group_rollback), cancel ' +
          '(wdc_deploy_cancel), restore (wdc_deploy_restore_snapshot). Default deploy.\n' +
          '  host: Target host; required for operation=deploy, ignored otherwise.\n' +
          '  ttlSeconds: TTL (60-3600, default 900).\n\n' +
          'Returns: { intentId, intentToken, expiresAt, kind, host, next } and a warning if the approval ' +
          'banner could not be shown. status ready with autoApproved: true means a trust grant the user created ' +
          'approved it at once: call the deploy tool now with the intentToken.',
        inputSchema: {
          domain: DomainSchema,
          operation: z.enum(operations).default('deploy'),
          host: HostSchema.optional(),
          ttlSeconds: z.number().int().min(60).max(3600).default(900),
        },
        annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false },
      },
      async ({ domain, operation, host, ttlSeconds }) =>
        // Intents live on the daemon (cross-plugin), not under the deploy
        // prefix. After creation a confirm-request makes the WDC app show
        // the Approve banner.
        safe(async () => {
          const intent = deployIntentFor(operation, host)
          const created = await daemonClient.post<{ intentId: string; intentToken: string; expiresAt: string }>(
            '/api/mcp/intents',
            { domain, host: intent.host, kind: intent.kind, expiresIn: ttlSeconds },
          )
          const what = operation === 'deploy'
            ? `deploy ${domain} → ${intent.host}`
            : `${DEPLOY_INTENT_OPERATIONS[operation].describe} ${domain}`
          const result = { ...created, kind: intent.kind, domain, host: intent.host }
          let banner: unknown
          try {
            banner = await daemonClient.post('/api/mcp/intents/confirm-request', {
              intentId: created.intentId,
              prompt: `AI requests to ${what}`,
            })
          } catch (err) {
            return {
              ...result,
              warning:
                'The intent was created but WDC could not be asked to show the approval banner: ' +
                (err instanceof Error ? err.message : String(err)) +
                `. Ask the user to approve intent ${created.intentId} in WDC → MCP → Intents.`,
            }
          }
          // A trust grant that covers the operation approves it on the spot.
          const answer = banner && typeof banner === 'object'
            ? (banner as { confirmed?: boolean; autoApproved?: boolean; matchedGrantId?: string | null })
            : undefined
          if (answer?.confirmed === true) {
            const autoApproved = answer.autoApproved === true
            return {
              ...result,
              status: 'ready',
              autoApproved,
              ...(answer.matchedGrantId ? { matchedGrantId: answer.matchedGrantId } : {}),
              next: autoApproved ? NEXT_AUTO_APPROVED : 'Approved. Call the deploy tool now with this intentToken.',
            }
          }
          return {
            ...result,
            next: 'Ask the user to approve the request in the WDC app, then call the deploy tool with this intentToken.',
          }
        }),
    )
  }

  // ── DESTRUCTIVE (deploy:admin) ────────────────────────────────────────

  if (canAdmin && !opts.readonly) {
    server.registerTool(
      'wdc_deploy_preflight',
      {
        title: 'Preview a deploy (dry run)',
        description:
          'Resolve what a deploy of {domain} to {host} would do without doing it: release id, source and ' +
          'target paths, current release, shared dirs/files, retention, and whether the source changed since ' +
          'the last successful deploy. Nothing is copied and no deploy run is recorded.\n\n' +
          'The daemon checks the deploy intent before the dry run and uses it up, so the real deploy needs ' +
          'a second approval.\n\n' +
          'Args:\n  domain: Site domain.\n  host: Target host.\n  branch: Optional branch.\n' +
          '  intentToken: Approved intent from wdc_deploy_create_intent(operation: deploy, host).',
        inputSchema: {
          domain: DomainSchema,
          host: HostSchema,
          branch: z.string().max(255).optional().describe('Optional git branch.'),
          intentToken: intentTokenSchema('Mint it with wdc_deploy_create_intent(operation: "deploy", host).'),
        },
        annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false },
      },
      async ({ domain, host, branch, intentToken }) =>
        safe(() =>
          daemonClient.post(
            `${PREFIX}/sites/${encodeURIComponent(domain)}/deploy`,
            { host, ...(branch ? { branch } : {}), dryRun: true, intentToken },
            { 'X-Intent-Token': intentToken },
          ),
        ),
    )

    server.registerTool(
      'wdc_deploy_site',
      {
        title: 'Deploy a site to a host (DESTRUCTIVE)',
        description:
          'Deploy {domain} to {host}. Returns 202 with { deployId, status } immediately; follow progress ' +
          'with wdc_deploy_get_status. The host must have local source/target paths configured in the ' +
          'site\'s deploy settings (see wdc_deploy_list_targets).\n\n' +
          'Needs the user\'s approval: call wdc_deploy_create_intent(operation: "deploy", host) first and ' +
          'pass the approved intentToken.\n\n' +
          'Args:\n' +
          '  domain: Site domain.\n' +
          '  host: Target host from the deploy settings.\n' +
          '  branch: Optional branch override.\n' +
          '  intentToken: Approved deploy intent for this host.\n' +
          '  idempotencyKey: Optional UUIDv4 for correlating retries.',
        inputSchema: {
          domain: DomainSchema,
          host: HostSchema,
          branch: z.string().max(255).optional().describe('Optional git branch to deploy.'),
          intentToken: intentTokenSchema('Mint it with wdc_deploy_create_intent(operation: "deploy", host).'),
          idempotencyKey: IdempotencyKeySchema,
        },
        annotations: { readOnlyHint: false, destructiveHint: true, idempotentHint: false, openWorldHint: true },
      },
      async ({ domain, host, branch, intentToken, idempotencyKey }) =>
        // This route reads the intent token from the body only.
        safe(() =>
          daemonClient.post(
            `${PREFIX}/sites/${encodeURIComponent(domain)}/deploy`,
            {
              host,
              ...(branch ? { branch } : {}),
              idempotencyKey: idempotencyKey ?? randomUuid(),
              intentToken,
            },
            { 'X-Intent-Token': intentToken },
          ),
        ),
    )

    server.registerTool(
      'wdc_deploy_rollback',
      {
        title: 'Roll back a deploy (DESTRUCTIVE)',
        description:
          'Roll the live release back to the release before the given deploy.\n\n' +
          'Needs the user\'s approval: wdc_deploy_create_intent(operation: "rollback") or ' +
          'wdc_request_confirmation(operation: "deploy_rollback", target: domain).\n\n' +
          'Args:\n  domain: Site domain.\n  deployId: UUID of the deploy run to rewind FROM.\n' +
          '  intentToken: Approved rollback intent.',
        inputSchema: {
          domain: DomainSchema,
          deployId: DeployIdSchema,
          intentToken: intentTokenSchema('Mint it with wdc_deploy_create_intent(operation: "rollback").'),
        },
        annotations: { readOnlyHint: false, destructiveHint: true, idempotentHint: false, openWorldHint: true },
      },
      async ({ domain, deployId, intentToken }) =>
        safe(() =>
          daemonClient.post(
            `${PREFIX}/sites/${encodeURIComponent(domain)}/deploys/${encodeURIComponent(deployId)}/rollback`,
            {},
            { 'X-Intent-Token': intentToken },
          ),
        ),
    )

    server.registerTool(
      'wdc_deploy_cancel',
      {
        title: 'Cancel an in-flight deploy (DESTRUCTIVE)',
        description:
          'Cancel a deploy that has not yet crossed its point of no return. The daemon answers 409 ' +
          'past_point_of_no_return once the release was switched — use wdc_deploy_rollback then.\n\n' +
          'Needs the user\'s approval: wdc_deploy_create_intent(operation: "cancel") or ' +
          'wdc_request_confirmation(operation: "deploy_cancel", target: domain).\n\n' +
          'Args:\n  domain: Site domain.\n  deployId: UUID.\n  intentToken: Approved cancel intent.',
        inputSchema: {
          domain: DomainSchema,
          deployId: DeployIdSchema,
          intentToken: intentTokenSchema('Mint it with wdc_deploy_create_intent(operation: "cancel").'),
        },
        annotations: { readOnlyHint: false, destructiveHint: true, idempotentHint: false, openWorldHint: false },
      },
      async ({ domain, deployId, intentToken }) =>
        safe(() =>
          daemonClient.delete(
            `${PREFIX}/sites/${encodeURIComponent(domain)}/deploys/${encodeURIComponent(deployId)}`,
            { 'X-Intent-Token': intentToken },
          ),
        ),
    )

    server.registerTool(
      'wdc_deploy_group_start',
      {
        title: 'Deploy a site to several hosts at once (DESTRUCTIVE)',
        description:
          'Start a deploy group: one site, 2-20 hosts deployed together (in parallel unless the site\'s ' +
          'deploy settings say otherwise). Hosts without local paths configured are recorded as no-ops.\n\n' +
          'Needs the user\'s approval: ONE deploy intent for the FIRST host in `hosts` authorises the whole ' +
          'group — wdc_deploy_create_intent(operation: "deploy", host: hosts[0]). Do not mint per-host intents.\n\n' +
          'Args:\n' +
          '  domain: Site domain.\n' +
          '  hosts: 2-20 host names from the deploy settings (the intent is checked against the first).\n' +
          '  intentToken: Approved deploy intent for hosts[0].\n\n' +
          'Returns: { groupId, hostCount, realCount, noopCount }.',
        inputSchema: {
          domain: DomainSchema,
          hosts: z
            .array(HostSchema)
            .min(2)
            .max(20)
            .refine(h => new Set(h).size === h.length, 'hosts must not repeat')
            .describe('Target host names (2-20, no repeats).'),
          intentToken: intentTokenSchema('Mint it with wdc_deploy_create_intent(operation: "deploy", host: hosts[0]).'),
        },
        annotations: { readOnlyHint: false, destructiveHint: true, idempotentHint: false, openWorldHint: true },
      },
      async ({ domain, hosts, intentToken }) =>
        safe(() =>
          daemonClient.post(
            `${PREFIX}/sites/${encodeURIComponent(domain)}/groups`,
            { hosts },
            { 'X-Intent-Token': intentToken },
          ),
        ),
    )

    server.registerTool(
      'wdc_deploy_group_rollback',
      {
        title: 'Roll back every host in a deploy group (DESTRUCTIVE)',
        description:
          'Roll back all hosts of a deploy group. Hosts that never switched release are no-ops.\n\n' +
          'Needs the user\'s approval: wdc_deploy_create_intent(operation: "rollback") or ' +
          'wdc_request_confirmation(operation: "deploy_rollback", target: domain).\n\n' +
          'Args:\n  domain: Site domain.\n  groupId: UUID of the group.\n  intentToken: Approved rollback intent.',
        inputSchema: {
          domain: DomainSchema,
          groupId: GroupIdSchema,
          intentToken: intentTokenSchema('Mint it with wdc_deploy_create_intent(operation: "rollback").'),
        },
        annotations: { readOnlyHint: false, destructiveHint: true, idempotentHint: false, openWorldHint: true },
      },
      async ({ domain, groupId, intentToken }) =>
        safe(() =>
          daemonClient.post(
            `${PREFIX}/sites/${encodeURIComponent(domain)}/groups/${encodeURIComponent(groupId)}/rollback`,
            {},
            { 'X-Intent-Token': intentToken },
          ),
        ),
    )

    server.registerTool(
      'wdc_deploy_restore_snapshot',
      {
        title: 'Restore a pre-deploy DB snapshot (DESTRUCTIVE — overwrites live data)',
        description:
          'Restore the database snapshot taken before a deploy over the live database. Restore is never ' +
          'triggered by a rollback (rollback rewinds code, restore overwrites data).\n\n' +
          'Needs the user\'s approval: wdc_deploy_create_intent(operation: "restore") or ' +
          'wdc_request_confirmation(operation: "deploy_restore", target: domain).\n\n' +
          'Args:\n  domain: Site domain.\n  deployId: Snapshot id (the deployId of the run that took it, ' +
          'see wdc_deploy_list_snapshots).\n  intentToken: Approved restore intent.',
        inputSchema: {
          domain: DomainSchema,
          deployId: DeployIdSchema,
          intentToken: intentTokenSchema('Mint it with wdc_deploy_create_intent(operation: "restore").'),
        },
        annotations: { readOnlyHint: false, destructiveHint: true, idempotentHint: false, openWorldHint: true },
      },
      async ({ domain, deployId, intentToken }) =>
        safe(() =>
          daemonClient.post(
            `${PREFIX}/sites/${encodeURIComponent(domain)}/snapshots/${encodeURIComponent(deployId)}/restore`,
            { confirm: true },
            { 'X-Intent-Token': intentToken },
          ),
        ),
    )
  }
}

/** Polyfill for crypto.randomUUID on older Node runtimes. */
function randomUuid(): string {
  const c = (globalThis as unknown as { crypto?: { randomUUID?: () => string } }).crypto
  if (c?.randomUUID) return c.randomUUID()
  // RFC4122 v4 fallback
  return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, (c2) => {
    const r = (Math.random() * 16) | 0
    const v = c2 === 'x' ? r : (r & 0x3) | 0x8
    return v.toString(16)
  })
}
