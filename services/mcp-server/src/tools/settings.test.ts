/**
 * Unit tests for the operator-only settings guard on wdc_update_settings.
 */

import { describe, it, expect, vi } from 'vitest'

vi.mock('../daemonClient.js', () => ({ daemonClient: {} }))

import { protectedSettingKeys } from './settings.js'

describe('protectedSettingKeys', () => {
  it('lets ordinary preferences through', () => {
    expect(protectedSettingKeys(['ports.http', 'general.theme', 'backup.scheduleHours'])).toEqual([])
  })

  it('blocks keys that change what the daemon trusts or where it writes', () => {
    expect(
      protectedSettingKeys([
        'daemon.catalogUrl',
        'mcp.always_confirm_kinds',
        'deploy.enabled',
        'plugins.autoSyncEnabled',
        'paths.hostsFile',
        'backup.dir',
        'ports.http',
      ]),
    ).toEqual([
      'daemon.catalogUrl',
      'mcp.always_confirm_kinds',
      'deploy.enabled',
      'plugins.autoSyncEnabled',
      'paths.hostsFile',
      'backup.dir',
    ])
  })

  it('is not fooled by case or surrounding whitespace', () => {
    expect(protectedSettingKeys([' Daemon.CatalogUrl ', 'MCP.enabled'])).toEqual([' Daemon.CatalogUrl ', 'MCP.enabled'])
  })
})
