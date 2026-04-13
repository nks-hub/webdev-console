// System / status / activity tools.
import { daemonClient } from '../daemonClient.js'

export const systemTools = [
  {
    name: 'wdc_get_status',
    description:
      'Get daemon status: version, uptime in seconds, plugin count. Used as a health check before calling other tools.',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
    handler: async () => daemonClient.get('/api/status'),
  },
  {
    name: 'wdc_get_system_info',
    description:
      'Get full system snapshot: daemon info, services running/total, sites count, plugins count, binaries count, OS tag/arch, .NET runtime, catalog cache status.',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
    handler: async () => daemonClient.get('/api/system'),
  },
  {
    name: 'wdc_get_recent_activity',
    description:
      'Get recent activity timeline (config edits, site creates/deletes, validation events). Useful when diagnosing "what changed recently?" questions.',
    inputSchema: {
      type: 'object',
      properties: {
        limit: {
          type: 'integer',
          minimum: 1,
          maximum: 200,
          default: 20,
          description: 'Number of recent activity rows to return (clamped 1-200 by the daemon).',
        },
      },
      additionalProperties: false,
    },
    handler: async (args: { limit?: number }) =>
      daemonClient.get(`/api/activity?limit=${args.limit ?? 20}`),
  },
] as const
