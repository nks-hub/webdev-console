# @nks-wdc/mcp-server

MCP (Model Context Protocol) server exposing the **NKS WebDev Console** daemon as AI-callable tools and resources. Lets Claude Desktop, Claude Code, Cursor, and other MCP clients fully manage your local development environment.

> **72 tools across 14 modules** (33 with `--readonly`) — built on the modern `McpServer.registerTool()` API with Zod input schemas, honest tool annotations (`readOnlyHint` / `destructiveHint` / `idempotentHint` / `openWorldHint`), shared response formatting with JSON-envelope truncation at 25 KB, split read/write SQL tools (`wdc_query` vs `wdc_execute`), and non-empty credential schemas to prevent accidental wipes. See [`docs/plans/mcp-server-integration-plan.md`](../../docs/plans/mcp-server-integration-plan.md) for the full roadmap.

## Connect an AI client

The WDC installer ships this server as one bundled file,
`<WDC resources>/mcp-server/index.js`. In the app, **MCP → Connect an AI
client** shows the exact path for your install with copy buttons, an MCP
on/off switch and live status (MCP enabled, MCP token file present, last
MCP call). The server runs with Node.js 18+ from `PATH`.

Default locations of the bundled server:

| OS | Path |
|---|---|
| Windows (per-user install) | `%LOCALAPPDATA%\Programs\NKS WebDev Console\resources\mcp-server\index.js` |
| macOS | `/Applications/NKS WebDev Console.app/Contents/Resources/mcp-server/index.js` |
| Linux (AppImage) | `~/.wdc/mcp-server/index.js` — copied there on demand, because the AppImage mount path changes on every launch |

### Claude Code

```bash
claude mcp add --scope user nks-wdc -- node "<WDC resources>/mcp-server/index.js"
```

Options (`--scope`, `-e KEY=value`) go before the server name; `--`
separates the server command so its arguments are not read as
`claude mcp add` flags. Check with `claude mcp list`.

### Claude Desktop

Merge into `~/Library/Application Support/Claude/claude_desktop_config.json`
(macOS) or `%APPDATA%\Claude\claude_desktop_config.json` (Windows), then
restart Claude Desktop:

```json
{
  "mcpServers": {
    "nks-wdc": {
      "command": "node",
      "args": ["<WDC resources>/mcp-server/index.js"]
    }
  }
}
```

### Cursor

Merge the same `mcpServers` entry into `.cursor/mcp.json` in your project
(or `~/.cursor/mcp.json` for every project).

### From a source checkout

```bash
npm install
npm run build        # tsc → dist/index.js
npm run bundle       # esbuild → dist-bundle/index.js (what the installer ships)
claude mcp add --scope user nks-wdc -- node "$(pwd)/dist/index.js"
```

### Enabling MCP

Approval requests (`wdc_request_confirmation`) need MCP enabled in WDC — the
switch on the MCP card, or Settings → Advanced → MCP. While it is off the
daemon answers `404 mcp_disabled` with a hint saying where to turn it on.

### Read-only mode

Append `--readonly` to skip mutation tools (only list/get/metrics):

```bash
claude mcp add --scope user nks-wdc-readonly -- node "<WDC resources>/mcp-server/index.js" --readonly
```

## Tool catalog (72 tools)

### Sites (6)
| Tool | Type |
|---|---|
| `wdc_list_sites` / `wdc_get_site` / `wdc_get_site_metrics` / `wdc_get_site_access_log` | read |
| `wdc_create_site` | mutate |
| `wdc_delete_site` | **destructive** |

### Services (3)
| Tool | Type |
|---|---|
| `wdc_list_services` | read |
| `wdc_start_service` / `wdc_stop_service` | mutate |

### System (3)
| Tool | Type |
|---|---|
| `wdc_get_status` / `wdc_get_system_info` / `wdc_get_recent_activity` | read |

### Databases (6)
| Tool | Type |
|---|---|
| `wdc_list_databases` / `wdc_database_tables` / `wdc_query` | read (SELECT/SHOW/EXPLAIN/DESCRIBE/WITH only) |
| `wdc_create_database` | mutate |
| `wdc_execute` / `wdc_drop_database` | **destructive** |

### SSL (4)
| Tool | Type |
|---|---|
| `wdc_list_certs` | read |
| `wdc_install_ca` / `wdc_generate_cert` | mutate |
| `wdc_revoke_cert` | **destructive** |

