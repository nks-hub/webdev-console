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

import { daemonClient } from '../daemonClient.js'
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

export function registerConfirmationTools(server: McpServer, opts: RegisterOptions): void {
  if (opts.readonly) return

  const operations = Object.keys(CONFIRMABLE_OPERATIONS) as [ConfirmableOperation, ...ConfirmableOperation[]]

  server.registerTool(
    'wdc_request_confirmation',
    {
      title: 'Ask the user to approve a destructive operation',
      description:
        'Destructive operations (dropping or changing databases, deleting sites, stopping services, ' +
        'revoking certificates, deleting DNS records, installing binaries or plugins, restoring backups) ' +
        'need the user\'s approval in the WDC app. Call this first, tell the user what you want to do and ' +
        'why, wait for them to approve the banner in WDC, then call the operation\'s tool with the ' +
        'returned intentToken. Until it is approved the daemon answers 425 pending_confirmation.\n\n' +
        'Args:\n  operation: One of ' + operations.join(', ') + '.\n' +
        '  target: What the operation acts on (database name, site domain, service id, zone id, ' +
        'binary app, plugin id). Ignored for backup_restore.\n' +
        '  reason: One sentence shown to the user.',
      inputSchema: {
        operation: z.enum(operations),
        target: z.string().min(1).max(255),
        reason: z.string().min(1).max(500),
        ttlSeconds: z.number().int().min(60).max(3600).default(600),
      },
      annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false },
    },
    async ({ operation, target, reason, ttlSeconds }) =>
      safe(async () => {
        const { kind, domain, host } = intentFor(operation, target)
        const created = await daemonClient.post<{ intentId: string; intentToken: string; expiresAt: string }>(
          '/api/mcp/intents',
          { domain, host, kind, expiresIn: ttlSeconds },
        )
        await daemonClient.post('/api/mcp/intents/confirm-request', {
          intentId: created.intentId,
          prompt: `AI wants to ${CONFIRMABLE_OPERATIONS[operation].describe} ${operation === 'backup_restore' ? '' : target}: ${reason}`.trim(),
        })
        return {
          ...created,
          next: 'Ask the user to approve the request in the WDC app, then call the operation again with this intentToken.',
        }
      }),
  )
}
