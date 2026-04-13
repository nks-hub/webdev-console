// SSL / mkcert tools — wraps the daemon's /api/ssl endpoints which delegate
// to the SSL plugin's mkcert wrapper.

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

export function registerSslTools(server: McpServer, opts: RegisterOptions): void {
  server.registerTool(
    'wdc_list_certs',
    {
      title: 'List SSL certificates',
      description:
        'List all locally-issued site certificates with subject, issuer, ' +
        'validFrom/validTo, and the corresponding domain.',
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
    async ({ response_format }) => safe(() => daemonClient.get('/api/ssl/certs'), response_format),
  )

  if (opts.readonly) return

  server.registerTool(
    'wdc_install_ca',
    {
      title: 'Install mkcert CA',
      description:
        'Install the mkcert local Certificate Authority into the system trust store. ' +
        'Idempotent — safe to call repeatedly. Required once before any wdc_generate_cert ' +
        'call so browsers trust the resulting site certs.',
      inputSchema: {},
      annotations: {
        readOnlyHint: false,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async () => safe(() => daemonClient.post('/api/ssl/install-ca')),
  )

  server.registerTool(
    'wdc_generate_cert',
    {
      title: 'Generate SSL certificate',
      description:
        'Generate an mkcert-signed certificate for a domain and optional ServerAlias entries. ' +
        'The cert + private key are written under ~/.wdc/ssl/sites/{domain}/. ' +
        'The CA must be installed first via wdc_install_ca.\n\n' +
        'Args:\n' +
        '  domain: Primary domain.\n' +
        '  aliases: Additional Subject Alternative Names. Wildcard entries like "*.myapp.loc" are supported.',
      inputSchema: {
        domain: DomainSchema,
        aliases: z.array(z.string()).optional().describe('Additional Subject Alternative Names'),
      },
      annotations: {
        readOnlyHint: false,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async ({ domain, aliases }) =>
      safe(() => daemonClient.post('/api/ssl/generate', { domain, aliases })),
  )

  server.registerTool(
    'wdc_revoke_cert',
    {
      title: 'Revoke certificate (destructive)',
      description:
        'DESTRUCTIVE: Delete a site certificate. The vhost will fall back to HTTP-only ' +
        'on next reload.\n\n' +
        'Args:\n  domain: Domain to revoke.\n  confirm: Must be "YES".',
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
      safe(() => daemonClient.delete(`/api/ssl/certs/${encodeURIComponent(domain)}`)),
  )
}
