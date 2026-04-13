// PHP version + extension management.
import { z } from 'zod'
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js'

import { daemonClient } from '../daemonClient.js'
import type { RegisterOptions } from '../index.js'
import { toolResponse, toolError, ToolTextResult } from '../formatting.js'
import { PhpVersionSchema, ResponseFormat, ResponseFormatSchema } from '../schemas.js'

async function safe(fn: () => Promise<unknown>, format?: ResponseFormat): Promise<ToolTextResult> {
  try {
    return toolResponse(await fn(), format)
  } catch (err) {
    return toolError(err instanceof Error ? err.message : String(err))
  }
}

export function registerPhpTools(server: McpServer, opts: RegisterOptions): void {
  server.registerTool(
    'wdc_list_php_versions',
    {
      title: 'List PHP versions',
      description:
        'List all detected PHP installations with version, executable path, isDefault flag, ' +
        'extension count, and active site count.',
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
      safe(() => daemonClient.get('/api/php/versions'), response_format),
  )

  if (opts.readonly) return

  server.registerTool(
    'wdc_set_default_php',
    {
      title: 'Set default PHP version',
      description:
        'Set the default PHP version used by sites that do not override it. ' +
        'Restarts the corresponding php-cgi pool.\n\n' +
        'Args:\n  version: PHP major.minor like "8.3" or full version like "8.3.10".',
      inputSchema: {
        version: z
          .string()
          .min(3)
          .describe('PHP major.minor like "8.3" or full version like "8.3.10"'),
      },
      annotations: {
        readOnlyHint: false,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async ({ version }) => safe(() => daemonClient.put('/api/php/default', { version })),
  )

  server.registerTool(
    'wdc_toggle_php_extension',
    {
      title: 'Enable/disable PHP extension',
      description:
        'Enable or disable a PHP extension for a specific version. The daemon restarts ' +
        'the corresponding php-cgi pool after the change. Use wdc_list_php_versions first ' +
        'to discover installed major.minor values.\n\n' +
        'Args:\n' +
        '  majorMinor: PHP version like "8.3".\n' +
        '  extensionName: Extension name like "redis", "imagick", "xdebug" (no .so/.dll suffix).\n' +
        '  enabled: true to enable, false to disable.',
      inputSchema: {
        majorMinor: PhpVersionSchema,
        extensionName: z
          .string()
          .min(1)
          .describe('Extension name without suffix (e.g. "redis")'),
        enabled: z.boolean(),
      },
      annotations: {
        readOnlyHint: false,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async ({ majorMinor, extensionName, enabled }) =>
      safe(() =>
        daemonClient.post(
          `/api/php/${encodeURIComponent(majorMinor)}/extensions/${encodeURIComponent(extensionName)}`,
          { enabled },
        ),
      ),
  )
}
