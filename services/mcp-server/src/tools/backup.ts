// Backup management — create, list, restore.
import { z } from 'zod'
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js'

import { daemonClient } from '../daemonClient.js'
import type { RegisterOptions } from '../index.js'
import { safe } from '../formatting.js'
import { ConfirmYesSchema } from '../schemas.js'

export function registerBackupTools(server: McpServer, opts: RegisterOptions): void {
  server.registerTool(
    'wdc_list_backups',
    {
      title: 'List backups',
      description:
        'List all existing backup zip files under ~/.wdc/backups/ with size and creation ' +
        'timestamp, newest-first.',
      inputSchema: {},
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async () => safe(() => daemonClient.get('/api/backup/list')),
  )

  if (opts.readonly) return

  server.registerTool(
    'wdc_create_backup',
    {
      title: 'Create backup',
      description:
        'Create a zip backup of the user WDC state (sites, ssl certs, caddy config, state.db). ' +
        'Excludes binaries (re-downloadable) and mysql data files (huge).\n\n' +
        'Returns: { path, fileCount, sizeBytes }.',
      inputSchema: {},
      annotations: {
        readOnlyHint: false,
        destructiveHint: false,
        idempotentHint: false,
        openWorldHint: false,
      },
    },
    async () => safe(() => daemonClient.post('/api/backup')),
  )

  server.registerTool(
    'wdc_restore_backup',
    {
      title: 'Restore backup (destructive)',
      description:
        'DESTRUCTIVE: Restore a backup zip file, overwriting current state. The daemon ' +
        'creates a safety pre-restore backup automatically.\n\n' +
        'Args:\n  path: Absolute path to a backup zip under the backup root.\n  confirm: Must be "YES".\n\n' +
        'You MUST show the user the backup path + timestamp before passing confirm="YES".',
      inputSchema: {
        path: z.string().min(1).describe('Absolute path to a backup zip'),
        confirm: ConfirmYesSchema,
      },
      annotations: {
        readOnlyHint: false,
        destructiveHint: true,
        idempotentHint: false,
        openWorldHint: false,
      },
    },
    async ({ path }) => safe(() => daemonClient.post('/api/restore', { path })),
  )
}
