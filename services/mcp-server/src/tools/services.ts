// Service control tools — start/stop/list for Apache, MySQL, PHP, etc.
import { z } from 'zod'
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js'

import { daemonClient } from '../daemonClient.js'
import type { RegisterOptions } from '../index.js'
import { toolResponse, toolError, ToolTextResult } from '../formatting.js'
import { ResponseFormat, ResponseFormatSchema } from '../schemas.js'

async function safe(fn: () => Promise<unknown>, format?: ResponseFormat): Promise<ToolTextResult> {
  try {
    return toolResponse(await fn(), format)
  } catch (err) {
    return toolError(err instanceof Error ? err.message : String(err))
  }
}

const ServiceIdSchema = z
  .string()
  .min(1)
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
      inputSchema: {
        response_format: ResponseFormatSchema.optional(),
      },
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async ({ response_format }) => safe(() => daemonClient.get('/api/services'), response_format),
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
