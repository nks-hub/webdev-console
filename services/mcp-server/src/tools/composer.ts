// Composer tools — per-site dependency management through the daemon's
// Composer plugin (src/daemon/.../Sites/ComposerEndpoints.cs). Commands run
// in the site's document root (or the composer.json root above it) with the
// site's PHP version.
import { z } from 'zod'
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js'

import { daemonClient } from '../daemonClient.js'
import type { RegisterOptions } from '../index.js'
import { safe } from '../formatting.js'
import { DomainSchema, capabilityNote } from '../schemas.js'

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

// Install, require and remove run the project's Composer scripts (arbitrary
// code), so the daemon only allows them in the "composer" capability group.
const COMPOSER = capabilityNote('Composer commands')

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
    async ({ domain }) => safe(() => daemonClient.get(`/api/sites/${encodeURIComponent(domain)}/composer/status`)),
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
    async ({ domain }) => safe(() => daemonClient.get(`/api/sites/${encodeURIComponent(domain)}/composer/outdated`)),
  )

  if (opts.readonly) return

  server.registerTool(
    'wdc_composer_install',
    {
      title: 'composer install',
      description:
        'Run `composer install` for a site: installs the locked dependencies into vendor/ (runs the ' +
        'project\'s Composer scripts).\n\n' +
        `${COMPOSER}\n\n` +
        'Args:\n  domain: Site domain.\n\n' +
        'Returns: { exitCode, stdout, stderr } (exitCode 0 = success).',
      inputSchema: { domain: DomainSchema },
      annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: true, openWorldHint: true },
    },
    async ({ domain }) => safe(() => daemonClient.post(`/api/sites/${encodeURIComponent(domain)}/composer/install`)),
  )

  server.registerTool(
    'wdc_composer_require',
    {
      title: 'composer require',
      description:
        'Add a package to a site with `composer require` (updates composer.json and composer.lock; runs the ' +
        'project\'s and the package\'s Composer scripts).\n\n' +
        `${COMPOSER}\n\n` +
        'Args:\n  domain: Site domain.\n  package: vendor/name, optionally with a constraint (vendor/name:^1.0).\n\n' +
        'Returns: { exitCode, stdout, stderr }.',
      inputSchema: { domain: DomainSchema, package: PackageSchema },
      annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: true, openWorldHint: true },
    },
    async ({ domain, package: pkg }) => safe(() => daemonClient.post(`/api/sites/${encodeURIComponent(domain)}/composer/require`, { package: pkg })),
  )

  server.registerTool(
    'wdc_composer_remove',
    {
      title: 'composer remove',
      description:
        'Remove a package from a site with `composer remove` (updates composer.json and composer.lock, ' +
        'deletes it from vendor/; runs the project\'s Composer scripts).\n\n' +
        `${COMPOSER}\n\n` +
        'Args:\n  domain: Site domain.\n  package: vendor/name.\n\n' +
        'Returns: { exitCode, stdout, stderr }.',
      inputSchema: { domain: DomainSchema, package: PackageNameSchema },
      annotations: { readOnlyHint: false, destructiveHint: true, idempotentHint: true, openWorldHint: false },
    },
    async ({ domain, package: pkg }) => safe(() => daemonClient.post(`/api/sites/${encodeURIComponent(domain)}/composer/remove`, { package: pkg })),
  )
}
