// Cloudflare integration — config, token verify, zones, DNS records, tunnels.
import { z } from 'zod'
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js'

import { daemonClient } from '../daemonClient.js'
import type { RegisterOptions } from '../index.js'
import { safe } from '../formatting.js'
import { ConfirmYesSchema, DomainSchema } from '../schemas.js'

// Cloudflare zone + record ids are 32-char lowercase hex. Pinning the
// shape blocks traversal-style payloads and trims the surface to what
// the daemon actually forwards to the Cloudflare API. Mirrors the
// ServiceIdSchema / PluginIdSchema hardening from today's commits
// fda3f9c + 8332610.
const CloudflareIdSchema = z
  .string()
  .length(32)
  .regex(/^[a-f0-9]{32}$/, {
    message: 'Cloudflare id must be 32 lowercase hex characters',
  })
const ZoneIdSchema = CloudflareIdSchema.describe(
  'Cloudflare zone id from wdc_list_cloudflare_zones (32 lowercase hex chars)',
)
const RecordIdSchema = CloudflareIdSchema.describe(
  'Cloudflare DNS record id (32 lowercase hex chars)',
)

// Non-empty string — prevents accidentally wiping credentials by passing
// an empty string through Zod's default string schema.
const NonEmptyString = z.string().min(1)

export function registerCloudflareTools(server: McpServer, opts: RegisterOptions): void {
  server.registerTool(
    'wdc_get_cloudflare_config',
    {
      title: 'Get Cloudflare config',
      description:
        'Get the current Cloudflare plugin config with sensitive fields (apiToken, ' +
        'tunnelToken) redacted to last 4 chars.',
      inputSchema: {},
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async () => safe(() => daemonClient.get('/api/cloudflare/config')),
  )

  server.registerTool(
    'wdc_verify_cloudflare_token',
    {
      title: 'Verify Cloudflare API token',
      description:
        'Verify the configured Cloudflare API token against /user/tokens/verify.\n\n' +
        'Returns: { success, errors[], result } from the Cloudflare API.',
      inputSchema: {},
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: true,
      },
    },
    async () => safe(() => daemonClient.get('/api/cloudflare/verify')),
  )

  server.registerTool(
    'wdc_list_cloudflare_zones',
    {
      title: 'List Cloudflare zones',
      description:
        'List all DNS zones the configured Cloudflare API token has access to.\n\n' +
        'Returns: { result: [{ id, name, status }] }.',
      inputSchema: {},
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: true,
      },
    },
    async () => safe(() => daemonClient.get('/api/cloudflare/zones')),
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
      },
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: true,
      },
    },
    async ({ zoneId }) =>
      safe(() => daemonClient.get(`/api/cloudflare/zones/${encodeURIComponent(zoneId)}/dns`)),
  )

  server.registerTool(
    'wdc_list_cloudflare_tunnels',
    {
      title: 'List Cloudflare tunnels',
      description:
        'List all Cloudflare tunnels visible to the configured API token. Used by the ' +
        'auto-setup flow to find or create the WDC managed tunnel.',
      inputSchema: {},
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: true,
      },
    },
    async () => safe(() => daemonClient.get('/api/cloudflare/tunnels')),
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
      },
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async ({ domain }) => {
      const qs = new URLSearchParams({ domain })
      return safe(() => daemonClient.get(`/api/cloudflare/suggest-subdomain?${qs.toString()}`))
    },
  )

  if (opts.readonly) return

  server.registerTool(
    'wdc_save_cloudflare_config',
    {
      title: 'Save Cloudflare config',
      description:
        'Update Cloudflare plugin settings. Pass any subset of fields — omitted fields keep ' +
        'current value. Empty strings are rejected to prevent accidental credential wipes. ' +
        'Returns the new redacted config.',
      inputSchema: {
        cloudflaredPath: NonEmptyString.optional(),
        tunnelToken: NonEmptyString.optional(),
        tunnelName: NonEmptyString.optional(),
        tunnelId: NonEmptyString.optional(),
        apiToken: NonEmptyString.optional(),
        accountId: NonEmptyString.optional(),
        defaultZoneId: NonEmptyString.optional(),
        subdomainTemplate: NonEmptyString.optional().describe(
          'Template like "{stem}-{hash}". Placeholders: {stem}, {hash}, {user}',
        ),
      },
      annotations: {
        // This is an upsert on plugin config, not a destructive delete.
        // Empty-string credential wipes are blocked by NonEmptyString above,
        // so flip destructiveHint off — pairing it with no confirm gate
        // was confusing AI clients that branch on the annotation.
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
        '  zoneId: Zone id (used in the URL path, NOT in the body).\n' +
        '  name: DNS record name (subdomain or @).\n' +
        '  content: Target — IP, hostname, or text.\n' +
        '  type: Record type.\n' +
        '  proxied: Route through Cloudflare proxy.\n' +
        '  ttl: TTL in seconds (1 = auto).',
      inputSchema: {
        zoneId: ZoneIdSchema,
        // RFC 1035 caps DNS names at 253 chars (and labels at 63).
        // Content is record-type dependent so we keep it loose but
        // capped at 4 KB — enough for a long TXT/SPF record without
        // letting megabyte blobs through to Cloudflare's API.
        name: z.string().min(1).max(253).describe('DNS record name (subdomain or @, max 253 chars)'),
        content: z.string().min(1).max(4096).describe('Target — IP, hostname, or text (max 4 KB)'),
        type: z.enum(['A', 'AAAA', 'CNAME', 'TXT', 'MX']).default('CNAME'),
        proxied: z.boolean().default(true),
        // Cloudflare accepts 1 (auto) or 60…86400 seconds.
        ttl: z.number().int().min(1).max(86400).default(1),
      },
      annotations: {
        readOnlyHint: false,
        destructiveHint: false,
        idempotentHint: false,
        openWorldHint: true,
      },
    },
    async ({ zoneId, name, content, type, proxied, ttl }) =>
      safe(() =>
        daemonClient.post(`/api/cloudflare/zones/${encodeURIComponent(zoneId)}/dns`, {
          name,
          content,
          type,
          proxied,
          ttl,
        }),
      ),
  )

  server.registerTool(
    'wdc_delete_cloudflare_dns',
    {
      title: 'Delete DNS record (destructive)',
      description:
        'DESTRUCTIVE: Delete a DNS record from a Cloudflare zone.\n\n' +
        'Args:\n  zoneId: Zone id.\n  recordId: Record id.\n  confirm: Must be "YES".\n\n' +
        'You MUST show the user the full record (call wdc_list_cloudflare_dns first) ' +
        'before passing confirm="YES".',
      inputSchema: {
        zoneId: ZoneIdSchema,
        recordId: RecordIdSchema,
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
