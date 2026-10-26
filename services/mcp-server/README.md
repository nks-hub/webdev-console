# @nks-wdc/mcp-server

MCP (Model Context Protocol) server exposing the **NKS WebDev Console** daemon as AI-callable tools and resources. Lets Claude Desktop, Claude Code, Cursor, and other MCP clients fully manage your local development environment.

> **48 tools across 11 modules** — built on the modern `McpServer.registerTool()` API with Zod input schemas, honest tool annotations (`readOnlyHint` / `destructiveHint` / `idempotentHint` / `openWorldHint`), shared response formatting with JSON-envelope truncation at 25 KB, split read/write SQL tools (`wdc_query` vs `wdc_execute`), and non-empty credential schemas to prevent accidental wipes. See [`docs/plans/mcp-server-integration-plan.md`](../../docs/plans/mcp-server-integration-plan.md) for the full roadmap.

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

Restart Claude Desktop. All 48 tools appear in the tool picker.

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

## Tool catalog (48 tools)

### Sites (5)
| Tool | Type |
|---|---|
| `wdc_list_sites` / `wdc_get_site` / `wdc_get_site_metrics` | read |
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

### PHP (3)
| Tool | Type |
|---|---|
| `wdc_list_php_versions` | read |
| `wdc_set_default_php` / `wdc_toggle_php_extension` | mutate |

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

### Deploy (16)
Registered per deploy scope from the port file's `scope:` line (`deploy:read`, `deploy:write`, `deploy:admin`; all when absent).

| Tool | Type |
|---|---|
| `wdc_deploy_list_targets` / `wdc_deploy_get_status` / `wdc_deploy_list_releases` / `wdc_deploy_get_release` / `wdc_deploy_get_logs` / `wdc_deploy_list_snapshots` / `wdc_deploy_list_groups` / `wdc_deploy_group_status` | read |
| `wdc_deploy_create_intent` | mutate (asks the user to approve a deploy, rollback, cancel or restore) |
| `wdc_deploy_preflight` (dry run; uses up a deploy intent) | mutate |
| `wdc_deploy_site` / `wdc_deploy_rollback` / `wdc_deploy_cancel` / `wdc_deploy_group_start` / `wdc_deploy_group_rollback` / `wdc_deploy_restore_snapshot` | **destructive**, need an approved `intentToken` |

A group deploy is approved with one deploy intent for the first host of the group. Rollback, cancel and snapshot restore can also be requested with `wdc_request_confirmation` (`deploy_rollback`, `deploy_cancel`, `deploy_restore`, target = site domain).

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