### PHP (2)
| Tool | Type |
|---|---|
| `wdc_list_php_versions` | read |
| `wdc_toggle_php_extension` | mutate |

### Binaries (5)
| Tool | Type |
|---|---|
| `wdc_list_catalog` / `wdc_list_installed_binaries` | read |
| `wdc_install_binary` / `wdc_refresh_catalog` | mutate |
| `wdc_uninstall_binary` | **destructive** |

### Plugins (5)
| Tool | Type |
|---|---|
| `wdc_list_plugins` / `wdc_get_plugin_marketplace` | read |
| `wdc_enable_plugin` / `wdc_disable_plugin` / `wdc_install_plugin_from_marketplace` | mutate |

### Backup (3)
| Tool | Type |
|---|---|
| `wdc_list_backups` | read |
| `wdc_create_backup` | mutate |
| `wdc_restore_backup` | **destructive** |

### Settings (2)
| Tool | Type |
|---|---|
| `wdc_get_settings` | read |
| `wdc_update_settings` | mutate |

### Cloudflare (9)
| Tool | Type |
|---|---|
| `wdc_get_cloudflare_config` / `wdc_verify_cloudflare_token` / `wdc_list_cloudflare_zones` / `wdc_list_cloudflare_dns` / `wdc_list_cloudflare_tunnels` / `wdc_get_cloudflare_subdomain_suggestion` | read |
| `wdc_save_cloudflare_config` / `wdc_create_cloudflare_dns` | mutate |
| `wdc_delete_cloudflare_dns` | **destructive** |

### MySQL users (7)
| Tool | Type |
|---|---|
| `wdc_list_mysql_users` | read |
| `wdc_create_mysql_user` / `wdc_set_mysql_user_password` / `wdc_grant_mysql_user_database` / `wdc_change_mysql_root_password` | mutate |
| `wdc_drop_mysql_user` / `wdc_reset_mysql_root_password` | **destructive** |

### Deploy (16, depending on the token's deploy scopes)
| Tool | Type |
|---|---|
| `wdc_deploy_list_targets` / `wdc_deploy_get_status` / `wdc_deploy_list_releases` / `wdc_deploy_get_release` / `wdc_deploy_get_logs` / `wdc_deploy_preflight` / `wdc_deploy_group_status` | read |
| `wdc_deploy_create_intent` / `wdc_deploy_lock` / `wdc_deploy_unlock` | mutate |
| `wdc_deploy_site` / `wdc_deploy_rollback` / `wdc_deploy_cancel` / `wdc_deploy_group_start` / `wdc_deploy_group_rollback` / `wdc_deploy_restore_snapshot` | **destructive** |

### Confirmation (1)
| Tool | Type |
|---|---|
| `wdc_request_confirmation` | mutate — asks the operator to approve a destructive operation |

## Daemon discovery

The MCP server reads the WDC daemon's port + auth token from the first of:

1. `$TMPDIR/nks-wdc-mcp.port`: the MCP token, which the daemon issues for AI clients
2. `$TMPDIR/nks-wdc-daemon.port`: the GUI token, only used with daemons that predate the MCP token
3. `~/.wdc/daemon.port` (legacy fallback)

On Unix a port file is only used when it belongs to the current user and has no group/other permissions.

If the daemon isn't running, every tool call returns:

```
NKS WDC daemon is not running. Start the WDC GUI or run `wdc daemon start`.
```

The token rotates per daemon restart — the MCP server re-reads the port file automatically on the first 401.

## Security

- **Local-only:** stdio transport means no network exposure; the MCP server is a parent-child process under your AI client
- **Bearer token:** every daemon request is auth-gated by the per-session token from the port file
- **Human approval for destructive operations:** with the MCP token, the daemon refuses destructive routes (drop database, SQL that can write, delete site, stop service, revoke certificate, delete DNS record, install binary or plugin, restore backup, deploy/rollback) unless the request carries an intent the user approved in the WDC app. Call `wdc_request_confirmation`, let the user approve the banner, then call the tool again with `intentToken`. The MCP token cannot approve intents, create trust grants, call admin routes or change operator-only settings, and `X-Allow-Unconfirmed` is ignored for it. Read-only SQL through `wdc_query` needs no approval; the daemon checks it independently.
- **Destructive tools:** also require explicit `confirm: "YES"` in the input
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
