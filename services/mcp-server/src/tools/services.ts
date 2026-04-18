// Service control tools — start/stop/list for Apache, MySQL, PHP, etc.
import { z } from 'zod'
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js'

import { daemonClient } from '../daemonClient.js'
import type { RegisterOptions } from '../index.js'
import { safe } from '../formatting.js'

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
      },
      annotations: {
        readOnlyHint: false,
        destructiveHint: true,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async ({ serviceId }) =>
      safe(() => daemonClient.post(`/api/services/${encodeURIComponent(serviceId)}/stop`)),
  )
}
