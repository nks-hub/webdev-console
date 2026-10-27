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

  it('blocks mcp.* keys, which decide what the agent may do', () => {
    expect(
      protectedSettingKeys([
        'mcp.always_confirm_kinds',
        'mcp.capabilities.service_config',
        'daemon.catalogUrl',
        'backup.dir',
        'ports.http',
      ]),
    ).toEqual(['mcp.always_confirm_kinds', 'mcp.capabilities.service_config'])
  })

  it('leaves other operator settings to the daemon capability check', () => {
    expect(protectedSettingKeys(['daemon.catalogUrl', 'deploy.enabled', 'paths.hostsFile', 'backup.dir'])).toEqual([])
  })

  it('is not fooled by case or surrounding whitespace', () => {
    expect(protectedSettingKeys([' MCP.Capabilities.hosts_file ', 'MCP.enabled'])).toEqual([' MCP.Capabilities.hosts_file ', 'MCP.enabled'])
  })
})
