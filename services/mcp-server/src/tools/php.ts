// PHP version + extension management.
import { z } from 'zod'
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js'

import { daemonClient } from '../daemonClient.js'
import type { RegisterOptions } from '../index.js'
import { safe } from '../formatting.js'
import { PhpVersionSchema } from '../schemas.js'

export function registerPhpTools(server: McpServer, opts: RegisterOptions): void {
  server.registerTool(
    'wdc_list_php_versions',
    {
      title: 'List PHP versions',
      description:
        'List all detected PHP installations with version, executable path, isDefault flag, ' +
        'extension count, and active site count.',
      inputSchema: {},
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async () => safe(() => daemonClient.get('/api/php/versions')),
  )

  if (opts.readonly) return

  // NOTE: There is intentionally no `wdc_set_default_php` tool. In WDC
  // every site carries its own `phpVersion` field — there is no global
  // "default PHP" concept in the daemon, and the corresponding daemon
  // endpoint does not exist. Use `wdc_create_site` / site update to
  // pin a specific PHP version per site instead.

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
