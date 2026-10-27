// Site management tools.
import { z } from 'zod'
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js'

import { daemonClient } from '../daemonClient.js'
import type { RegisterOptions } from '../index.js'
import { IntentTokenSchema, intentHeaders } from './confirmation.js'
import { safe } from '../formatting.js'
import { ConfirmYesSchema, DomainSchema, capabilityNote } from '../schemas.js'

const AccessLogLimitSchema = z
  .number()
  .int()
  .min(1)
  .max(1000)
  .default(100)
  .describe('Max number of recent access log entries to return (1-1000)')

const PortSchema = z.number().int().min(1).max(65535)

const AliasesSchema = z
  .array(z.string().min(1).max(253))
  .max(100)
  .describe('ServerAlias entries — supports leading wildcard like "*.myapp.loc"')

/** Per-site php.ini overrides (SitePhpSettings in the daemon). */
const PhpSettingsSchema = z
  .object({
    memoryLimit: z.string().max(16).optional().describe('e.g. "512M"'),
    maxExecutionTime: z.number().int().min(0).max(86400).optional(),
    maxInputTime: z.number().int().min(-1).max(86400).optional(),
    postMaxSize: z.string().max(16).optional(),
    uploadMaxFilesize: z.string().max(16).optional(),
    xdebugEnabled: z.boolean().optional(),
    opcacheEnabled: z.boolean().optional(),
    displayErrors: z.boolean().optional(),
    extraExtensions: z.array(z.string().min(1).max(64)).max(50).optional(),
    disabledExtensions: z.array(z.string().min(1).max(64)).max(50).optional(),
    timezone: z.string().max(64).optional(),
  })
  .strict()
  .describe('Per-site php.ini overrides; omitted keys inherit the PHP version defaults')

// Site fields beyond domain, documentRoot, phpVersion, sslEnabled, aliases and
// framework are the daemon's site_advanced_fields capability; a
// nodeStartCommand also needs node_process and an approved intent.
const ADVANCED_FIELDS = capabilityNote('Advanced site fields')
const NODE_PROCESS = capabilityNote('Node.js start commands')

const HistoryTimestampSchema = z
  .string()
  .min(1)
  .max(64)
  .regex(/^[A-Za-z0-9_.:-]+$/, 'Use a timestamp exactly as wdc_site_history lists it')
  .describe('History entry timestamp, exactly as returned by wdc_site_history')

type StepStatus = 'ok' | 'skipped' | 'failed' | 'unknown'
const NUMERIC_STEP_STATUS: StepStatus[] = ['ok', 'skipped', 'failed']

/**
 * Apply-step status as a word. The daemon sends "ok" / "skipped" / "failed";
 * builds that serialised the status enum send its index (0, 1, 2) instead.
 */
export function stepStatus(status: unknown): StepStatus {
  if (typeof status === 'number') return NUMERIC_STEP_STATUS[status] ?? 'unknown'
  if (typeof status === 'string') {
    const s = status.toLowerCase()
    if (s === 'ok' || s === 'succeeded' || s === 'success') return 'ok'
    if (s === 'skipped' || s === 'failed') return s
    if (/^\d+$/.test(s)) return NUMERIC_STEP_STATUS[Number(s)] ?? 'unknown'
  }
  return 'unknown'
}

interface ApplyStep {
  name?: string
  status?: unknown
  detail?: string | null
}

/** Normalises an apply result and counts its steps by status. */
export function summariseApply<T extends { steps?: ApplyStep[] }>(result: T) {
  const steps = (result.steps ?? []).map(st => ({ name: st.name, status: stepStatus(st.status), detail: st.detail ?? null }))
  const failed = steps.filter(st => st.status === 'failed')
  return {
    ...result,
    steps,
    summary: {
      ok: steps.filter(st => st.status === 'ok').length,
      skipped: steps.filter(st => st.status === 'skipped').length,
      failed: failed.length,
    },
    ...(failed.length > 0 ? { failedSteps: failed.map(st => `${st.name}: ${st.detail ?? 'failed'}`) } : {}),
  }
}

/** Fields wdc_update_site may change; everything else is kept from the current config. */
const UPDATABLE_SITE_FIELDS = [
  'documentRoot',
  'phpVersion',
  'aliases',
  'sslEnabled',
  'httpPort',
  'httpsPort',
  'framework',
  'phpSettings',
] as const

/**
 * The daemon's PUT replaces the whole site config, so a partial update must
 * start from the current one. Returns the body to PUT.
 */
