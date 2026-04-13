// Service control tools — Phase A MVP subset.
// Maps daemon REST endpoints under /api/services/* to MCP tools.

import { daemonClient } from '../daemonClient.js'

export const servicesTools = [
  {
    name: 'wdc_list_services',
    description:
      'List all registered services (Apache, MySQL, PHP, Redis, Mailpit, etc.) with current state, PID, CPU%, memory, and uptime.',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
    handler: async () => daemonClient.get('/api/services'),
  },
  {
    name: 'wdc_start_service',
    description:
      'Start a service by id (e.g. "apache", "mysql", "php"). Returns the new service status. Idempotent — already-running services return the existing status without restart.',
    inputSchema: {
      type: 'object',
      required: ['serviceId'],
      properties: {
        serviceId: {
          type: 'string',
          description: 'Service identifier — typically the lowercase plugin name like "apache".',
        },
      },
      additionalProperties: false,
    },
    handler: async (args: { serviceId: string }) =>
      daemonClient.post(`/api/services/${encodeURIComponent(args.serviceId)}/start`),
  },
  {
    name: 'wdc_stop_service',
    description:
      'Stop a service by id. The daemon performs a graceful shutdown with 15s timeout, then SIGKILL fallback. Returns the new status.',
    inputSchema: {
      type: 'object',
      required: ['serviceId'],
      properties: {
        serviceId: { type: 'string' },
      },
      additionalProperties: false,
    },
    handler: async (args: { serviceId: string }) =>
      daemonClient.post(`/api/services/${encodeURIComponent(args.serviceId)}/stop`),
  },
] as const
