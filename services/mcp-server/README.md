# @nks-wdc/mcp-server

MCP (Model Context Protocol) server exposing the **NKS WebDev Console** daemon as AI-callable tools and resources. Lets Claude Desktop, Claude Code, Cursor, and other MCP clients fully manage your local development environment.

> **Phase A MVP** — see [`docs/plans/mcp-server-integration-plan.md`](../../docs/plans/mcp-server-integration-plan.md) for the full roadmap.

## Quick start

### Claude Desktop

Add to `~/Library/Application Support/Claude/claude_desktop_config.json` (macOS) or `%APPDATA%\Claude\claude_desktop_config.json` (Windows):

```json
{
  "mcpServers": {
    "nks-wdc": {
      "command": "npx",
      "args": ["-y", "@nks-wdc/mcp-server"]
    }
  }
}
```

Restart Claude Desktop. The 11 Phase A tools appear in the tool picker.

### Claude Code

Add to `~/.claude.json` `mcpServers` section, same shape as above.

### Cursor

Add to `.cursor/mcp.json` in your project, same shape.

### Read-only mode

Append `--readonly` to skip mutation tools (only list/get/metrics):

```json
{
  "mcpServers": {
    "nks-wdc": {
      "command": "npx",
      "args": ["-y", "@nks-wdc/mcp-server", "--readonly"]
    }
  }
}
```

## Phase A tool catalog (11 tools)

| Tool | Type | Description |
|---|---|---|
| `wdc_list_sites` | read | List all configured sites |
| `wdc_get_site` | read | Get a single site by domain |
| `wdc_create_site` | mutate | Create a new site (vhost + hosts + optional SSL) |
| `wdc_delete_site` | **destructive** | Delete a site (requires `confirm: "YES"`) |
| `wdc_get_site_metrics` | read | Live access-log metrics for a site |
| `wdc_list_services` | read | List Apache/MySQL/PHP/etc. with state + CPU/RAM |
| `wdc_start_service` | mutate | Start a service by id |
| `wdc_stop_service` | mutate | Stop a service by id |
| `wdc_get_status` | read | Daemon health check |
| `wdc_get_system_info` | read | Full system snapshot |
| `wdc_get_recent_activity` | read | Recent activity timeline |

Phases B–C add the remaining ~64 tools (databases, SSL, PHP versions, binaries, plugins, backup, Cloudflare, settings sync). See the integration plan for the full mapping.

## Daemon discovery

The MCP server reads the WDC daemon's port + auth token from one of:

1. `$TMPDIR/nks-wdc-daemon.port` (where the daemon writes it on startup)
2. `~/.wdc/daemon.port` (legacy fallback)

If the daemon isn't running, every tool call returns:

```
NKS WDC daemon is not running. Start the WDC GUI or run `wdc daemon start`.
```

The token rotates per daemon restart — the MCP server re-reads the port file automatically on the first 401.

## Security

- **Local-only:** stdio transport means no network exposure; the MCP server is a parent-child process under your AI client
- **Bearer token:** every daemon request is auth-gated by the per-session token from the port file
- **Destructive tools:** `wdc_delete_site` requires explicit `confirm: "YES"` in the input
- **Read-only mode:** `--readonly` flag skips registering mutation tools (they don't appear in `tools/list`)
- **Daemon validation:** all input flows through the daemon's existing validators (`ValidateDomain`, `IsValidDatabaseName`, etc.) — MCP is a thin transport, not the security boundary

## Development

```bash
cd services/mcp-server
npm install
npm run build
npm start
```

Test with the daemon running and a stdio MCP client (e.g., `npx @modelcontextprotocol/inspector node dist/index.js`).

## License

MIT
