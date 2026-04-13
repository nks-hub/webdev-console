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

const READONLY = process.argv.includes('--readonly')

// Tool registry — Phase A MVP includes the 11 most-used daemon endpoints.
// Future phases add the full ~75 tools per docs/plans/mcp-server-integration-plan.md.
const allTools = [
  ...sitesTools,
  ...servicesTools,
  ...systemTools,
]

// In read-only mode skip tools that mutate daemon state. We tag mutations
// by inspecting the tool description for "DESTRUCTIVE" or by hard-coding
// the read-only allowlist. Phase A uses the explicit allowlist.
const READONLY_TOOL_NAMES: ReadonlySet<string> = new Set<string>([
  'wdc_list_sites',
  'wdc_get_site',
  'wdc_get_site_metrics',
  'wdc_list_services',
  'wdc_get_status',
  'wdc_get_system_info',
  'wdc_get_recent_activity',
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
