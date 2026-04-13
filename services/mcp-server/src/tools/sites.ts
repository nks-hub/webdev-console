// Site management tools — Phase A MVP subset.
// Each tool wraps a daemon REST endpoint. Tools are registered via
// `server.registerTool()` with Zod input schemas so the MCP SDK can
// auto-generate JSON Schema + validate arguments before calling the
// handler.

import { z } from 'zod'
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js'

import { daemonClient } from '../daemonClient.js'
import type { RegisterOptions } from '../index.js'
import { toolResponse, toolError, ToolTextResult } from '../formatting.js'
import {
  ConfirmYesSchema,
  DomainSchema,
  PhpVersionSchema,
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

export function registerSitesTools(server: McpServer, opts: RegisterOptions): void {
  server.registerTool(
    'wdc_list_sites',
    {
      title: 'List sites',
      description:
        'List all configured local development sites.\n\n' +
        'Returns: Array of SiteInfo objects with domain, documentRoot, phpVersion, ' +
        'sslEnabled, ports, framework, and Cloudflare config.\n\n' +
        'Example: Use first to discover what sites exist before calling wdc_get_site for details.',
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
    async ({ response_format }) => safe(() => daemonClient.get('/api/sites'), response_format),
  )

  server.registerTool(
    'wdc_get_site',
    {
      title: 'Get site details',
      description:
        'Get full details for a single site by its domain.\n\n' +
        'Args:\n  domain: Local domain like "myapp.loc" — must match an existing site.\n\n' +
        'Returns: SiteInfo object, or error if not found.',
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
      safe(() => daemonClient.get(`/api/sites/${encodeURIComponent(domain)}`), response_format),
  )

  server.registerTool(
    'wdc_get_site_metrics',
    {
      title: 'Get site metrics (live)',
      description:
        'Get live performance metrics for a site: request count, access log size, ' +
        'last request timestamp.\n\n' +
        'Args:\n  domain: Local site domain.\n\n' +
        'Returns: Metrics snapshot, or null when the site has no Apache access log yet.',
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
        () => daemonClient.get(`/api/sites/${encodeURIComponent(domain)}/metrics`),
        response_format,
      ),
  )

  if (opts.readonly) return

  server.registerTool(
    'wdc_create_site',
    {
      title: 'Create site',
      description:
        'Create a new local development site. The daemon generates the Apache vhost, ' +
        'hosts file entry, optional SSL certificate, and writes the TOML config.\n\n' +
        'Args:\n' +
        '  domain: Local domain like "myapp.loc" (must end in a TLD).\n' +
        '  documentRoot: Absolute filesystem path to the site web root.\n' +
        '  phpVersion: PHP major.minor like "8.3" (empty for static/Node).\n' +
        '  sslEnabled: Generate mkcert cert + HTTPS vhost.\n' +
        '  aliases: Additional ServerAlias entries (supports wildcard "*.myapp.loc").\n' +
        '  framework: Hint for vhost template (wordpress, laravel, nette, symfony, nextjs, node, static). Empty = auto-detect.\n\n' +
        'Returns: Created SiteInfo.',
      inputSchema: {
        domain: DomainSchema,
        documentRoot: z.string().min(1).describe('Absolute filesystem path to the site web root'),
        phpVersion: z
          .string()
          .default('')
          .describe('PHP major.minor (e.g. "8.3"). Empty for static or Node sites'),
        sslEnabled: z.boolean().default(false),
        aliases: z
          .array(z.string())
          .optional()
          .describe('ServerAlias entries — supports leading wildcard like "*.myapp.loc"'),
        framework: z
          .string()
          .default('')
          .describe(
            'Framework hint (wordpress, laravel, nette, symfony, nextjs, node, static). Empty = auto-detect.',
          ),
      },
      annotations: {
        readOnlyHint: false,
        destructiveHint: false,
        idempotentHint: false,
        openWorldHint: false,
      },
    },
    async (args) => safe(() => daemonClient.post('/api/sites', args)),
  )

  server.registerTool(
    'wdc_delete_site',
    {
      title: 'Delete site (destructive)',
      description:
        'DESTRUCTIVE: Delete a local development site. Removes the vhost config, ' +
        'hosts file entry, and TOML. Document root and databases are NOT touched.\n\n' +
        'Args:\n' +
        '  domain: Local domain to remove.\n' +
        '  confirm: Must be the literal string "YES".\n\n' +
        'Always confirm with the user before calling this tool.',
      inputSchema: {
        domain: DomainSchema,
        confirm: ConfirmYesSchema,
      },
      annotations: {
        readOnlyHint: false,
        destructiveHint: true,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async ({ domain }) =>
      safe(() => daemonClient.delete(`/api/sites/${encodeURIComponent(domain)}`)),
  )
}
