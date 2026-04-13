// Cloudflare integration — config, token verify, zones, DNS records, tunnels.
import { daemonClient } from '../daemonClient.js'

export const cloudflareTools = [
  {
    name: 'wdc_get_cloudflare_config',
    description:
      'Get the current Cloudflare plugin config with sensitive fields (apiToken, tunnelToken) redacted to last 4 chars of the api token.',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
    handler: async () => daemonClient.get('/api/cloudflare/config'),
  },
  {
    name: 'wdc_save_cloudflare_config',
    description:
      'Update Cloudflare plugin settings. Pass any subset of fields — omitted fields keep current value. Returns the new redacted config.',
    inputSchema: {
      type: 'object',
      properties: {
        cloudflaredPath: { type: 'string' },
        tunnelToken: { type: 'string' },
        tunnelName: { type: 'string' },
        tunnelId: { type: 'string' },
        apiToken: { type: 'string' },
        accountId: { type: 'string' },
        defaultZoneId: { type: 'string' },
        subdomainTemplate: {
          type: 'string',
          description: 'Template like "{stem}-{hash}". Placeholders: {stem}, {hash}, {user}.',
        },
      },
      additionalProperties: false,
    },
    handler: async (args: Record<string, string>) =>
      daemonClient.put('/api/cloudflare/config', args),
  },
  {
    name: 'wdc_verify_cloudflare_token',
    description:
      'Verify the configured Cloudflare API token against /user/tokens/verify. Returns { success, errors[], result } from the Cloudflare API.',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
    handler: async () => daemonClient.get('/api/cloudflare/verify'),
  },
  {
    name: 'wdc_list_cloudflare_zones',
    description:
      'List all DNS zones the configured Cloudflare API token has access to. Returns { result: [{ id, name, status }] }.',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
    handler: async () => daemonClient.get('/api/cloudflare/zones'),
  },
  {
    name: 'wdc_list_cloudflare_dns',
    description:
      'List DNS records for a specific Cloudflare zone. Returns the raw Cloudflare API result with type, name, content, proxied, ttl per record.',
    inputSchema: {
      type: 'object',
      required: ['zoneId'],
      properties: {
        zoneId: { type: 'string', description: 'Zone ID from wdc_list_cloudflare_zones.' },
      },
      additionalProperties: false,
    },
    handler: async (args: { zoneId: string }) =>
      daemonClient.get(`/api/cloudflare/zones/${encodeURIComponent(args.zoneId)}/dns`),
  },
  {
    name: 'wdc_create_cloudflare_dns',
    description:
      'Create a DNS record in a Cloudflare zone. Defaults: type=CNAME, proxied=true, ttl=1 (auto).',
    inputSchema: {
      type: 'object',
      required: ['zoneId', 'name', 'content'],
      properties: {
        zoneId: { type: 'string' },
        type: {
          type: 'string',
          enum: ['A', 'AAAA', 'CNAME', 'TXT', 'MX'],
          default: 'CNAME',
        },
        name: { type: 'string', description: 'DNS record name (subdomain or @).' },
        content: { type: 'string', description: 'Target — IP, hostname, or text.' },
        proxied: { type: 'boolean', default: true },
        ttl: { type: 'integer', minimum: 1, default: 1 },
      },
      additionalProperties: false,
    },
    handler: async (args: any) =>
      daemonClient.post(`/api/cloudflare/zones/${encodeURIComponent(args.zoneId)}/dns`, args),
  },
  {
    name: 'wdc_delete_cloudflare_dns',
    description:
      'DESTRUCTIVE: Delete a DNS record from a Cloudflare zone. Requires confirm: "YES".',
    inputSchema: {
      type: 'object',
      required: ['zoneId', 'recordId', 'confirm'],
      properties: {
        zoneId: { type: 'string' },
        recordId: { type: 'string' },
        confirm: { type: 'string', enum: ['YES'] },
      },
      additionalProperties: false,
    },
    handler: async (args: { zoneId: string; recordId: string; confirm: string }) => {
      if (args.confirm !== 'YES') {
        throw new Error('Refusing to delete DNS record without confirm: "YES"')
      }
      return daemonClient.delete(
        `/api/cloudflare/zones/${encodeURIComponent(args.zoneId)}/dns/${encodeURIComponent(args.recordId)}`,
      )
    },
  },
  {
    name: 'wdc_list_cloudflare_tunnels',
    description:
      'List all Cloudflare tunnels visible to the configured API token. Used by the auto-setup flow to find or create the WDC managed tunnel.',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
    handler: async () => daemonClient.get('/api/cloudflare/tunnels'),
  },
  {
    name: 'wdc_get_cloudflare_subdomain_suggestion',
    description:
      'Given a local domain, compute the deterministic public subdomain via the configured template + install salt hash. Used by SiteEdit to pre-fill the public name when enabling a tunnel.',
    inputSchema: {
      type: 'object',
      required: ['domain'],
      properties: {
        domain: { type: 'string', description: 'Local domain like "myapp.loc".' },
      },
      additionalProperties: false,
    },
    handler: async (args: { domain: string }) =>
      daemonClient.get(`/api/cloudflare/suggest-subdomain?domain=${encodeURIComponent(args.domain)}`),
  },
] as const
