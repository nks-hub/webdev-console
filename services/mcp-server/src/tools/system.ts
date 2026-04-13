// System / status / activity tools.
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

export function registerSystemTools(server: McpServer, _opts: RegisterOptions): void {
  server.registerTool(
    'wdc_get_status',
    {
      title: 'Get daemon status',
      description:
        'Get daemon status: version, uptime in seconds, plugin count. ' +
        'Use this as a health check before calling other tools.',
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
    async ({ response_format }) => safe(() => daemonClient.get('/api/status'), response_format),
  )

  server.registerTool(
    'wdc_get_system_info',
    {
      title: 'Get system info',
      description:
        'Get full system snapshot: daemon info, services running/total, sites count, ' +
        'plugins count, binaries count, OS tag/arch, .NET runtime, catalog cache status.',
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
    async ({ response_format }) => safe(() => daemonClient.get('/api/system'), response_format),
  )

  server.registerTool(
    'wdc_get_recent_activity',
    {
      title: 'Get recent activity',
      description:
        'Get recent activity timeline (config edits, site creates/deletes, validation events). ' +
        'Useful when diagnosing "what changed recently?" questions.\n\n' +
        'Args:\n  limit: Number of rows to return (1-200, default 20).',
      inputSchema: {
        limit: z
          .number()
          .int()
          .min(1)
          .max(200)
          .default(20)
          .describe('Number of recent activity rows to return'),
        response_format: ResponseFormatSchema.optional(),
      },
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async ({ limit, response_format }) =>
      safe(() => daemonClient.get(`/api/activity?limit=${limit ?? 20}`), response_format),
  )
}
