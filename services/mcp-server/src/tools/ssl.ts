// SSL / mkcert tools — wraps the daemon's /api/ssl endpoints which delegate
// to the SSL plugin's mkcert wrapper.

import { daemonClient } from '../daemonClient.js'

export const sslTools = [
  {
    name: 'wdc_list_certs',
    description:
      'List all locally-issued site certificates with subject, issuer, validFrom/validTo, and the corresponding domain.',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
    handler: async () => daemonClient.get('/api/ssl/certs'),
  },
  {
    name: 'wdc_install_ca',
    description:
      'Install the mkcert local Certificate Authority into the system trust store. Idempotent — safe to call repeatedly. Required once before any wdc_generate_cert call so browsers trust the resulting site certs.',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
    handler: async () => daemonClient.post('/api/ssl/install-ca'),
  },
  {
    name: 'wdc_generate_cert',
    description:
      'Generate an mkcert-signed certificate for a domain and optional ServerAlias entries. The cert + private key are written under ~/.wdc/ssl/sites/{domain}/. The CA must be installed first via wdc_install_ca.',
    inputSchema: {
      type: 'object',
      required: ['domain'],
      properties: {
        domain: { type: 'string', pattern: '^[a-z0-9][a-z0-9.-]*\\.[a-z]{2,}$' },
        aliases: {
          type: 'array',
          items: { type: 'string' },
          description: 'Additional Subject Alternative Names. Wildcard entries like "*.myapp.loc" are supported.',
        },
      },
      additionalProperties: false,
    },
    handler: async (args: { domain: string; aliases?: string[] }) =>
      daemonClient.post('/api/ssl/generate', args),
  },
  {
    name: 'wdc_revoke_cert',
    description:
      'DESTRUCTIVE: Delete a site certificate. The vhost will fall back to HTTP-only on next reload. Requires confirm: "YES".',
    inputSchema: {
      type: 'object',
      required: ['domain', 'confirm'],
      properties: {
        domain: { type: 'string' },
        confirm: { type: 'string', enum: ['YES'] },
      },
      additionalProperties: false,
    },
    handler: async (args: { domain: string; confirm: string }) => {
      if (args.confirm !== 'YES') {
        throw new Error('Refusing to revoke certificate without confirm: "YES"')
      }
      return daemonClient.delete(`/api/ssl/certs/${encodeURIComponent(args.domain)}`)
    },
  },
] as const
