import { test, expect } from './_fixtures'
import { readFileSync } from 'node:fs'
import { join, resolve } from 'node:path'

// Source-level contract for "Connect an AI client":
//  - the MCP server bundle is staged and shipped as an extraResource;
//  - main resolves its path and preload exposes it to the renderer;
//  - the MCP hub card renders the exact copy-paste commands, the
//    mcp.enabled switch and live status.

const frontendRoot = process.cwd()
const srcRoot = resolve(frontendRoot, 'src')
const read = (...parts: string[]): string => readFileSync(join(frontendRoot, ...parts), 'utf-8')

test.describe('MCP connect contract', () => {
  test('installer ships the bundled MCP server', () => {
    const builder = read('electron-builder.yml')
    expect(builder).toMatch(/- from: resources\/mcp-server\r?\n\s+to: mcp-server/)
    const pkg = JSON.parse(read('package.json')) as { scripts: Record<string, string> }
    expect(pkg.scripts['stage:mcp']).toContain('services/mcp-server run bundle')
    expect(pkg.scripts['stage:mcp']).toContain('resources/mcp-server')
    for (const script of ['dist', 'dist:mac', 'dist:linux']) {
      expect(pkg.scripts[script], script).toMatch(/npm run stage:mcp && electron-vite build/)
    }
    const mcpPkg = JSON.parse(readFileSync(resolve(frontendRoot, '../../services/mcp-server/package.json'), 'utf-8')) as {
      scripts: Record<string, string>
    }
    expect(mcpPkg.scripts.bundle).toBe('node scripts/bundle.mjs')
  })

  test('main resolves the server path and preload exposes it', () => {
    const main = read('electron', 'main.ts')
    expect(main).toMatch(/join\(process\.resourcesPath, 'mcp-server', 'index\.js'\)/)
    expect(main).toMatch(/ipcMain\.handle\('mcp-server-info'[\s\S]*?nks-wdc-mcp\.port/)
    const preload = read('electron', 'preload.ts')
    expect(preload).toMatch(/getMcpServerInfo: \([\s\S]*?ipcRenderer\.invoke\('mcp-server-info'\)/)
    expect(readFileSync(join(srcRoot, 'env.d.ts'), 'utf-8')).toContain('getMcpServerInfo?: () => Promise<McpServerInfo>')
  })

  test('connect snippets use the verified claude mcp add syntax and mcpServers JSON', () => {
    const util = readFileSync(join(srcRoot, 'utils', 'mcpConnect.ts'), 'utf-8')
    // Options before the server name, `--` before the server command.
    expect(util).toContain('`claude mcp add --scope user ${MCP_SERVER_NAME} -- node ${quoteArg(serverPath)}`')
    expect(util).toContain("export const MCP_SERVER_NAME = 'nks-wdc'")
    expect(util).toMatch(/mcpServers: \{ \[MCP_SERVER_NAME\]: \{ command: 'node', args: \[serverPath\] \} \}/)
  })

  test('MCP hub mounts the card with enable switch and live status', () => {
    const hub = readFileSync(join(srcRoot, 'components', 'pages', 'McpHub.vue'), 'utf-8')
    expect(hub).toContain('<McpConnectCard />')
    const card = readFileSync(join(srcRoot, 'components', 'mcp', 'McpConnectCard.vue'), 'utf-8')
    expect(card).toContain("saveSettings({ 'mcp.enabled': String(enabled) })")
    expect(card).toContain('window.electronAPI?.getMcpServerInfo?.()')
    expect(card).toContain('fetchMcpToolCalls(1, 0)')
    expect(card).toContain("'mcp:tool-call'")
    for (const id of ['claudeCode', 'claudeDesktop', 'cursor']) expect(card).toContain(`id: '${id}'`)
    const settings = readFileSync(join(srcRoot, 'components', 'pages', 'Settings.vue'), 'utf-8')
    expect(settings).toMatch(/data-testid="settings-mcp-connect-link"[\s\S]*?\$router\.push\('\/mcp\/activity'\)/)
  })

  test('connect strings exist in en and cs', () => {
    for (const locale of ['en', 'cs']) {
      const j = JSON.parse(readFileSync(join(srcRoot, 'locales', `${locale}.json`), 'utf-8'))
      const c = j.mcpConnect
      expect(c.title, `${locale} title`).toBeTruthy()
      expect(c.enableLabel).toBeTruthy()
      expect(c.status.lastCall).toContain('{when}')
      for (const id of ['claudeCode', 'claudeDesktop', 'cursor']) {
        expect(c.clients[id].title).toBeTruthy()
        expect(c.clients[id].where).toBeTruthy()
      }
    }
    const cs = JSON.parse(readFileSync(join(srcRoot, 'locales', 'cs.json'), 'utf-8'))
    expect(cs.mcpConnect.title).toBe('Připojit AI klienta')
  })
})
