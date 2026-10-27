// Server-level guidance for the AI client.

/**
 * Sent to the client in the initialize response; clients show it to the model
 * before any tool call.
 */
export const SERVER_INSTRUCTIONS =
  'Tools for the local NKS WebDev Console (WDC) daemon: sites, services, databases, SSL, PHP, ' +
  'binaries, plugins, backups, Cloudflare and deploys. Destructive operations need the user\'s ' +
  'approval in the WDC app: call wdc_request_confirmation, tell the user what you want to do and why, ' +
  'wait until they approve (wdc_get_confirmation_status, or waitSeconds), then call the tool with the ' +
  'returned intentToken; each token works once, for exactly that operation and target. What MCP may ' +
  'do at all is configured by the user in WDC (MCP must be enabled, and capability groups can be ' +
  'turned off); you cannot change those settings. When a tool fails, read the whole error: it carries ' +
  'the daemon\'s reason, detail and hint plus a "Next step" telling you what to do, for example request ' +
  'a confirmation, wait for approval, or ask the user to enable something in WDC.'
