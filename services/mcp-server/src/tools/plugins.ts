// Plugin management — list, enable/disable, marketplace install.
import { daemonClient } from '../daemonClient.js'

export const pluginsTools = [
  {
    name: 'wdc_list_plugins',
    description:
      'List all loaded WDC plugins with id, name, version, enabled state, capabilities, supported platforms, and UI definition.',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
    handler: async () => daemonClient.get('/api/plugins'),
  },
  {
    name: 'wdc_enable_plugin',
    description:
      'Enable a previously-disabled plugin by id. The plugin\'s services start participating in the dashboard + Start All flow.',
    inputSchema: {
      type: 'object',
      required: ['id'],
      properties: {
        id: { type: 'string', description: 'Plugin id like "nks.wdc.apache".' },
      },
      additionalProperties: false,
    },
    handler: async (args: { id: string }) =>
      daemonClient.post(`/api/plugins/${encodeURIComponent(args.id)}/enable`),
  },
  {
    name: 'wdc_disable_plugin',
    description:
      'Disable a plugin so its services are hidden from the dashboard and skipped by Start All. Plugin DLL stays loaded — only the user-visible state changes.',
    inputSchema: {
      type: 'object',
      required: ['id'],
      properties: {
        id: { type: 'string' },
      },
      additionalProperties: false,
    },
    handler: async (args: { id: string }) =>
      daemonClient.post(`/api/plugins/${encodeURIComponent(args.id)}/disable`),
  },
  {
    name: 'wdc_get_plugin_marketplace',
    description:
      'Fetch the plugin marketplace catalog (third-party plugins available for install). Returns { source, reachable, count, plugins: [...] }.',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
    handler: async () => daemonClient.get('/api/plugins/marketplace'),
  },
  {
    name: 'wdc_install_plugin_from_marketplace',
    description:
      'Download and install a plugin from a marketplace entry. Requires id + downloadUrl from the marketplace catalog. The daemon will need a restart after install for the plugin to be active.',
    inputSchema: {
      type: 'object',
      required: ['id', 'downloadUrl'],
      properties: {
        id: { type: 'string', pattern: '^[a-zA-Z0-9._-]{1,128}$' },
        downloadUrl: {
          type: 'string',
          description: 'HTTPS URL (or http://localhost for dev). Daemon validates the scheme.',
        },
      },
      additionalProperties: false,
    },
    handler: async (args: { id: string; downloadUrl: string }) =>
      daemonClient.post('/api/plugins/install', args),
  },
] as const
