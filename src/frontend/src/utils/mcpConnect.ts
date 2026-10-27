/**
 * Copy-paste snippets that register the bundled NKS WDC MCP server with an
 * AI client. The server is a stdio process started with the user's Node.js:
 * `node "<resources>/mcp-server/index.js"`.
 */

export const MCP_SERVER_NAME = 'nks-wdc'

/** Placeholder shown when the server path is unknown (browser dev mode). */
export const MCP_SERVER_PATH_PLACEHOLDER = '<WDC>/resources/mcp-server/index.js'

// Double quotes keep paths with spaces ("Program Files") intact in cmd,
// PowerShell and POSIX shells. Backslashes are left alone: they are path
// separators on Windows and literal inside double quotes elsewhere.
function quoteArg(value: string): string {
  return `"${value.replace(/"/g, '\\"')}"`
}

/**
 * Claude Code CLI registration. Options go before the server name and `--`
 * separates the server command, so its own arguments are never parsed as
 * `claude mcp add` flags.
 */
export function claudeCodeCommand(serverPath: string): string {
  return `claude mcp add --scope user ${MCP_SERVER_NAME} -- node ${quoteArg(serverPath)}`
}

/**
 * `mcpServers` config block. Claude Desktop (claude_desktop_config.json) and
 * Cursor (.cursor/mcp.json) both read this shape.
 */
export function mcpServersJson(serverPath: string): string {
  return JSON.stringify(
    { mcpServers: { [MCP_SERVER_NAME]: { command: 'node', args: [serverPath] } } },
    null,
    2,
  )
}
