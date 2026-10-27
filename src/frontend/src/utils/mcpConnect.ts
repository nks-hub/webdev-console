/**
 * Copy-paste snippets that register the bundled NKS WDC MCP server with an
 * AI client. The server is a stdio process started with the user's Node.js
 * (`node "<resources>/mcp-server/index.js"`), or without Node.js with the
 * WDC binary itself in Node mode.
 */

export const MCP_SERVER_NAME = 'nks-wdc'

/** Placeholder shown when the server path is unknown (browser dev mode). */
export const MCP_SERVER_PATH_PLACEHOLDER = '<WDC>/resources/mcp-server/index.js'

/** How the AI client starts the server: program, arguments, extra environment. */
export interface McpLaunch {
  command: string
  args: string[]
  env?: Record<string, string>
}

/** Node.js 18+ from PATH. */
export function nodeLaunch(serverPath: string): McpLaunch {
  return { command: 'node', args: [serverPath] }
}

/**
 * WDC's own binary in Node mode, for machines without Node.js. The Windows
 * build asks for administrator rights (requireAdministrator), which an AI
 * client cannot grant; RunAsInvoker starts it with the client's rights.
 * Must match mcpAppRuntimeEnv() in electron/main.ts, which the connection
 * test uses.
 */
export function appLaunch(appPath: string, serverPath: string, platform: string): McpLaunch {
  const env: Record<string, string> = { ELECTRON_RUN_AS_NODE: '1' }
  if (platform === 'win32') env.__COMPAT_LAYER = 'RunAsInvoker'
  return { command: appPath, args: [serverPath], env }
}

// Double quotes keep paths with spaces ("Program Files") intact in cmd,
// PowerShell and POSIX shells. Backslashes are left alone: they are path
// separators on Windows and literal inside double quotes elsewhere.
function quoteArg(value: string): string {
  return /^[\w./:@=-]+$/.test(value) ? value : `"${value.replace(/"/g, '\\"')}"`
}

/**
 * Claude Code CLI registration. `-e` takes several values, so it comes
 * first and `--scope` ends it: right before the server name it would read
 * the name as one more variable. `--` separates the server command, so its
 * own arguments are never parsed as `claude mcp add` flags.
 */
export function claudeCodeCommand(launch: McpLaunch): string {
  const env = Object.entries(launch.env ?? {}).map(([k, v]) => `-e ${k}=${v} `).join('')
  const command = [launch.command, ...launch.args].map(quoteArg).join(' ')
  return `claude mcp add ${env}--scope user ${MCP_SERVER_NAME} -- ${command}`
}

/**
 * Cursor warns above 40 tools and may leave the rest out; the server's
 * `--modules=essential` set has 39 (sites, services, databases, status and
 * approvals).
 */
export function cursorLaunch(launch: McpLaunch): McpLaunch {
  return { ...launch, args: [...launch.args, '--modules=essential'] }
}

function serverEntry(launch: McpLaunch): Record<string, unknown> {
  return { command: launch.command, args: launch.args, ...(launch.env ? { env: launch.env } : {}) }
}

/**
 * `mcpServers` config for a new file. Claude Desktop
 * (claude_desktop_config.json) and Cursor (.cursor/mcp.json) both read
 * this shape.
 */
export function mcpServersJson(launch: McpLaunch): string {
  return JSON.stringify({ mcpServers: { [MCP_SERVER_NAME]: serverEntry(launch) } }, null, 2)
}

/** Only the `"nks-wdc": { … }` entry, for a file that already has other servers. */
export function mcpServerEntryJson(launch: McpLaunch): string {
  return `"${MCP_SERVER_NAME}": ${JSON.stringify(serverEntry(launch), null, 2)}`
}
