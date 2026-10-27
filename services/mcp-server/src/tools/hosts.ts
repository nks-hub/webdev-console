// Hosts file and DNS cache (src/daemon/.../Hosts/HostsEndpoints.cs,
// Services/DnsEndpoints.cs). Writing the hosts file needs elevation and
// stays in the WDC app; WDC keeps its own entries in sync when sites change.
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js'

import { daemonClient } from '../daemonClient.js'
import type { RegisterOptions } from '../index.js'
import { safe } from '../formatting.js'

export function registerHostsTools(server: McpServer, opts: RegisterOptions): void {
  server.registerTool(
    'wdc_list_hosts',
    {
      title: 'List hosts file entries',
      description:
        'Read the system hosts file. Each entry says whether it belongs to a WDC site.\n\n' +
        'Returns: Array of { enabled, ip, hostname, source (wdc | external), comment, lineNumber }.',
      inputSchema: {},
      annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false },
    },
    async () => safe(() => daemonClient.get('/api/hosts')),
  )

  if (opts.readonly) return

  server.registerTool(
    'wdc_flush_dns',
    {
      title: 'Flush the DNS cache',
      description:
        'Flush the operating system\'s DNS resolver cache, e.g. when a new site domain does not resolve ' +
        'yet. Supported on Windows; elsewhere it reports ok: false.\n\n' +
        'Returns: { ok, output }.',
      inputSchema: {},
      annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: true, openWorldHint: false },
    },
    async () => safe(() => daemonClient.post('/api/dns/flush')),
  )
}
