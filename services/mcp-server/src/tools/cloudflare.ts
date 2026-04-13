// Cloudflare integration — config, token verify, zones, DNS records, tunnels.
import { z } from 'zod'
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js'

import { daemonClient } from '../daemonClient.js'
import type { RegisterOptions } from '../index.js'
import { toolResponse, toolError, ToolTextResult } from '../formatting.js'
import {
  ConfirmYesSchema,
  DomainSchema,
  ResponseFormat,
  ResponseFormatSchema,
} from '../schemas.js'

async function safe(fn: () => Promise<unknown>, format?: ResponseFormat): Promise<ToolTextResult> {
  try {
    return toolResponse(await fn(), format)
  } catch (err) {
    return toolError(err instanceof Error ? err.message : String(err))
  }
}

const ZoneIdSchema = z.string().min(1).describe('Cloudflare zone id from wdc_list_cloudflare_zones')

export function registerCloudflareTools(server: McpServer, opts: RegisterOptions): void {
  server.registerTool(
    'wdc_get_cloudflare_config',
    {
      title: 'Get Cloudflare config',
      description:
        'Get the current Cloudflare plugin config with sensitive fields (apiToken, ' +
        'tunnelToken) redacted to last 4 chars.',
      inputSchema: {
        response_format: ResponseFormatSchema.optional(),
      },
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async ({ response_format }) =>
      safe(() => daemonClient.get('/api/cloudflare/config'), response_format),
  )

  server.registerTool(
    'wdc_verify_cloudflare_token',
    {
      title: 'Verify Cloudflare API token',
      description:
        'Verify the configured Cloudflare API token against /user/tokens/verify.\n\n' +
        'Returns: { success, errors[], result } from the Cloudflare API.',
      inputSchema: {
        response_format: ResponseFormatSchema.optional(),
      },
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: true,
      },
    },
    async ({ response_format }) =>
      safe(() => daemonClient.get('/api/cloudflare/verify'), response_format),
  )

  server.registerTool(
    'wdc_list_cloudflare_zones',
    {
      title: 'List Cloudflare zones',
      description:
        'List all DNS zones the configured Cloudflare API token has access to.\n\n' +
        'Returns: { result: [{ id, name, status }] }.',
      inputSchema: {
        response_format: ResponseFormatSchema.optional(),
      },
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: true,
      },
    },
    async ({ response_format }) =>
      safe(() => daemonClient.get('/api/cloudflare/zones'), response_format),
  )

  server.registerTool(
    'wdc_list_cloudflare_dns',
    {
      title: 'List DNS records in zone',
      description:
        'List DNS records for a specific Cloudflare zone.\n\n' +
        'Args:\n  zoneId: Zone id.\n\n' +
        'Returns: Raw Cloudflare API result with type, name, content, proxied, ttl per record.',
      inputSchema: {
        zoneId: ZoneIdSchema,
        response_format: ResponseFormatSchema.optional(),
      },
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: true,
      },
    },
    async ({ zoneId, response_format }) =>
      safe(
        () => daemonClient.get(`/api/cloudflare/zones/${encodeURIComponent(zoneId)}/dns`),
        response_format,
      ),
  )

  server.registerTool(
    'wdc_list_cloudflare_tunnels',
    {
      title: 'List Cloudflare tunnels',
      description:
        'List all Cloudflare tunnels visible to the configured API token. Used by the ' +
        'auto-setup flow to find or create the WDC managed tunnel.',
      inputSchema: {
        response_format: ResponseFormatSchema.optional(),
      },
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: true,
      },
    },
    async ({ response_format }) =>
      safe(() => daemonClient.get('/api/cloudflare/tunnels'), response_format),
  )

  server.registerTool(
    'wdc_get_cloudflare_subdomain_suggestion',
    {
      title: 'Get Cloudflare subdomain suggestion',
      description:
        'Given a local domain, compute the deterministic public subdomain via the ' +
        'configured template + install salt hash. Used by SiteEdit to pre-fill the public ' +
        'name when enabling a tunnel.\n\n' +
        'Args:\n  domain: Local domain like "myapp.loc".',
      inputSchema: {
        domain: DomainSchema,
        response_format: ResponseFormatSchema.optional(),
      },
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async ({ domain, response_format }) =>
      safe(
        () =>
          daemonClient.get(
            `/api/cloudflare/suggest-subdomain?domain=${encodeURIComponent(domain)}`,
          ),
        response_format,
      ),
  )

  if (opts.readonly) return

  server.registerTool(
    'wdc_save_cloudflare_config',
    {
      title: 'Save Cloudflare config',
      description:
        'Update Cloudflare plugin settings. Pass any subset of fields — omitted fields keep ' +
        'current value. Returns the new redacted config.',
      inputSchema: {
        cloudflaredPath: z.string().optional(),
        tunnelToken: z.string().optional(),
        tunnelName: z.string().optional(),
        tunnelId: z.string().optional(),
        apiToken: z.string().optional(),
        accountId: z.string().optional(),
        defaultZoneId: z.string().optional(),
        subdomainTemplate: z
          .string()
          .optional()
          .describe('Template like "{stem}-{hash}". Placeholders: {stem}, {hash}, {user}'),
      },
      annotations: {
        readOnlyHint: false,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async (args) => safe(() => daemonClient.put('/api/cloudflare/config', args)),
  )

  server.registerTool(
    'wdc_create_cloudflare_dns',
    {
      title: 'Create DNS record',
      description:
        'Create a DNS record in a Cloudflare zone. Defaults: type=CNAME, proxied=true, ttl=1.\n\n' +
        'Args:\n' +
        '  zoneId: Zone id.\n' +
        '  name: DNS record name (subdomain or @).\n' +
        '  content: Target — IP, hostname, or text.\n' +
        '  type: Record type.\n' +
        '  proxied: Route through Cloudflare proxy.\n' +
        '  ttl: TTL in seconds (1 = auto).',
      inputSchema: {
        zoneId: ZoneIdSchema,
        name: z.string().min(1).describe('DNS record name (subdomain or @)'),
        content: z.string().min(1).describe('Target — IP, hostname, or text'),
        type: z.enum(['A', 'AAAA', 'CNAME', 'TXT', 'MX']).default('CNAME'),
        proxied: z.boolean().default(true),
        ttl: z.number().int().min(1).default(1),
      },
      annotations: {
        readOnlyHint: false,
        destructiveHint: false,
        idempotentHint: false,
        openWorldHint: true,
      },
    },
    async ({ zoneId, ...body }) =>
      safe(() =>
        daemonClient.post(`/api/cloudflare/zones/${encodeURIComponent(zoneId)}/dns`, {
          zoneId,
          ...body,
        }),
      ),
  )

  server.registerTool(
    'wdc_delete_cloudflare_dns',
    {
      title: 'Delete DNS record (destructive)',
      description:
        'DESTRUCTIVE: Delete a DNS record from a Cloudflare zone.\n\n' +
        'Args:\n  zoneId: Zone id.\n  recordId: Record id.\n  confirm: Must be "YES".',
      inputSchema: {
        zoneId: ZoneIdSchema,
        recordId: z.string().min(1).describe('DNS record id to delete'),
        confirm: ConfirmYesSchema,
      },
      annotations: {
        readOnlyHint: false,
        destructiveHint: true,
        idempotentHint: true,
        openWorldHint: true,
      },
    },
    async ({ zoneId, recordId }) =>
      safe(() =>
        daemonClient.delete(
          `/api/cloudflare/zones/${encodeURIComponent(zoneId)}/dns/${encodeURIComponent(recordId)}`,
        ),
      ),
  )
}
