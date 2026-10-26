// Service control tools — start/stop/list for Apache, MySQL, PHP, etc.
import { z } from 'zod'
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js'

import { daemonClient } from '../daemonClient.js'
import type { RegisterOptions } from '../index.js'
import { IntentTokenSchema, intentHeaders } from './confirmation.js'
import { safe } from '../formatting.js'
import { ConfirmYesSchema } from '../schemas.js'

// Mirror the PluginIdSchema shape — service ids live in a URL path segment,
// so accepting arbitrary characters risks traversal-style payloads reaching
// the daemon routing layer. Service ids are always lowercase alphanumerics
// with optional dots/hyphens/underscores (e.g. "apache", "php8.3", "nks.wdc.mysql").
const ServiceIdSchema = z
  .string()
  .min(1)
  .max(128)
  .regex(/^[a-zA-Z0-9._-]+$/, {
    message: 'Service id allows only [a-zA-Z0-9._-]',
  })
  // Reject "." / ".." — dot is in the class above so "." alone is accepted,
  // and "x..y" would pass too. Block traversal-shaped ids at the MCP edge.
  .refine((s) => s !== '.' && s !== '..' && !s.includes('..'), {
    message: 'Service id must not be "." / ".." or contain ".."',
  })
  .describe('Service identifier — typically the lowercase plugin name like "apache" or "mysql"')

