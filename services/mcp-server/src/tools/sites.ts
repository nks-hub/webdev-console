// Site management tools — Phase A MVP subset.
//
// Each tool definition wraps a daemon REST endpoint with a JSON Schema for
// the AI client to discover. Output is the raw daemon JSON serialized as
// MCP TextContent (MCP v0.2 doesn't support typed tool outputs yet).

import { z } from 'zod'
import { daemonClient } from '../daemonClient.js'

export const sitesTools = [
  {
    name: 'wdc_list_sites',
    description:
      'List all configured local development sites. Returns an array of SiteInfo objects with domain, documentRoot, phpVersion, sslEnabled, ports, framework, and Cloudflare config.',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
    handler: async () => daemonClient.get('/api/sites'),
  },
  {
    name: 'wdc_get_site',
    description:
      'Get details for a single site by domain name. Returns the full SiteInfo object or 404 if not found.',
    inputSchema: {
      type: 'object',
      required: ['domain'],
      properties: {
        domain: {
          type: 'string',
          description: 'Local domain like "myapp.loc". Must match the exact configured domain.',
        },
      },
      additionalProperties: false,
    },
    handler: async (args: { domain: string }) =>
      daemonClient.get(`/api/sites/${encodeURIComponent(args.domain)}`),
  },
  {
    name: 'wdc_create_site',
    description:
      'Create a new local development site. The daemon generates the Apache vhost, hosts file entry, optional SSL certificate, and writes the TOML config. Returns the created SiteInfo.',
    inputSchema: {
      type: 'object',
      required: ['domain', 'documentRoot'],
      properties: {
        domain: {
          type: 'string',
          pattern: '^[a-z0-9][a-z0-9.-]*\\.[a-z]{2,}$',
          description: 'Local domain like "myapp.loc". Must end in a TLD.',
        },
        documentRoot: {
          type: 'string',
          description: 'Absolute filesystem path to the site\'s web root.',
        },
        phpVersion: {
          type: 'string',
          description: 'PHP major.minor (e.g. "8.3"). Empty for static or Node sites.',
          default: '',
        },
        sslEnabled: { type: 'boolean', default: false },
        aliases: {
          type: 'array',
          items: { type: 'string' },
          description: 'ServerAlias entries — supports leading wildcard like "*.myapp.loc".',
        },
        framework: {
          type: 'string',
          description: 'Framework hint (wordpress, laravel, nette, symfony, nextjs, node, static). Empty = auto-detect.',
        },
      },
      additionalProperties: false,
    },
    handler: async (args: any) => daemonClient.post('/api/sites', args),
  },
  {
    name: 'wdc_delete_site',
    description:
      'DESTRUCTIVE: Delete a local development site. Removes the vhost config, hosts file entry, and TOML. Document root and databases are NOT touched. Requires explicit confirm: "YES" to prevent accidental deletion.',
    inputSchema: {
      type: 'object',
      required: ['domain', 'confirm'],
      properties: {
        domain: { type: 'string' },
        confirm: {
          type: 'string',
          enum: ['YES'],
          description: 'Must be the literal string "YES" to confirm deletion.',
        },
      },
      additionalProperties: false,
    },
    handler: async (args: { domain: string; confirm: string }) => {
      if (args.confirm !== 'YES') {
        throw new Error('Refusing to delete site without confirm: "YES"')
      }
      return daemonClient.delete(`/api/sites/${encodeURIComponent(args.domain)}`)
    },
  },
  {
    name: 'wdc_get_site_metrics',
    description:
      'Get live performance metrics for a site (request count, access log size, last request timestamp). Returns null when the site has no Apache access log yet.',
    inputSchema: {
      type: 'object',
      required: ['domain'],
      properties: {
        domain: { type: 'string' },
      },
      additionalProperties: false,
    },
    handler: async (args: { domain: string }) =>
      daemonClient.get(`/api/sites/${encodeURIComponent(args.domain)}/metrics`),
  },
] as const
