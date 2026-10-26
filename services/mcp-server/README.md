# @nks-wdc/mcp-server

MCP (Model Context Protocol) server exposing the **NKS WebDev Console** daemon as AI-callable tools and resources. Lets Claude Desktop, Claude Code, Cursor, and other MCP clients fully manage your local development environment.

> **107 tools across 17 modules** — built on the `McpServer.registerTool()` API with Zod input schemas, tool annotations (`readOnlyHint` / `destructiveHint` / `idempotentHint` / `openWorldHint`), shared response formatting with truncation at 25 KB, split read/write SQL tools (`wdc_query` vs `wdc_execute`), and human approval in the WDC app for destructive operations.

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

Restart Claude Desktop. The tools appear in the tool picker.

### Claude Code

Build the server once (`npm install && npm run build` in `services/mcp-server`), then register it for all your projects:

```bash
claude mcp add --scope user nks-wdc node <path>/services/mcp-server/dist/index.js
```

Append `-- --readonly` to register the read-only tool set. `claude mcp list` shows whether it connected.

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

## Tool catalog (107 tools)

Read tools are registered in every mode; mutate and destructive tools are left out with `--readonly`. Destructive tools that the daemon guards need an approved `intentToken` (see [Security](#security)); most also take `confirm: "YES"`.

### Sites (14)
| Tool | Type |
|---|---|
| `wdc_list_sites` / `wdc_get_site` / `wdc_get_site_metrics` / `wdc_get_site_access_log` / `wdc_get_site_error_log` / `wdc_site_history` | read |
| `wdc_create_site` / `wdc_update_site` / `wdc_apply_site` / `wdc_reapply_all_sites` / `wdc_detect_framework` / `wdc_duplicate_site` | mutate |
| `wdc_delete_site` (intent `site_delete`) / `wdc_site_rollback` | **destructive** |

`wdc_create_site` takes `domain`, `documentRoot`, `phpVersion`, `sslEnabled`, `aliases`, `framework`, `httpPort`, `httpsPort`, `nodeUpstreamPort`, `nodeStartCommand`, `environment` and `phpSettings`, and returns either the SiteInfo or `{ site, warnings, hints }` when an apply step failed or WDC has a suggestion. `wdc_update_site` changes only the fields it is given.

### Services (8)
| Tool | Type |
|---|---|
| `wdc_list_services` / `wdc_get_service_logs` / `wdc_get_service_config` | read |
| `wdc_start_service` / `wdc_validate_config` | mutate |
| `wdc_stop_service` / `wdc_restart_service` (intent `service_restart`) / `wdc_update_service_config` (intent `service_config_write`) | **destructive** |

### System and hosts (5)
| Tool | Type |
|---|---|
| `wdc_get_status` / `wdc_get_system_info` / `wdc_get_recent_activity` / `wdc_list_hosts` | read |
| `wdc_flush_dns` | mutate |

### Databases (12)
| Tool | Type |
|---|---|
| `wdc_list_databases` / `wdc_database_tables` / `wdc_database_size` / `wdc_table_columns` / `wdc_table_indexes` / `wdc_browse_table` | read |
| `wdc_query` | read (SELECT/SHOW/EXPLAIN/DESCRIBE/WITH only) |
| `wdc_create_database` / `wdc_export_database` | mutate |
| `wdc_execute` (intent `database_query`) / `wdc_drop_database` (intent `database_drop`) / `wdc_import_database` (intent `database_import`) | **destructive** |

### MySQL users (7)
| Tool | Type |
|---|---|
| `wdc_list_mysql_users` | read |
| `wdc_create_mysql_user` / `wdc_set_mysql_user_password` / `wdc_grant_mysql_user_database` / `wdc_change_mysql_root_password` | mutate |
| `wdc_drop_mysql_user` / `wdc_reset_mysql_root_password` | **destructive** |

### SSL (4)
| Tool | Type |
|---|---|
| `wdc_list_certs` | read |
| `wdc_install_ca` / `wdc_generate_cert` | mutate |
| `wdc_revoke_cert` (intent `ssl_cert_delete`) | **destructive** |

### PHP (3)
| Tool | Type |
|---|---|
| `wdc_list_php_versions` / `wdc_list_php_extensions` | read |
| `wdc_toggle_php_extension` | mutate |

Each site carries its own PHP version; change it with `wdc_update_site`.

### Composer (5)
| Tool | Type |
|---|---|
| `wdc_composer_status` / `wdc_composer_outdated` | read |
| `wdc_composer_install` / `wdc_composer_require` | mutate |
| `wdc_composer_remove` | **destructive** |

### Node.js sites (4)
| Tool | Type |
|---|---|
| `wdc_list_node_sites` | read |
| `wdc_start_node_site` / `wdc_restart_node_site` | mutate |
| `wdc_stop_node_site` | **destructive** |

### Binaries (5)
| Tool | Type |
|---|---|
| `wdc_list_catalog` / `wdc_list_installed_binaries` | read |
| `wdc_refresh_catalog` / `wdc_install_binary` (intent `binary_install`) | mutate |
| `wdc_uninstall_binary` | **destructive** |

### Plugins (6)
| Tool | Type |
|---|---|
| `wdc_list_plugins` / `wdc_get_plugin_marketplace` | read |
| `wdc_enable_plugin` / `wdc_disable_plugin` / `wdc_install_plugin_from_marketplace` (intent `plugin_install`) | mutate |
| `wdc_uninstall_plugin` (intent `plugin_uninstall`) | **destructive** |

### Backup (6)
| Tool | Type |
|---|---|
| `wdc_list_backups` / `wdc_backup_stats` | read |
| `wdc_create_backup` | mutate |
| `wdc_restore_backup` / `wdc_restore_backup_by_id` (intent `backup_restore`) / `wdc_delete_backup` | **destructive** |

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
| `wdc_delete_cloudflare_dns` (intent `dns_record_delete`) | **destructive** |

### Deploy (16)
Registered per deploy scope from the port file's `scope:` line (`deploy:read`, `deploy:write`, `deploy:admin`; all when absent).

| Tool | Type |
|---|---|
| `wdc_deploy_list_targets` / `wdc_deploy_get_status` / `wdc_deploy_list_releases` / `wdc_deploy_get_release` / `wdc_deploy_get_logs` / `wdc_deploy_group_status` | read |
| `wdc_deploy_create_intent` / `wdc_deploy_preflight` / `wdc_deploy_lock` / `wdc_deploy_unlock` | mutate |
| `wdc_deploy_site` / `wdc_deploy_rollback` / `wdc_deploy_cancel` / `wdc_deploy_group_start` / `wdc_deploy_group_rollback` / `wdc_deploy_restore_snapshot` | **destructive** |

### Approvals (1)
| Tool | Type |
|---|---|
| `wdc_request_confirmation` | mutate (asks the user to approve one destructive operation in the WDC app) |

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
- **Human approval for destructive operations:** with the MCP token, the daemon refuses destructive routes (drop database, SQL that can write, import a database, delete site, stop or restart a service, overwrite a service config file, revoke certificate, delete DNS record, install binary or plugin, uninstall plugin, restore backup, deploy/rollback) unless the request carries an intent the user approved in the WDC app. Call `wdc_request_confirmation`, let the user approve the banner, then call the tool again with `intentToken`. The MCP token cannot approve intents, create trust grants, call admin routes or change operator-only settings, and `X-Allow-Unconfirmed` is ignored for it. Read-only SQL through `wdc_query` needs no approval; the daemon checks it independently.
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
