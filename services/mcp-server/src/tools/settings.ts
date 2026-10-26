// Settings + sync tools.
import { z } from 'zod'
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js'

import { daemonClient } from '../daemonClient.js'
import type { RegisterOptions } from '../index.js'
import { safe, toolError } from '../formatting.js'

// The daemon stores settings as strings, but the MCP surface accepts
// string | number | boolean and coerces to string for convenience.
const SettingsValueSchema = z.union([z.string(), z.number(), z.boolean()])

// Settings an AI client must not change on its own: they decide what the
// daemon trusts or where it reads and writes. Through them a prompt-injected
// agent could point daemon.catalogUrl at its own catalog and install a binary
// from it, clear mcp.always_confirm_kinds, or turn on the deploy surface.
// Operators change these in the GUI or with `wdc settings`.
const PROTECTED_SETTING_PREFIXES = ['mcp.', 'deploy.', 'daemon.', 'plugins.', 'sync.', 'paths.', 'telemetry.']
const PROTECTED_SETTING_KEYS = new Set(['backup.dir'])

export function protectedSettingKeys(keys: string[]): string[] {
  return keys.filter((k) => {
    const key = k.trim().toLowerCase()
    return PROTECTED_SETTING_KEYS.has(key) || PROTECTED_SETTING_PREFIXES.some((p) => key.startsWith(p))
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
        'Pass a partial dict — keys not in the body are untouched. Keys under ' +
        PROTECTED_SETTING_PREFIXES.map((p) => `${p}*`).join(', ') +
        ' and backup.dir are operator-only and are rejected.\n\n' +
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
          `These settings can only be changed by the operator in the WDC app: ${blocked.join(', ')}. ` +
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