export function registerServicesTools(server: McpServer, opts: RegisterOptions): void {
  server.registerTool(
    'wdc_list_services',
    {
      title: 'List services',
      description:
        'List all registered services (Apache, MySQL, PHP, Redis, Mailpit, etc.) ' +
        'with current state, PID, CPU%, memory, and uptime.\n\n' +
        'Returns: Array of ServiceStatus objects.',
      inputSchema: {},
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async () => safe(() => daemonClient.get('/api/services')),
  )

  server.registerTool(
    'wdc_get_service_logs',
    {
      title: 'Get service log lines',
      description:
        'Read the last lines of a service\'s log (Apache, MySQL, PHP-FPM, Redis, ...), oldest first.\n\n' +
        'Args:\n  serviceId: e.g. "apache", "mysql".\n  lines: How many lines (1-1000, default 100).\n\n' +
        'Returns: Array of log lines.',
      inputSchema: {
        serviceId: ServiceIdSchema,
        lines: z.number().int().min(1).max(1000).default(100),
      },
      annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false },
    },
    async ({ serviceId, lines }) =>
      safe(() => daemonClient.get(`/api/services/${encodeURIComponent(serviceId)}/logs?lines=${lines}`)),
  )

  server.registerTool(
    'wdc_get_service_config',
    {
      title: 'Read service config files',
      description:
        'Read the config files WDC manages for a service (httpd.conf and vhosts for Apache, php.ini for ' +
        'PHP, my.cnf for MySQL, ...).\n\n' +
        'Args:\n  serviceId: e.g. "apache", "php", "mysql".\n\n' +
        'Returns: { serviceId, files: [{ name, path, content }] }, or files: [] with managedExternally: true ' +
        'for services configured through their own API (Cloudflare, SSL, hosts).',
      inputSchema: { serviceId: ServiceIdSchema },
      annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false },
    },
    async ({ serviceId }) => safe(() => daemonClient.get(`/api/services/${encodeURIComponent(serviceId)}/config`)),
  )

  if (opts.readonly) return

  server.registerTool(
    'wdc_start_service',
    {
      title: 'Start service',
      description:
        'Start a service by id. Idempotent — already-running services return the existing ' +
        'status without restart.\n\nArgs:\n  serviceId: e.g. "apache", "mysql", "php".',
      inputSchema: {
        serviceId: ServiceIdSchema,
      },
      annotations: {
        readOnlyHint: false,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async ({ serviceId }) =>
      safe(() => daemonClient.post(`/api/services/${encodeURIComponent(serviceId)}/start`)),
  )

  server.registerTool(
    'wdc_stop_service',
    {
      title: 'Stop service',
      description:
        'Stop a service by id. The daemon performs a graceful shutdown with 15s timeout ' +
        'then SIGKILL fallback.\n\nArgs:\n  serviceId: Service to stop.',
      inputSchema: {
        serviceId: ServiceIdSchema,
        intentToken: IntentTokenSchema,
      },
      annotations: {
        readOnlyHint: false,
        destructiveHint: true,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async ({ serviceId, intentToken }) =>
      safe(() => daemonClient.post(`/api/services/${encodeURIComponent(serviceId)}/stop`, undefined, intentHeaders(intentToken))),
  )

  server.registerTool(
    'wdc_restart_service',
    {
      title: 'Restart service',
      description:
        'Stop and start a service by id (the service is briefly down).\n\n' +
        'Needs the user\'s approval: wdc_request_confirmation(operation: "service_restart", target: serviceId), ' +
        'then pass the approved intentToken.\n\n' +
        'Args:\n  serviceId: Service to restart.\n  intentToken: Approved intent.\n\n' +
        'Returns: the service status after the restart.',
      inputSchema: {
        serviceId: ServiceIdSchema,
        intentToken: IntentTokenSchema,
      },
      annotations: { readOnlyHint: false, destructiveHint: true, idempotentHint: false, openWorldHint: false },
    },
    async ({ serviceId, intentToken }) =>
      safe(() =>
        daemonClient.post(`/api/services/${encodeURIComponent(serviceId)}/restart`, undefined, intentHeaders(intentToken)),
      ),
  )

  server.registerTool(
    'wdc_validate_config',
    {
      title: 'Validate a service config file',
      description:
        'Check a service config file with the service\'s own validator (httpd -t, php -n -c, mysqld ' +
        '--validate-config, ...). Pass content to check a draft before writing it with ' +
        'wdc_update_service_config; the file on disk is left as it was.\n\n' +
        'Args:\n' +
        '  serviceId: apache, php, mysql or redis.\n' +
        '  configPath: Path of the file, as wdc_get_service_config lists it.\n' +
        '  content: Optional draft content to validate instead of the file on disk.\n\n' +
        'Returns: { isValid, output }.',
      inputSchema: {
        serviceId: ServiceIdSchema,
        configPath: z.string().min(1).max(4096),
        content: z.string().max(1_000_000).optional(),
      },
      annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: true, openWorldHint: false },
    },
    async ({ serviceId, configPath, content }) =>
      safe(() =>
        daemonClient.post('/api/config/validate', {
          serviceId,
          configPath,
          ...(content !== undefined ? { content } : {}),
        }),
      ),
  )

  server.registerTool(
    'wdc_update_service_config',
    {
      title: 'Overwrite a service config file (destructive)',
      description:
        'DESTRUCTIVE: Replace a service config file managed by WDC and restart the service if it is ' +
        'running. A broken config can stop the service from starting, so validate the content with ' +
        'wdc_validate_config first.\n\n' +
        'Needs the user\'s approval: wdc_request_confirmation(operation: "service_config_write", ' +
        'target: serviceId), then pass the approved intentToken.\n\n' +
        'Args:\n' +
        '  serviceId: e.g. "apache", "php", "mysql".\n' +
        '  path: Path of the file, as wdc_get_service_config lists it.\n' +
        '  content: The complete new file content.\n' +
        '  confirm: Must be "YES".\n' +
        '  intentToken: Approved intent.\n\n' +
        'You MUST show the user the file and the change before passing confirm="YES".\n\n' +
        'Returns: { saved, applied, restarted, message }.',
      inputSchema: {
        serviceId: ServiceIdSchema,
        path: z.string().min(1).max(4096),
        content: z.string().max(1_000_000),
        confirm: ConfirmYesSchema,
        intentToken: IntentTokenSchema,
      },
      annotations: { readOnlyHint: false, destructiveHint: true, idempotentHint: false, openWorldHint: false },
    },
    async ({ serviceId, path, content, intentToken }) =>
      safe(() =>
        daemonClient.post(
          `/api/services/${encodeURIComponent(serviceId)}/config`,
          { path, content },
          intentHeaders(intentToken),
        ),
      ),
  )
}
