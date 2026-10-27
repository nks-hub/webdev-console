import { test, expect } from './_fixtures'
import { readFileSync } from 'node:fs'
import { join, resolve } from 'node:path'

// Source-level contract: the operator switches for MCP capability groups
// read the daemon catalog and write only `mcp.capabilities.*` through the
// GUI settings call, behind a confirmation when enabling.

const srcRoot = resolve(process.cwd(), 'src')

test.describe('MCP capabilities settings contract', () => {
  const source = () => readFileSync(join(srcRoot, 'components', 'settings', 'McpCapabilitiesSettings.vue'), 'utf-8')

  test('loads the catalog from GET /api/mcp/capabilities', () => {
    expect(source()).toMatch(/\/api\/mcp\/capabilities/)
  })

  test('saves the daemon-provided settingKey and confirms before enabling', () => {
    const s = source()
    expect(s).toMatch(/saveSettings\(\{\s*\[cap\.settingKey\]:\s*String\(enabled\)\s*\}\)/)
    expect(s.indexOf('ElMessageBox.confirm')).toBeLessThan(s.indexOf('saveSettings({'))
  })

  test('Settings registers the MCP section and hides the save footer there', () => {
    const s = readFileSync(join(srcRoot, 'components', 'pages', 'Settings.vue'), 'utf-8')
    expect(s).toMatch(/name: 'mcp', label: t\('settings\.tabs\.mcp'\)/)
    expect(s).toMatch(/<McpCapabilitiesSettings v-if="activeTab === 'mcp'" \/>/)
    expect(s).toMatch(/activeTab !== 'mcp'" #footer/)
  })

  test('every capability group has en + cs labels', () => {
    const ids = ['settings_operator', 'service_config', 'hosts_file', 'node_process', 'site_advanced_fields',
      'cloudflare_credentials', 'plugin_install', 'database_admin', 'backup_restore', 'deploy_config',
      'daemon_admin', 'unlisted_routes', 'composer']
    for (const locale of ['en', 'cs']) {
      const json = JSON.parse(readFileSync(join(srcRoot, 'locales', `${locale}.json`), 'utf-8')) as
        { mcpCapabilities?: { groups?: Record<string, { title?: string; description?: string }> } }
      for (const id of ids) {
        expect(json.mcpCapabilities?.groups?.[id]?.title, `${locale} ${id}`).toBeTruthy()
        expect(json.mcpCapabilities?.groups?.[id]?.description, `${locale} ${id}`).toBeTruthy()
      }
    }
  })
})
