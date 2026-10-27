import { test, expect } from './_fixtures'
import { readFileSync } from 'node:fs'
import { join, resolve } from 'node:path'
import { isSettingSyncable, pickSyncableSettings } from '../../../src/utils/settingsSync'

// Settings export / import and cloud push / pull must never carry the
// catalog login token or this device's identity: an imported file used to
// sign the device into someone else's account and clone its device id.

const srcRoot = resolve(process.cwd(), 'src')

test.describe('Settings sync contract', () => {
  test('secrets and device identity never leave the machine', () => {
    const picked = pickSyncableSettings({
      'sync.accountToken': 'eyJhbGciOi',
      'sync.accountEmail': 'user@example.com',
      'sync.deviceId': 'dev-1',
      'sync.deviceName': 'Laptop',
      'sync.lastSyncTime': '2026-01-01T00:00:00Z',
      'ports.http': '80',
      'paths.apache': 'C:\\x',
      'mcp.enabled': 'true',
      'telemetry.enabled': 'true',
      'backup.scheduleHours': '24',
      'backup.content.ssl': 'false',
      'daemon.catalogUrl': 'https://catalog.example.com',
    })
    expect(picked).toEqual({
      'backup.scheduleHours': '24',
      'backup.content.ssl': 'false',
      'daemon.catalogUrl': 'https://catalog.example.com',
    })
    expect(isSettingSyncable('sync.newSecret')).toBe(false)
  })

  test('Settings.vue filters every inbound and outbound path through the allowlist', () => {
    const s = readFileSync(join(srcRoot, 'components', 'pages', 'Settings.vue'), 'utf-8')
    expect(s).toContain("from '../../utils/settingsSync'")
    expect(s).not.toMatch(/SYNC_SETTINGS_PREFIXES/)
    // export / push payload, pull, import and snapshot restore
    expect(s.match(/pickSyncableSettings\(/g)?.length ?? 0).toBeGreaterThanOrEqual(4)
    expect(s).not.toMatch(/deviceId: deviceId\.value,\s*\n\s*deviceName/)
  })
})
