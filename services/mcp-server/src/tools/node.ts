// Node.js site processes — the apps WDC runs for sites with a Node upstream
// port (src/daemon/.../Sites/NodeSiteEndpoints.cs).
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js'

import { daemonClient } from '../daemonClient.js'
import type { RegisterOptions } from '../index.js'
import { safe } from '../formatting.js'
import { DomainSchema, capabilityNote } from '../schemas.js'
import { IntentTokenSchema, intentHeaders } from './confirmation.js'

const NODE_PROCESS = capabilityNote('Node.js start commands')

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
        'Start the Node.js process of a site (its nodeStartCommand on its nodeUpstreamPort). The daemon ' +
        'runs that command as a shell command.\n\n' +
        `${NODE_PROCESS} Each start needs the user's approval: ` +
        'wdc_request_confirmation(operation: "node_process", target: domain), then pass the approved intentToken.\n\n' +
        'Args:\n  domain: Site domain; the site must have a Node upstream port.\n  intentToken: Approved intent.\n\n' +
        'Returns: the process status.',
      inputSchema: { domain: DomainSchema, intentToken: IntentTokenSchema },
      // A second start needs a new approval (the intent token is single-use).
      annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false },
    },
    async ({ domain, intentToken }) =>
      safe(() => daemonClient.post(`/api/node/sites/${encodeURIComponent(domain)}/start`, undefined, intentHeaders(intentToken))),
  )

  server.registerTool(
    'wdc_stop_node_site',
    {
      title: 'Stop a Node.js site',
      description:
        'Stop the Node.js process of a site (the site stops answering until started again).\n\n' +
        `${NODE_PROCESS}\n\n` +
        'Args:\n  domain: Site domain.\n\n' +
        'Returns: { ok, domain }.',
      inputSchema: { domain: DomainSchema },
      annotations: { readOnlyHint: false, destructiveHint: true, idempotentHint: true, openWorldHint: false },
    },
    async ({ domain }) => safe(() => daemonClient.post(`/api/node/sites/${encodeURIComponent(domain)}/stop`)),
  )

  server.registerTool(
    'wdc_restart_node_site',
    {
      title: 'Restart a Node.js site',
      description:
        'Restart the Node.js process of a site, e.g. after changing its code or environment. The daemon ' +
        'runs its nodeStartCommand as a shell command.\n\n' +
        `${NODE_PROCESS} Each restart needs the user's approval: ` +
        'wdc_request_confirmation(operation: "node_process", target: domain), then pass the approved intentToken.\n\n' +
        'Args:\n  domain: Site domain.\n  intentToken: Approved intent.\n\n' +
        'Returns: the process status.',
      inputSchema: { domain: DomainSchema, intentToken: IntentTokenSchema },
      annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false },
    },
    async ({ domain, intentToken }) =>
      safe(() => daemonClient.post(`/api/node/sites/${encodeURIComponent(domain)}/restart`, undefined, intentHeaders(intentToken))),
  )
}
