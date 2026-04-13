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

export interface RegisterOptions {
  readonly: boolean
}

const READONLY = process.argv.includes('--readonly')

const server = new McpServer({
  name: 'nks-wdc-mcp-server',
  version: '0.2.0',
})

const opts: RegisterOptions = { readonly: READONLY }

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

const transport = new StdioServerTransport()
await server.connect(transport)

process.stderr.write(
  `[nks-wdc-mcp-server] connected via stdio${READONLY ? ' (read-only)' : ''}\n`,
)
