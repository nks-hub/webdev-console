// Node.js site processes — the apps WDC runs for sites with a Node upstream
// port (src/daemon/.../Sites/NodeSiteEndpoints.cs).
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js'

import { daemonClient } from '../daemonClient.js'
import type { RegisterOptions } from '../index.js'
import { safe } from '../formatting.js'
import { DomainSchema } from '../schemas.js'

const nodeSite = (domain: string, action: string) => `/api/node/sites/${encodeURIComponent(domain)}/${action}`

export function registerNodeTools(server: McpServer, opts: RegisterOptions): void {
  server.registerTool(
    'wdc_list_node_sites',
    {
      title: 'List Node.js site processes',
      description:
        'List the Node.js processes WDC manages for sites.\n\n' +
        'Returns: Array of per-site process statuses from the Node.js plugin; empty when it is not loaded.',
      inputSchema: {},
      annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false },
    },
    async () => safe(() => daemonClient.get('/api/node/sites')),
  )

  if (opts.readonly) return

  server.registerTool(
    'wdc_start_node_site',
    {
      title: 'Start a Node.js site',
      description:
        'Start the Node.js process of a site (its nodeStartCommand on its nodeUpstreamPort).\n\n' +
        'Args:\n  domain: Site domain; the site must have a Node upstream port.\n\n' +
        'Returns: the process status.',
      inputSchema: { domain: DomainSchema },
      annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: true, openWorldHint: false },
    },
    async ({ domain }) => safe(() => daemonClient.post(nodeSite(domain, 'start'))),
  )

  server.registerTool(
    'wdc_stop_node_site',
    {
      title: 'Stop a Node.js site',
      description:
        'Stop the Node.js process of a site (the site stops answering until started again).\n\n' +
        'Args:\n  domain: Site domain.\n\n' +
        'Returns: { ok, domain }.',
      inputSchema: { domain: DomainSchema },
      annotations: { readOnlyHint: false, destructiveHint: true, idempotentHint: true, openWorldHint: false },
    },
    async ({ domain }) => safe(() => daemonClient.post(nodeSite(domain, 'stop'))),
  )

  server.registerTool(
    'wdc_restart_node_site',
    {
      title: 'Restart a Node.js site',
      description:
        'Restart the Node.js process of a site, e.g. after changing its code or environment.\n\n' +
        'Args:\n  domain: Site domain.\n\n' +
        'Returns: the process status.',
      inputSchema: { domain: DomainSchema },
      annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false },
    },
    async ({ domain }) => safe(() => daemonClient.post(nodeSite(domain, 'restart'))),
  )
}