export function mergeSiteUpdate(current: Record<string, unknown>, changes: Record<string, unknown>): Record<string, unknown> {
  const next: Record<string, unknown> = { ...current }
  for (const key of UPDATABLE_SITE_FIELDS) {
    if (changes[key] !== undefined) next[key] = changes[key]
  }
  return next
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
        'Use first to discover what sites exist before calling wdc_get_site for details.',
      inputSchema: {},
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async () => safe(() => daemonClient.get('/api/sites')),
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
      },
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async ({ domain }) =>
      safe(() => daemonClient.get(`/api/sites/${encodeURIComponent(domain)}`)),
  )

  server.registerTool(
    'wdc_get_site_access_log',
    {
      title: 'Get recent site access log entries',
      description:
        'Tail the last N Apache access log entries for a site. Returns parsed ' +
        'entries with timestamp, client IP, HTTP method + path, status code, ' +
        'response size, referer, and user agent. Also returns the top 10 ' +
        'client IPs by hit count within the window.\n\n' +
        'Args:\n' +
        '  domain: Local site domain.\n' +
        '  limit: Number of most-recent entries to return (1-1000, default 100).\n\n' +
        'Returns: { domain, count, topClients, entries } sorted oldest → newest. If the result ' +
        'is too large, the oldest entries are dropped and truncation says how many.\n\n' +
        'Use this for diagnosing traffic spikes, spotting scrapers, verifying ' +
        'that a deployment is actually serving requests, or auditing who just ' +
        'visited a specific site.',
      inputSchema: {
        domain: DomainSchema,
        limit: AccessLogLimitSchema.optional(),
      },
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async ({ domain, limit }) => {
      const qs = new URLSearchParams()
      if (limit !== undefined) qs.set('limit', String(limit))
      // Entries run oldest → newest; an oversized result keeps the newest.
      return safe(() => daemonClient.get(`/api/sites/${encodeURIComponent(domain)}/access-log?${qs.toString()}`), {
        keep: 'tail',
      })
    },
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
      },
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async ({ domain }) =>
      safe(() => daemonClient.get(`/api/sites/${encodeURIComponent(domain)}/metrics`)),
  )

  server.registerTool(
    'wdc_get_site_error_log',
    {
      title: 'Get recent site error log entries',
      description:
        'Read the newest Apache and PHP error log entries that concern a site (its vhost error logs, ' +
        'the global Apache error log, PHP-FPM and PHP error logs).\n\n' +
        'Args:\n' +
        '  domain: Local site domain.\n' +
        '  lines: Max entries to return (1-1000, default 100).\n' +
        '  since: Optional ISO timestamp; only entries after it.\n\n' +
        'Returns: Array of { timestamp, severity, source (apache-error | php-fpm-error | php-error), ' +
        'message, pid, client }, newest first. Missing log files give an empty list.',
      inputSchema: {
        domain: DomainSchema,
        lines: z.number().int().min(1).max(1000).default(100),
        since: z.string().datetime({ offset: true }).optional().describe('ISO 8601 timestamp'),
      },
      annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false },
    },
    async ({ domain, lines, since }) => {
      const qs = new URLSearchParams({ lines: String(lines) })
      if (since) qs.set('since', since)
      return safe(() => daemonClient.get(`/api/sites/${encodeURIComponent(domain)}/logs/errors?${qs.toString()}`))
    },
  )

  server.registerTool(
    'wdc_site_history',
    {
      title: 'List config history of a site',
      description:
        'List the archived versions of a site\'s config (one per save), newest first. Pass a timestamp ' +
        'to wdc_site_rollback to go back to that version.\n\n' +
        'Args:\n  domain: Local site domain.\n\n' +
        'Returns: Array of { timestamp, size, createdAt }.',
      inputSchema: { domain: DomainSchema },
      annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false },
    },
    async ({ domain }) => safe(() => daemonClient.get(`/api/sites/${encodeURIComponent(domain)}/history`)),
  )

  if (opts.readonly) return

  server.registerTool(
    'wdc_create_site',
    {
      title: 'Create site',
      description:
        'Create a new local development site. The daemon generates the Apache vhost, ' +
        'hosts file entry, optional SSL certificate, and writes the site config.\n\n' +
        'Args:\n' +
        '  domain: Local domain like "myapp.loc" (must end in a TLD).\n' +
        '  documentRoot: Absolute filesystem path to the site web root.\n' +
        '  phpVersion: PHP major.minor like "8.3" (empty for static/Node).\n' +
        '  sslEnabled: Generate mkcert cert + HTTPS vhost.\n' +
        '  aliases: Additional ServerAlias entries (supports wildcard "*.myapp.loc").\n' +
        '  framework: Hint for vhost template (wordpress, laravel, nette, symfony, nextjs, node, static). Empty = auto-detect.\n' +
        '  httpPort / httpsPort: Listener ports (default 80 / 443).\n' +
        '  nodeUpstreamPort: Port of the Node.js app the vhost proxies to (Node sites; 0 = not a Node site).\n' +
        '  nodeStartCommand: Command WDC runs to start the Node.js app (e.g. "npm run start").\n' +
        '  environment: Environment variables for the site (Node process / PHP env).\n' +
        '  phpSettings: Per-site php.ini overrides (memoryLimit, maxExecutionTime, xdebugEnabled, ...).\n' +
        '  intentToken: Approved intent, only with a nodeStartCommand.\n\n' +
        `httpPort, httpsPort, nodeUpstreamPort, environment and phpSettings: ${ADVANCED_FIELDS} ` +
        `nodeStartCommand (a shell command the daemon runs): ${NODE_PROCESS} It also needs the user's ` +
        'approval: wdc_request_confirmation(operation: "node_process", target: domain). Without them, ' +
        'create the site with the basic fields only.\n\n' +
        'Returns: the created SiteInfo, or { site: SiteInfo, warnings: string[], hints: string[] } when ' +
        'some apply step failed (warnings, e.g. "ssl: mkcert missing") or WDC has a suggestion (hints, ' +
        'e.g. run composer install). Report warnings to the user; the site exists either way.',
      inputSchema: {
        domain: DomainSchema,
        documentRoot: z.string().min(1).describe('Absolute filesystem path to the site web root'),
        phpVersion: z
          .string()
          .default('')
          .describe('PHP major.minor (e.g. "8.3"). Empty for static or Node sites'),
        sslEnabled: z.boolean().default(true),
        aliases: AliasesSchema.optional(),
        framework: z
          .string()
          .default('')
          .describe(
            'Framework hint (wordpress, laravel, nette, symfony, nextjs, node, static). Empty = auto-detect.',
          ),
        httpPort: PortSchema.optional().describe('HTTP listener port (default 80)'),
        httpsPort: PortSchema.optional().describe('HTTPS listener port (default 443)'),
        nodeUpstreamPort: z
          .number()
          .int()
          .min(0)
          .max(65535)
          .optional()
          .describe('Port of the Node.js app the vhost proxies to; 0 or omitted = not a Node site'),
        nodeStartCommand: z
          .string()
          .max(1000)
          .optional()
          .describe('Command WDC runs to start the Node.js app, e.g. "npm run start"'),
        environment: z
          .record(z.string().regex(/^[A-Za-z_][A-Za-z0-9_]*$/, 'Environment variable names use [A-Za-z0-9_]'), z.string().max(4096))
          .optional()
          .describe('Environment variables for the site'),
        phpSettings: PhpSettingsSchema.optional(),
        intentToken: IntentTokenSchema,
      },
      annotations: {
        readOnlyHint: false,
        destructiveHint: false,
        idempotentHint: false,
        openWorldHint: false,
      },
    },
    async ({ intentToken, ...site }) => safe(() => daemonClient.post('/api/sites', site, intentHeaders(intentToken))),
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
        'You MUST show the user exactly which site will be removed and get explicit ' +
        'verbal confirmation before passing confirm="YES".',
      inputSchema: {
        domain: DomainSchema,
        confirm: ConfirmYesSchema,
        intentToken: IntentTokenSchema,
      },
      annotations: {
        readOnlyHint: false,
        destructiveHint: true,
        // A second call needs a new approval and then finds no site.
        idempotentHint: false,
        openWorldHint: false,
      },
    },
    async ({ domain, intentToken }) =>
      safe(() => daemonClient.delete(`/api/sites/${encodeURIComponent(domain)}`, intentHeaders(intentToken))),
  )

  server.registerTool(
    'wdc_update_site',
    {
      title: 'Update site settings',
      description:
        'Change settings of an existing site and re-apply it (vhost, certificate, hosts entry, reload). ' +
        'Only the fields you pass change; the rest of the config is kept. The previous config is archived ' +
        '(see wdc_site_history / wdc_site_rollback).\n\n' +
        'Args:\n' +
        '  domain: Site to change.\n' +
        '  documentRoot, phpVersion, aliases (replaces the list), sslEnabled, httpPort, httpsPort, framework, ' +
        'phpSettings (replaces the overrides): new values.\n\n' +
        `Changing httpPort, httpsPort or phpSettings: ${ADVANCED_FIELDS}\n\n` +
        'Returns: the updated SiteInfo, or { site, warnings } when an apply step failed.',
      inputSchema: {
        domain: DomainSchema,
        documentRoot: z.string().min(1).optional().describe('Absolute path of the new web root'),
        phpVersion: z.string().max(16).optional().describe('PHP major.minor like "8.3"; empty for static/Node'),
        aliases: AliasesSchema.optional(),
        sslEnabled: z.boolean().optional(),
        httpPort: PortSchema.optional(),
        httpsPort: PortSchema.optional(),
        framework: z.string().max(64).optional(),
        phpSettings: PhpSettingsSchema.optional(),
      },
      annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: true, openWorldHint: false },
    },
    async ({ domain, ...changes }) =>
      safe(async () => {
        if (Object.values(changes).every(v => v === undefined)) {
          throw new Error('Nothing to change: pass at least one field to update.')
        }
        const current = await daemonClient.get<Record<string, unknown>>(`/api/sites/${encodeURIComponent(domain)}`)
        return daemonClient.put(`/api/sites/${encodeURIComponent(domain)}`, mergeSiteUpdate(current, changes))
      }),
  )

  server.registerTool(
    'wdc_apply_site',
    {
      title: 'Re-apply a site',
      description:
        'Re-run the apply pipeline for one site (certificate, vhost, PHP, hosts entry, web server reload) ' +
        'without changing its config, e.g. after fixing what made a step fail.\n\n' +
        'Args:\n  domain: Site to apply.\n\n' +
        'Returns: { domain, success, steps: [{ name, status: ok | skipped | failed, detail }], ' +
        'summary: { ok, skipped, failed }, failedSteps? }.',
      inputSchema: { domain: DomainSchema },
      annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: true, openWorldHint: false },
    },
    async ({ domain }) =>
      safe(async () =>
        summariseApply(await daemonClient.post<{ steps?: ApplyStep[] }>(`/api/sites/${encodeURIComponent(domain)}/apply`)),
      ),
  )

  server.registerTool(
    'wdc_reapply_all_sites',
    {
      title: 'Re-apply every site',
      description:
        'Re-run the apply pipeline for every site, e.g. after changing ports or installing a new Apache ' +
        'or PHP. Can take a while with many sites.\n\n' +
        'Returns: Array of { domain, ok, steps: [{ name, status, detail }], summary, error? }.',
      inputSchema: {},
      annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: true, openWorldHint: false },
    },
    async () =>
      safe(async () => {
        const results = await daemonClient.post<Array<{ steps?: ApplyStep[] }>>('/api/sites/reapply-all')
        return (results ?? []).map(r => summariseApply(r))
      }),
  )

  server.registerTool(
    'wdc_detect_framework',
    {
      title: 'Detect the framework of a site',
      description:
        'Inspect the site\'s document root to detect its framework (WordPress, Laravel, Nette, Symfony, ' +
        'Next.js, ...) and store it on the site when it changed.\n\n' +
        'Args:\n  domain: Site domain.\n\n' +
        'Returns: { domain, framework } (framework null when nothing was recognised).',
      inputSchema: { domain: DomainSchema },
      annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: true, openWorldHint: false },
    },
    async ({ domain }) => safe(() => daemonClient.post(`/api/sites/${encodeURIComponent(domain)}/detect-framework`)),
  )

  server.registerTool(
    'wdc_duplicate_site',
    {
      title: 'Duplicate a site',
      description:
        'Create a copy of a site under a new domain. The new document root is created next to the ' +
        'source one (named after the new domain) and the config is copied (aliases cleared, Cloudflare ' +
        'subdomain cleared).\n\n' +
        'Args:\n' +
        '  domain: Source site.\n' +
        '  newDomain: Domain of the copy.\n' +
        '  copyFiles: all (whole doc root), top (top level without dotfiles, node_modules, vendor), ' +
        'or empty (empty doc root). Default all.\n\n' +
        'Returns: { domain, documentRoot, sourceDomain, copyFiles, warnings }.',
      inputSchema: {
        domain: DomainSchema,
        newDomain: DomainSchema,
        copyFiles: z.enum(['all', 'top', 'empty']).default('all'),
      },
      annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false },
    },
    async ({ domain, newDomain, copyFiles }) =>
      safe(() => daemonClient.post(`/api/sites/${encodeURIComponent(domain)}/duplicate`, { newDomain, copyFiles })),
  )

  server.registerTool(
    'wdc_site_rollback',
    {
      title: 'Roll a site back to an earlier config (destructive)',
      description:
        'DESTRUCTIVE: Replace the site\'s current config with an archived version from ' +
        'wdc_site_history and re-apply it. The current config is archived first, so this can be undone ' +
        'by rolling back again.\n\n' +
        'Args:\n' +
        '  domain: Site domain.\n' +
        '  timestamp: History entry to restore.\n' +
        '  confirm: Must be "YES".\n\n' +
        'You MUST show the user which version (timestamp) will replace the current config before passing ' +
        'confirm="YES".\n\n' +
        'Returns: { domain, restoredFrom }.',
      inputSchema: {
        domain: DomainSchema,
        timestamp: HistoryTimestampSchema,
        confirm: ConfirmYesSchema,
      },
      annotations: { readOnlyHint: false, destructiveHint: true, idempotentHint: false, openWorldHint: false },
    },
    async ({ domain, timestamp }) =>
      safe(() => daemonClient.post(`/api/sites/${encodeURIComponent(domain)}/rollback`, { timestamp })),
  )
}
