// Binary catalog + install/uninstall.
import { daemonClient } from '../daemonClient.js'

export const binariesTools = [
  {
    name: 'wdc_list_catalog',
    description:
      'List all known binary releases from the catalog API (Apache, PHP, MySQL, Caddy, mkcert, Mailpit, Redis, cloudflared, Node.js). Returns a flat array of BinaryRelease objects.',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
    handler: async () => daemonClient.get('/api/binaries/catalog'),
  },
  {
    name: 'wdc_list_installed_binaries',
    description:
      'List all currently installed binaries with app name, version, install path, and detected executable.',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
    handler: async () => daemonClient.get('/api/binaries/installed'),
  },
  {
    name: 'wdc_install_binary',
    description:
      'Download and extract a binary from the catalog. Synchronous — returns when extraction completes. Use the catalog list to find valid {app, version} pairs.',
    inputSchema: {
      type: 'object',
      required: ['app', 'version'],
      properties: {
        app: {
          type: 'string',
          description: 'App identifier like "apache", "php", "mysql", "node", "mkcert".',
        },
        version: {
          type: 'string',
          description: 'Full version string like "2.4.62" — must match a catalog entry.',
        },
      },
      additionalProperties: false,
    },
    handler: async (args: { app: string; version: string }) =>
      daemonClient.post('/api/binaries/install', args),
  },
  {
    name: 'wdc_uninstall_binary',
    description:
      'DESTRUCTIVE: Remove an installed binary version. Sites currently using it will fail to start until reconfigured. Requires confirm: "YES".',
    inputSchema: {
      type: 'object',
      required: ['app', 'version', 'confirm'],
      properties: {
        app: { type: 'string' },
        version: { type: 'string' },
        confirm: { type: 'string', enum: ['YES'] },
      },
      additionalProperties: false,
    },
    handler: async (args: { app: string; version: string; confirm: string }) => {
      if (args.confirm !== 'YES') {
        throw new Error('Refusing to uninstall binary without confirm: "YES"')
      }
      return daemonClient.delete(
        `/api/binaries/${encodeURIComponent(args.app)}/${encodeURIComponent(args.version)}`,
      )
    },
  },
  {
    name: 'wdc_refresh_catalog',
    description:
      'Force a refresh of the binary catalog cache from the configured catalog API URL. Returns the new release count and last-fetch timestamp.',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
    handler: async () => daemonClient.post('/api/binaries/catalog/refresh'),
  },
] as const
