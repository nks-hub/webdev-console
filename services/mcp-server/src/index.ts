#!/usr/bin/env node
// NKS WDC MCP server — stdio entry point.
//
// Connects an MCP-compatible AI client (Claude Desktop, Claude Code, Cursor)
// to the locally-running NKS WebDev Console daemon. The server discovers the
// daemon via the same `~/.wdc/daemon.port` file the wdc CLI uses, forwards
// each tool call to the daemon's REST API, and surfaces structured errors.
//
// Usage:
//   nks-wdc-mcp                 (mutation + read-only tools)
//   nks-wdc-mcp --readonly      (read-only tools only)

import { Server } from '@modelcontextprotocol/sdk/server/index.js'
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js'
import {
  CallToolRequestSchema,
  ListToolsRequestSchema,
} from '@modelcontextprotocol/sdk/types.js'
import { sitesTools } from './tools/sites.js'
import { servicesTools } from './tools/services.js'
import { systemTools } from './tools/system.js'
import { databasesTools } from './tools/databases.js'
import { sslTools } from './tools/ssl.js'
import { phpTools } from './tools/php.js'
import { binariesTools } from './tools/binaries.js'
import { pluginsTools } from './tools/plugins.js'
import { backupTools } from './tools/backup.js'
import { settingsTools } from './tools/settings.js'
import { cloudflareTools } from './tools/cloudflare.js'

const READONLY = process.argv.includes('--readonly')

// Tool registry — Phase A+B+C covers 47 daemon endpoints across 11 modules.
// Coverage roughly matches the planned ~75 tools from
// docs/plans/mcp-server-integration-plan.md (remaining items: per-site
// node lifecycle, sync push/pull, device fleet, more activity/history).
const allTools = [
  ...sitesTools,
  ...servicesTools,
  ...systemTools,
  ...databasesTools,
  ...sslTools,
  ...phpTools,
  ...binariesTools,
  ...pluginsTools,
  ...backupTools,
  ...settingsTools,
  ...cloudflareTools,
]

// In read-only mode skip tools that mutate daemon state. We tag mutations
// by inspecting the tool description for "DESTRUCTIVE" or by hard-coding
// the read-only allowlist. Phase A uses the explicit allowlist.
const READONLY_TOOL_NAMES: ReadonlySet<string> = new Set<string>([
  // sites
  'wdc_list_sites',
  'wdc_get_site',
  'wdc_get_site_metrics',
  // services
  'wdc_list_services',
  // system
  'wdc_get_status',
  'wdc_get_system_info',
  'wdc_get_recent_activity',
  // databases
  'wdc_list_databases',
  'wdc_database_tables',
  // ssl
  'wdc_list_certs',
  // php
  'wdc_list_php_versions',
  // binaries
  'wdc_list_catalog',
  'wdc_list_installed_binaries',
  // plugins
  'wdc_list_plugins',
  'wdc_get_plugin_marketplace',
  // backup
  'wdc_list_backups',
  // settings
  'wdc_get_settings',
  // cloudflare
  'wdc_get_cloudflare_config',
  'wdc_verify_cloudflare_token',
  'wdc_list_cloudflare_zones',
  'wdc_list_cloudflare_dns',
  'wdc_list_cloudflare_tunnels',
  'wdc_get_cloudflare_subdomain_suggestion',
])

const tools = READONLY
  ? allTools.filter(t => READONLY_TOOL_NAMES.has(t.name as string))
  : allTools

const toolMap = new Map<string, (typeof allTools)[number]>(
  tools.map(t => [t.name as string, t]),
)

const server = new Server(
  {
    name: 'nks-wdc',
    version: '0.1.0',
  },
  {
    capabilities: {
      tools: {},
    },
  },
)

server.setRequestHandler(ListToolsRequestSchema, async () => ({
  tools: tools.map(t => ({
    name: t.name,
    description: t.description,
    inputSchema: t.inputSchema,
  })),
}))

server.setRequestHandler(CallToolRequestSchema, async request => {
  const { name, arguments: args } = request.params
  const tool = toolMap.get(name)
  if (!tool) {
    throw new Error(`Unknown tool: ${name}`)
  }
  try {
    const result = await (tool.handler as (a: any) => Promise<unknown>)(args ?? {})
    return {
      content: [
        {
          type: 'text',
          text: typeof result === 'string' ? result : JSON.stringify(result, null, 2),
        },
      ],
    }
  } catch (err: any) {
    return {
      isError: true,
      content: [
        {
          type: 'text',
          text: `Error: ${err?.message ?? String(err)}`,
        },
      ],
    }
  }
})

const transport = new StdioServerTransport()
await server.connect(transport)

// Log to stderr (stdout is reserved for MCP protocol frames)
process.stderr.write(
  `[nks-wdc-mcp] connected — ${tools.length} tools registered${READONLY ? ' (read-only)' : ''}\n`,
)
