#!/usr/bin/env node
/**
 * NKS WDC MCP server — stdio entry point.
 *
 * Connects an MCP-compatible AI client (Claude Desktop, Claude Code, Cursor)
 * to the locally-running NKS WebDev Console daemon. Discovers the daemon via
 * the same `~/.wdc/daemon.port` file the wdc CLI uses, forwards each tool
 * call to the daemon's REST API with structured error surfacing.
 *
 * Usage:
 *   nks-wdc-mcp                 (mutation + read-only tools)
 *   nks-wdc-mcp --readonly      (read-only tools only)
 *
 * MUST log to stderr only — stdout is reserved for MCP protocol frames.
 */

import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js'
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js'

import { registerSitesTools } from './tools/sites.js'
import { registerServicesTools } from './tools/services.js'
import { registerSystemTools } from './tools/system.js'
import { registerDatabasesTools } from './tools/databases.js'
import { registerSslTools } from './tools/ssl.js'
import { registerPhpTools } from './tools/php.js'
import { registerBinariesTools } from './tools/binaries.js'
import { registerPluginsTools } from './tools/plugins.js'
import { registerBackupTools } from './tools/backup.js'
import { registerSettingsTools } from './tools/settings.js'
import { registerCloudflareTools } from './tools/cloudflare.js'
import { registerDeployTools } from './tools/deploy.js'
import { daemonClient } from './daemonClient.js'
import { wrapHandler } from './auditLog.js'

export interface RegisterOptions {
  readonly: boolean
  /**
   * Per-token deploy scope set, parsed from the port file's optional
   * `scope:` line. `['*']` = no restriction (legacy default). Concrete
   * scopes: `deploy:read`, `deploy:write`, `deploy:admin`. The deploy
   * tool registrar consults this to decide which of the 12 tools to
   * actually expose to the AI client.
   */
  deployScopes: string[]
}

const READONLY = process.argv.includes('--readonly')

const server = new McpServer({
  name: 'nks-wdc-mcp-server',
  version: '0.3.0',
})

// Phase 8 — wrap registerTool ONCE so every subsequent registration
// gets audit logging baked in. The wrapper inspects the third positional
// argument (the handler in @modelcontextprotocol/sdk@0.6+) and replaces
// it with `wrapHandler(name, handler)`. Reading the SDK source: the
// signature is registerTool(name, schema, handler) — handler is the
// function that fires when the AI client invokes the tool.
{
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  const original = (server as any).registerTool.bind(server)
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  ;(server as any).registerTool = (name: string, schema: any, handler: any) => {
    if (typeof handler === 'function') {
      return original(name, schema, wrapHandler(name, handler))
    }
    return original(name, schema, handler)
  }
}

// Pre-warm the connection so we can read the port file's scope line. Best-
// effort: if the daemon is not running, fall back to `['*']` so the MCP
// server still starts cleanly and a deferred client request triggers the
// "daemon not running" error path at call time instead of during boot.
let deployScopes: string[] = ['*']
try {
  await daemonClient.get('/healthz').catch(() => undefined)
  deployScopes = daemonClient.scopes()
} catch {
  /* swallow — daemon offline at MCP boot */
}

const opts: RegisterOptions = { readonly: READONLY, deployScopes }

// Each module's register function calls server.registerTool(...) for its
// tools. The READONLY flag is forwarded so mutation tools can opt out of
// registration when running in observe-only mode.
registerSitesTools(server, opts)
registerServicesTools(server, opts)
registerSystemTools(server, opts)
registerDatabasesTools(server, opts)
registerSslTools(server, opts)
registerPhpTools(server, opts)
registerBinariesTools(server, opts)
registerPluginsTools(server, opts)
registerBackupTools(server, opts)
registerSettingsTools(server, opts)
registerCloudflareTools(server, opts)
registerDeployTools(server, opts)

const transport = new StdioServerTransport()
await server.connect(transport)

process.stderr.write(
  `[nks-wdc-mcp-server] connected via stdio${READONLY ? ' (read-only)' : ''} ` +
  `[deploy-scopes: ${deployScopes.join(',') || 'none'}]\n`,
)
