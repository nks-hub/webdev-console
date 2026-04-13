// Binary catalog + install/uninstall.
import { z } from 'zod'
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js'

import { daemonClient } from '../daemonClient.js'
import type { RegisterOptions } from '../index.js'
import { toolResponse, toolError, ToolTextResult } from '../formatting.js'
import { ConfirmYesSchema, ResponseFormat, ResponseFormatSchema } from '../schemas.js'

async function safe(fn: () => Promise<unknown>, format?: ResponseFormat): Promise<ToolTextResult> {
  try {
    return toolResponse(await fn(), format)
  } catch (err) {
    return toolError(err instanceof Error ? err.message : String(err))
  }
}

const AppNameSchema = z
  .string()
  .min(1)
  .describe('App identifier like "apache", "php", "mysql", "node", "mkcert"')

const VersionSchema = z
  .string()
  .min(1)
  .describe('Full version string like "2.4.62" — must match a catalog entry')

export function registerBinariesTools(server: McpServer, opts: RegisterOptions): void {
  server.registerTool(
    'wdc_list_catalog',
    {
      title: 'List binary catalog',
      description:
        'List all known binary releases from the catalog API (Apache, PHP, MySQL, Caddy, ' +
        'mkcert, Mailpit, Redis, cloudflared, Node.js).\n\n' +
        'Returns: Flat array of BinaryRelease objects.',
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
      safe(() => daemonClient.get('/api/binaries/catalog'), response_format),
  )

  server.registerTool(
    'wdc_list_installed_binaries',
    {
      title: 'List installed binaries',
      description:
        'List all currently installed binaries with app name, version, install path, ' +
        'and detected executable.',
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
    async ({ response_format }) =>
      safe(() => daemonClient.get('/api/binaries/installed'), response_format),
  )

  if (opts.readonly) return

  server.registerTool(
    'wdc_install_binary',
    {
      title: 'Install binary from catalog',
      description:
        'Download and extract a binary from the catalog. Synchronous — returns when ' +
        'extraction completes. Use wdc_list_catalog first to find valid {app, version} pairs.\n\n' +
        'Args:\n  app: App identifier.\n  version: Full version string.',
      inputSchema: {
        app: AppNameSchema,
        version: VersionSchema,
      },
      annotations: {
        readOnlyHint: false,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: true,
      },
    },
    async ({ app, version }) =>
      safe(() => daemonClient.post('/api/binaries/install', { app, version })),
  )

  server.registerTool(
    'wdc_uninstall_binary',
    {
      title: 'Uninstall binary (destructive)',
      description:
        'DESTRUCTIVE: Remove an installed binary version. Sites currently using it will ' +
        'fail to start until reconfigured.\n\n' +
        'Args:\n  app: App identifier.\n  version: Version to remove.\n  confirm: Must be "YES".',
      inputSchema: {
        app: AppNameSchema,
        version: VersionSchema,
        confirm: ConfirmYesSchema,
      },
      annotations: {
        readOnlyHint: false,
        destructiveHint: true,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async ({ app, version }) =>
      safe(() =>
        daemonClient.delete(
          `/api/binaries/${encodeURIComponent(app)}/${encodeURIComponent(version)}`,
        ),
      ),
  )

  server.registerTool(
    'wdc_refresh_catalog',
    {
      title: 'Refresh binary catalog',
      description:
        'Force a refresh of the binary catalog cache from the configured catalog API URL. ' +
        'Returns the new release count and last-fetch timestamp.',
      inputSchema: {},
      annotations: {
        readOnlyHint: false,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: true,
      },
    },
    async () => safe(() => daemonClient.post('/api/binaries/catalog/refresh')),
  )
}
