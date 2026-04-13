// PHP version + extension management.
import { daemonClient } from '../daemonClient.js'

export const phpTools = [
  {
    name: 'wdc_list_php_versions',
    description:
      'List all detected PHP installations with version, executable path, isDefault flag, extension count, and active site count.',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
    handler: async () => daemonClient.get('/api/php/versions'),
  },
  {
    name: 'wdc_set_default_php',
    description:
      'Set the default PHP version used by sites that don\'t override it. Restarts the corresponding php-cgi pool. Returns the new active version.',
    inputSchema: {
      type: 'object',
      required: ['version'],
      properties: {
        version: {
          type: 'string',
          description: 'PHP major.minor like "8.3" or full version like "8.3.10".',
        },
      },
      additionalProperties: false,
    },
    handler: async (args: { version: string }) =>
      daemonClient.put('/api/php/default', { version: args.version }),
  },
  {
    name: 'wdc_toggle_php_extension',
    description:
      'Enable or disable a PHP extension for a specific version. Daemon restarts the corresponding php-cgi pool after the change. Use wdc_list_php_versions first to discover installed major.minor values.',
    inputSchema: {
      type: 'object',
      required: ['majorMinor', 'extensionName', 'enabled'],
      properties: {
        majorMinor: {
          type: 'string',
          pattern: '^[0-9]+\\.[0-9]+$',
          description: 'PHP version like "8.3" — the daemon globs matching install dirs.',
        },
        extensionName: {
          type: 'string',
          description: 'Extension name like "redis", "imagick", "xdebug" (without the .so/.dll suffix).',
        },
        enabled: { type: 'boolean' },
      },
      additionalProperties: false,
    },
    handler: async (args: { majorMinor: string; extensionName: string; enabled: boolean }) =>
      daemonClient.post(
        `/api/php/${encodeURIComponent(args.majorMinor)}/extensions/${encodeURIComponent(args.extensionName)}`,
        { enabled: args.enabled },
      ),
  },
] as const
