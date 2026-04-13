// Plugin management — list, enable/disable, marketplace install.
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

const PluginIdSchema = z
  .string()
  .min(1)
  .max(128)
  .regex(/^[a-zA-Z0-9._-]+$/, {
    message: 'Plugin id allows only [a-zA-Z0-9._-]',
  })
  .describe('Plugin id like "nks.wdc.apache"')

export function registerPluginsTools(server: McpServer, opts: RegisterOptions): void {
  server.registerTool(
    'wdc_list_plugins',
    {
      title: 'List plugins',
      description:
        'List all loaded WDC plugins with id, name, version, enabled state, ' +
        'capabilities, supported platforms, and UI definition.',
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
    async ({ response_format }) => safe(() => daemonClient.get('/api/plugins'), response_format),
  )

  server.registerTool(
    'wdc_get_plugin_marketplace',
    {
      title: 'Get plugin marketplace',
      description:
        'Fetch the plugin marketplace catalog (third-party plugins available for install).\n\n' +
        'Returns: { source, reachable, count, plugins: [...] }.',
      inputSchema: {
        response_format: ResponseFormatSchema.optional(),
      },
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: true,
      },
    },
    async ({ response_format }) =>
      safe(() => daemonClient.get('/api/plugins/marketplace'), response_format),
  )

  if (opts.readonly) return

  server.registerTool(
    'wdc_enable_plugin',
    {
      title: 'Enable plugin',
      description:
        'Enable a previously-disabled plugin by id. The plugin services start participating ' +
        'in the dashboard + Start All flow.\n\nArgs:\n  id: Plugin id.',
      inputSchema: {
        id: PluginIdSchema,
      },
      annotations: {
        readOnlyHint: false,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async ({ id }) => safe(() => daemonClient.post(`/api/plugins/${encodeURIComponent(id)}/enable`)),
  )

  server.registerTool(
    'wdc_disable_plugin',
    {
      title: 'Disable plugin',
      description:
        'Disable a plugin so its services are hidden from the dashboard and skipped by ' +
        'Start All. Plugin DLL stays loaded — only user-visible state changes.\n\n' +
        'Args:\n  id: Plugin id.',
      inputSchema: {
        id: PluginIdSchema,
      },
      annotations: {
        readOnlyHint: false,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async ({ id }) =>
      safe(() => daemonClient.post(`/api/plugins/${encodeURIComponent(id)}/disable`)),
  )

  server.registerTool(
    'wdc_install_plugin_from_marketplace',
    {
      title: 'Install plugin from marketplace',
      description:
        'Download and install a plugin from a marketplace entry. Requires id + downloadUrl ' +
        'from the marketplace catalog. The daemon will need a restart after install for the ' +
        'plugin to be active.\n\n' +
        'Args:\n  id: Plugin id.\n  downloadUrl: HTTPS URL (or http://localhost for dev).',
      inputSchema: {
        id: PluginIdSchema,
        downloadUrl: z
          .string()
          .url()
          .describe('HTTPS URL (or http://localhost for dev). Daemon validates the scheme'),
      },
      annotations: {
        readOnlyHint: false,
        destructiveHint: false,
        idempotentHint: false,
        openWorldHint: true,
      },
    },
    async ({ id, downloadUrl }) =>
      safe(() => daemonClient.post('/api/plugins/install', { id, downloadUrl })),
  )
}
