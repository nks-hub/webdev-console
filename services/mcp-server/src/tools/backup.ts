// Backup management — create, list, restore.
import { daemonClient } from '../daemonClient.js'

export const backupTools = [
  {
    name: 'wdc_create_backup',
    description:
      'Create a zip backup of the user\'s WDC state (sites, ssl certs, caddy config, state.db). Returns { path, fileCount, sizeBytes }. Excludes binaries (re-downloadable) and mysql data files (huge).',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
    handler: async () => daemonClient.post('/api/backup'),
  },
  {
    name: 'wdc_list_backups',
    description:
      'List all existing backup zip files under ~/.wdc/backups/ with size and creation timestamp, newest-first.',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
    handler: async () => daemonClient.get('/api/backup/list'),
  },
  {
    name: 'wdc_restore_backup',
    description:
      'DESTRUCTIVE: Restore a backup zip file, overwriting current state. Daemon creates a safety pre-restore backup automatically. Requires confirm: "YES".',
    inputSchema: {
      type: 'object',
      required: ['path', 'confirm'],
      properties: {
        path: {
          type: 'string',
          description: 'Absolute path to a backup zip under the backup root.',
        },
        confirm: { type: 'string', enum: ['YES'] },
      },
      additionalProperties: false,
    },
    handler: async (args: { path: string; confirm: string }) => {
      if (args.confirm !== 'YES') {
        throw new Error('Refusing to restore backup without confirm: "YES"')
      }
      return daemonClient.post('/api/restore', { path: args.path })
    },
  },
] as const
