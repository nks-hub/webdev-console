import { test, expect } from './_fixtures'
import { readFileSync } from 'node:fs'
import { join, resolve } from 'node:path'
import {
  appLaunch,
  claudeCodeCommand,
  cursorLaunch,
  mcpServerEntryJson,
  mcpServersJson,
  nodeLaunch,
} from '../../../src/utils/mcpConnect'

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
    const server = 'C:\\Program Files\\NKS WebDev Console\\resources\\mcp-server\\index.js'
    // Options before the server name, `--` before the server command.
    expect(claudeCodeCommand(nodeLaunch(server)))
      .toBe(`claude mcp add --scope user nks-wdc -- node "${server}"`)
    expect(JSON.parse(mcpServersJson(nodeLaunch(server))))
      .toEqual({ mcpServers: { 'nks-wdc': { command: 'node', args: [server] } } })
    // The entry alone pastes into an existing mcpServers object.
    expect(JSON.parse(`{${mcpServerEntryJson(nodeLaunch(server))}}`))
      .toEqual({ 'nks-wdc': { command: 'node', args: [server] } })
    // Cursor gets the 39-tool set: it leaves out tools above 40.
    expect(JSON.parse(mcpServersJson(cursorLaunch(nodeLaunch(server)))).mcpServers['nks-wdc'].args)
      .toEqual([server, '--modules=essential'])
  })

  test('without Node.js the snippets start the server with the WDC binary', () => {
    const exe = 'C:\\Program Files\\NKS WebDev Console\\NKS WebDev Console.exe'
    const server = 'C:\\Program Files\\NKS WebDev Console\\resources\\mcp-server\\index.js'
    const win = appLaunch(exe, server, 'win32')
    // The Windows binary is requireAdministrator; RunAsInvoker lets an AI
    // client start it without elevation.
    expect(win.env).toEqual({ ELECTRON_RUN_AS_NODE: '1', __COMPAT_LAYER: 'RunAsInvoker' })
    expect(appLaunch('/Applications/WDC.app/Contents/MacOS/WDC', '/x/index.js', 'darwin').env)
      .toEqual({ ELECTRON_RUN_AS_NODE: '1' })
    // -e is variadic: it must not sit right before the name (checked with
    // claude 2.1: "-e K=V nks-wdc" reads nks-wdc as a variable).
    expect(claudeCodeCommand(win)).toBe(
      `claude mcp add -e ELECTRON_RUN_AS_NODE=1 -e __COMPAT_LAYER=RunAsInvoker --scope user nks-wdc -- "${exe}" "${server}"`)
    expect(JSON.parse(mcpServersJson(win)).mcpServers['nks-wdc'])
      .toEqual({ command: exe, args: [server], env: { ELECTRON_RUN_AS_NODE: '1', __COMPAT_LAYER: 'RunAsInvoker' } })

    // The connection test in main uses the same environment and only lets
    // the renderer choose the runtime, never the command.
    const main = read('electron', 'main.ts')
    expect(main).toMatch(/function mcpAppRuntimeEnv[\s\S]*?ELECTRON_RUN_AS_NODE: '1'[\s\S]*?__COMPAT_LAYER = 'RunAsInvoker'/)
    expect(main).toMatch(/ipcMain\.handle\('mcp-server-selftest', async \(_event, runtime: unknown\)/)
    expect(main).toMatch(/ipcMain\.handle\('mcp-server-info'[\s\S]*?nodeVersion: await detectNodeVersion\(\)/)
    expect(read('electron', 'preload.ts')).toMatch(/testMcpServer: \([\s\S]*?ipcRenderer\.invoke\('mcp-server-selftest', runtime\)/)
    const card = readFileSync(join(srcRoot, 'components', 'mcp', 'McpConnectCard.vue'), 'utf-8')
    expect(card).toContain('window.electronAPI.testMcpServer(runtime.value)')
    expect(card).toContain("runtime.value = serverInfo.value.nodeVersion ? 'node' : 'app'")
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
    const caps = readFileSync(join(srcRoot, 'components', 'settings', 'McpCapabilitiesSettings.vue'), 'utf-8')
    // "How to connect" opens the steps even after an AI client has called WDC.
    expect(caps).toMatch(/data-testid="settings-mcp-connect-link"[\s\S]*?router\.push\('\/mcp\/activity\?setup=1'\)/)
    expect(card).toContain("route.query.setup === '1'")
    expect(card).toContain('if (lastCallAt.value && !setupRequested()) expanded.value = false')
  })

  test('CSV export sends every filter the activity list uses', () => {
    const activity = readFileSync(join(srcRoot, 'components', 'pages', 'McpActivity.vue'), 'utf-8')
    const exportFn = activity.slice(activity.indexOf('async function exportCsv'), activity.indexOf('async function refresh'))
    for (const param of ['dangerLevel', 'toolName', 'sessionId', 'q']) expect(exportFn).toContain(`params.set('${param}'`)
  })

  test('connect strings exist in en and cs', () => {
    for (const locale of ['en', 'cs']) {
      const j = JSON.parse(readFileSync(join(srcRoot, 'locales', `${locale}.json`), 'utf-8'))
      const c = j.mcpConnect
      expect(c.title, `${locale} title`).toBeTruthy()
      expect(c.enableLabel).toBeTruthy()
      expect(c.status.lastCall).toContain('{when}')
      expect(c.status.nodeFound).toContain('{version}')
      expect(c.status.nodeMissing).toBeTruthy()
      expect(c.test.button).toBeTruthy()
      for (const id of ['claudeCode', 'claudeDesktop', 'cursor']) {
        expect(c.clients[id].title).toBeTruthy()
        expect(c.clients[id].where).toBeTruthy()
      }
    }
    const cs = JSON.parse(readFileSync(join(srcRoot, 'locales', 'cs.json'), 'utf-8'))
    expect(cs.mcpConnect.title).toBe('Připojit AI klienta')
  })
})
