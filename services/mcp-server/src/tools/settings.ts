// Settings + sync tools.
import { z } from 'zod'
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js'

import { daemonClient } from '../daemonClient.js'
import type { RegisterOptions } from '../index.js'
import { safe, toolError } from '../formatting.js'

// The daemon stores settings as strings, but the MCP surface accepts
// string | number | boolean and coerces to string for convenience.
const SettingsValueSchema = z.union([z.string(), z.number(), z.boolean()])

// mcp.* settings decide what the MCP token may do (capabilities, always-
// confirm kinds, …): an agent must never change them, and the daemon refuses
// them for the MCP token too. Other operator settings (daemon.*, deploy.*,
// paths.*, …) are refused by the daemon unless the operator enabled the
// "Operator settings" capability in WDC → Settings → AI clients (MCP); its error carries
// a hint the agent should relay to the user.
const PROTECTED_SETTING_PREFIXES = ['mcp.']

export function protectedSettingKeys(keys: string[]): string[] {
  return keys.filter((k) => {
    const key = k.trim().toLowerCase()
    return PROTECTED_SETTING_PREFIXES.some((p) => key.startsWith(p))
  })
}

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
        'Pass a partial dict — keys not in the body are untouched. mcp.* keys are never ' +
        'writable by an AI client. deploy.*, daemon.*, plugins.*, sync.*, paths.*, telemetry.* ' +
        'and backup.dir need the operator to enable "Operator settings" in WDC → Settings → AI clients (MCP).\n\n' +
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
      const blocked = protectedSettingKeys(Object.keys(settings))
      if (blocked.length > 0) {
        return toolError(
          `mcp.* settings can only be changed by the operator in the WDC app: ${blocked.join(', ')}. ` +
            'Nothing was written.',
        )
      }
      const normalized: Record<string, string> = {}
      for (const [k, v] of Object.entries(settings)) {
        normalized[k] = String(v)
      }
      return safe(() => daemonClient.put('/api/settings', normalized))
    },
  )
}
