// Settings + sync tools.
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

export function registerSettingsTools(server: McpServer, opts: RegisterOptions): void {
  server.registerTool(
    'wdc_get_settings',
    {
      title: 'Get daemon settings',
      description:
        'Get all daemon settings as a flat key/value dictionary. ' +
        'Keys are "category.name" like "general.theme", "ports.http", "sync.deviceId".',
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
    async ({ response_format }) => safe(() => daemonClient.get('/api/settings'), response_format),
  )

  if (opts.readonly) return

  server.registerTool(
    'wdc_update_settings',
    {
      title: 'Update daemon settings',
      description:
        'Update one or more daemon settings atomically (single SQLite transaction). ' +
        'Pass a partial dict — keys not in the body are untouched.\n\n' +
        'Args:\n  settings: Map of "category.key" → string value.',
      inputSchema: {
        settings: z
          .record(z.string(), z.string())
          .describe('Map of "category.key" → string value'),
      },
      annotations: {
        readOnlyHint: false,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async ({ settings }) => safe(() => daemonClient.put('/api/settings', settings)),
  )
}
