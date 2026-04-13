// Settings + sync tools.
import { daemonClient } from '../daemonClient.js'

export const settingsTools = [
  {
    name: 'wdc_get_settings',
    description:
      'Get all daemon settings as a flat key/value dictionary. Keys are "category.name" like "general.theme", "ports.http", "sync.deviceId".',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
    handler: async () => daemonClient.get('/api/settings'),
  },
  {
    name: 'wdc_update_settings',
    description:
      'Update one or more daemon settings atomically (single SQLite transaction). Pass a partial dict — keys not in the body are untouched.',
    inputSchema: {
      type: 'object',
      required: ['settings'],
      properties: {
        settings: {
          type: 'object',
          description: 'Map of "category.key" → string value.',
          additionalProperties: { type: 'string' },
        },
      },
      additionalProperties: false,
    },
    handler: async (args: { settings: Record<string, string> }) =>
      daemonClient.put('/api/settings', args.settings),
  },
] as const
