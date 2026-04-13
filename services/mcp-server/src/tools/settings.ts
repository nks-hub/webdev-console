// Settings + sync tools.
import { z } from 'zod'
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js'

import { daemonClient } from '../daemonClient.js'
import type { RegisterOptions } from '../index.js'
import { safe } from '../formatting.js'

// The daemon stores settings as strings, but the MCP surface accepts
// string | number | boolean and coerces to string for convenience.
const SettingsValueSchema = z.union([z.string(), z.number(), z.boolean()])

export function registerSettingsTools(server: McpServer, opts: RegisterOptions): void {
  server.registerTool(
    'wdc_get_settings',
    {
      title: 'Get daemon settings',
      description:
        'Get all daemon settings as a flat key/value dictionary. ' +
        'Keys are "category.name" like "general.theme", "ports.http", "sync.deviceId".',
      inputSchema: {},
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async () => safe(() => daemonClient.get('/api/settings')),
  )

  if (opts.readonly) return

  server.registerTool(
    'wdc_update_settings',
    {
      title: 'Update daemon settings',
      description:
        'Update one or more daemon settings atomically (single SQLite transaction). ' +
        'Pass a partial dict — keys not in the body are untouched.\n\n' +
        'Args:\n  settings: Map of "category.key" → string/number/boolean.',
      inputSchema: {
        settings: z
          .record(z.string(), SettingsValueSchema)
          .describe('Map of "category.key" → string/number/boolean'),
      },
      annotations: {
        readOnlyHint: false,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async ({ settings }) => {
      const normalized: Record<string, string> = {}
      for (const [k, v] of Object.entries(settings)) {
        normalized[k] = String(v)
      }
      return safe(() => daemonClient.put('/api/settings', normalized))
    },
  )
}
