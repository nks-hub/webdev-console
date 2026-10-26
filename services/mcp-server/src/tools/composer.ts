// Composer tools — per-site dependency management through the daemon's
// Composer plugin (src/daemon/.../Sites/ComposerEndpoints.cs). Commands run
// in the site's document root (or the composer.json root above it) with the
// site's PHP version.
import { z } from 'zod'
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js'

import { daemonClient } from '../daemonClient.js'
import type { RegisterOptions } from '../index.js'
import { safe } from '../formatting.js'
import { DomainSchema } from '../schemas.js'

/** vendor/name with an optional version constraint, as the daemon accepts it for require. */
const PackageSchema = z
  .string()
  .min(3)
  .max(200)
  .regex(/^[A-Za-z0-9/_.:\-^~*@]+$/, 'Package must look like vendor/name or vendor/name:^1.2')
  .refine(s => !s.includes('..'), 'Package must not contain ".."')
  .describe('Package like "monolog/monolog" or with a constraint "monolog/monolog:^3.0"')

const PackageNameSchema = z
  .string()
  .min(3)
  .max(200)
  .regex(/^[A-Za-z0-9/_.-]+$/, 'Package must look like vendor/name (no version constraint)')
  .refine(s => !s.includes('..'), 'Package must not contain ".."')
  .describe('Package like "monolog/monolog"')

const site = (domain: string) => `/api/sites/${encodeURIComponent(domain)}/composer`

export function registerComposerTools(server: McpServer, opts: RegisterOptions): void {
  server.registerTool(
    'wdc_composer_status',
    {
      title: 'Composer status of a site',
      description:
        'Show whether a site uses Composer and what it requires.\n\n' +
        'Args:\n  domain: Site domain.\n\n' +
        'Returns: { hasComposerJson, hasLock, packages, phpVersion, framework, installSuggestion, composerRoot }.',
      inputSchema: { domain: DomainSchema },
      annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false },
    },
    async ({ domain }) => safe(() => daemonClient.get(`${site(domain)}/status`)),
  )

  server.registerTool(
    'wdc_composer_outdated',
    {
      title: 'List outdated Composer packages',
      description:
        'Run `composer outdated` for a site (queries Packagist).\n\n' +
        'Args:\n  domain: Site domain.\n\n' +
        'Returns: { installed: [{ name, version, latest, ... }] }.',
      inputSchema: { domain: DomainSchema },
      annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: true },
    },
    async ({ domain }) => safe(() => daemonClient.get(`${site(domain)}/outdated`)),
  )

  if (opts.readonly) return

  server.registerTool(
    'wdc_composer_install',
    {
      title: 'composer install',
      description:
        'Run `composer install` for a site: installs the locked dependencies into vendor/ (runs the ' +
        'project\'s Composer scripts).\n\n' +
        'Args:\n  domain: Site domain.\n\n' +
        'Returns: { exitCode, stdout, stderr } (exitCode 0 = success).',
      inputSchema: { domain: DomainSchema },
      annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: true, openWorldHint: true },
    },
    async ({ domain }) => safe(() => daemonClient.post(`${site(domain)}/install`)),
  )

  server.registerTool(
    'wdc_composer_require',
    {
      title: 'composer require',
      description:
        'Add a package to a site with `composer require` (updates composer.json and composer.lock).\n\n' +
        'Args:\n  domain: Site domain.\n  package: vendor/name, optionally with a constraint (vendor/name:^1.0).\n\n' +
        'Returns: { exitCode, stdout, stderr }.',
      inputSchema: { domain: DomainSchema, package: PackageSchema },
      annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: true, openWorldHint: true },
    },
    async ({ domain, package: pkg }) => safe(() => daemonClient.post(`${site(domain)}/require`, { package: pkg })),
  )

  server.registerTool(
    'wdc_composer_remove',
    {
      title: 'composer remove',
      description:
        'Remove a package from a site with `composer remove` (updates composer.json and composer.lock, ' +
        'deletes it from vendor/).\n\n' +
        'Args:\n  domain: Site domain.\n  package: vendor/name.\n\n' +
        'Returns: { exitCode, stdout, stderr }.',
      inputSchema: { domain: DomainSchema, package: PackageNameSchema },
      annotations: { readOnlyHint: false, destructiveHint: true, idempotentHint: true, openWorldHint: false },
    },
    async ({ domain, package: pkg }) => safe(() => daemonClient.post(`${site(domain)}/remove`, { package: pkg })),
  )
}
