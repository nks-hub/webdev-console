# @nks-wdc/mcp-server

MCP (Model Context Protocol) server exposing the **NKS WebDev Console** daemon as AI-callable tools and resources. Lets Claude Desktop, Claude Code, Cursor, and other MCP clients fully manage your local development environment.

> **108 tools across 17 modules** — built on the `McpServer.registerTool()` API with Zod input schemas, tool annotations (`readOnlyHint` / `destructiveHint` / `idempotentHint` / `openWorldHint`), shared response formatting with truncation at 25 KB, split read/write SQL tools (`wdc_query` vs `wdc_execute`), and human approval in the WDC app for destructive operations.

## Connect an AI client

The WDC installer ships this server as one bundled file,
`<WDC resources>/mcp-server/index.js`. In the app, **MCP → Connect an AI
client** shows the exact path for your install with copy buttons, an MCP
on/off switch, live status (MCP enabled, daemon ready, Node.js found, last
MCP call) and a **Test the connection** button that starts the server the
way the AI client will and calls `wdc_get_status`. The server runs with
Node.js 18+ from `PATH`, or without Node.js through the WDC binary itself
(see [Without Node.js](#without-nodejs)).

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

### Without Node.js

The WDC binary runs the server in Node mode when `ELECTRON_RUN_AS_NODE=1`
is set. On Windows the binary asks for administrator rights, which an AI
client cannot grant, so `__COMPAT_LAYER=RunAsInvoker` starts it with the
client's own rights:

```bash
claude mcp add -e ELECTRON_RUN_AS_NODE=1 -e __COMPAT_LAYER=RunAsInvoker --scope user nks-wdc -- "<WDC>/NKS WebDev Console.exe" "<WDC resources>/mcp-server/index.js"
```

```json
{
  "mcpServers": {
    "nks-wdc": {
      "command": "<WDC>/NKS WebDev Console.exe",
      "args": ["<WDC resources>/mcp-server/index.js"],
      "env": { "ELECTRON_RUN_AS_NODE": "1", "__COMPAT_LAYER": "RunAsInvoker" }
    }
  }
}
```

`-e` takes several values, so it goes before another option, never right
before the server name (it would read the name as one more variable).
On macOS and Linux leave out `__COMPAT_LAYER`; for an AppImage the command
is the `.AppImage` file.

### From a source checkout

```bash
npm install
npm run build        # tsc → dist/index.js
npm run bundle       # esbuild → dist-bundle/index.js (what the installer ships)
claude mcp add --scope user nks-wdc -- node "$(pwd)/dist/index.js"
```

### Enabling MCP

Nothing works until MCP is enabled in WDC: the switch on the **AI clients
(MCP)** page or in Settings → AI clients (MCP). While it is off the daemon
refuses every request made with the MCP token (`403 mcp_disabled`, the
`/api/mcp/*` routes answer `404 mcp_disabled`) with a hint saying where to
turn it on. Turning MCP off also cuts an MCP server that is already
running; it works again once MCP is back on.

### Read-only mode

Append `--readonly` to skip mutation tools (only list/get/metrics):

```bash
claude mcp add --scope user nks-wdc-readonly -- node "<WDC resources>/mcp-server/index.js" --readonly
```

## Tool catalog (108 tools)

Read tools are registered in every mode; mutate and destructive tools are left out with `--readonly`. Destructive tools that the daemon guards need an approved `intentToken` (see [Security](#security)); most also take `confirm: "YES"`. Tools marked with a capability only work once the user enables that capability group in WDC → Settings → AI clients (MCP).

### Sites (14)
| Tool | Type |
|---|---|
| `wdc_list_sites` / `wdc_get_site` / `wdc_get_site_metrics` / `wdc_get_site_access_log` / `wdc_get_site_error_log` / `wdc_site_history` | read |
| `wdc_create_site` / `wdc_update_site` / `wdc_apply_site` / `wdc_reapply_all_sites` / `wdc_detect_framework` / `wdc_duplicate_site` | mutate |
| `wdc_delete_site` (intent `site_delete`) / `wdc_site_rollback` | **destructive** |

`wdc_create_site` takes `domain`, `documentRoot`, `phpVersion`, `sslEnabled`, `aliases`, `framework`, `httpPort`, `httpsPort`, `nodeUpstreamPort`, `nodeStartCommand`, `environment` and `phpSettings`, and returns either the SiteInfo or `{ site, warnings, hints }` when an apply step failed or WDC has a suggestion. `wdc_update_site` changes only the fields it is given. Fields beyond `domain`, `documentRoot`, `phpVersion`, `sslEnabled`, `aliases` and `framework` need capability `site_advanced_fields`; a `nodeStartCommand` needs capability `node_process` and intent `node_process`. `wdc_site_rollback` only restores the site's own config history.

### Services (8)
| Tool | Type |
|---|---|
| `wdc_list_services` / `wdc_get_service_logs` / `wdc_get_service_config` | read |
| `wdc_start_service` / `wdc_validate_config` | mutate |
| `wdc_stop_service` / `wdc_restart_service` (intent `service_restart`) / `wdc_update_service_config` (capability `service_config`, intent `service_config_write`) | **destructive** |

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
| `wdc_create_database` / `wdc_export_database` (with `outputPath`: capability `database_admin`) | mutate |
| `wdc_execute` (intent `database_query`) / `wdc_drop_database` (intent `database_drop`) / `wdc_import_database` (capability `database_admin`, intent `database_import`) | **destructive** |

### MySQL users (7)
| Tool | Type |
|---|---|
| `wdc_list_mysql_users` | read |
| `wdc_create_mysql_user` / `wdc_set_mysql_user_password` / `wdc_grant_mysql_user_database` / `wdc_change_mysql_root_password` | mutate |
| `wdc_drop_mysql_user` (intent `mysql_user_drop`) / `wdc_reset_mysql_root_password` (intent `mysql_root_reset`), both capability `database_admin` | **destructive** |

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
| `wdc_composer_install` / `wdc_composer_require` (capability `composer`) | mutate |
| `wdc_composer_remove` (capability `composer`) | **destructive** |

Composer runs the project's and the packages' scripts, so install, require and remove need capability `composer`.

### Node.js sites (4)
| Tool | Type |
|---|---|
| `wdc_list_node_sites` | read |
| `wdc_start_node_site` / `wdc_restart_node_site` (capability `node_process`, intent `node_process`) | mutate |
| `wdc_stop_node_site` (capability `node_process`) | **destructive** |

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
| `wdc_enable_plugin` / `wdc_disable_plugin` / `wdc_install_plugin_from_marketplace` (capability `plugin_install`, intent `plugin_install`) | mutate |
| `wdc_uninstall_plugin` (capability `plugin_install`, intent `plugin_uninstall`) | **destructive** |

### Backup (6)
| Tool | Type |
|---|---|
| `wdc_list_backups` / `wdc_backup_stats` | read |
| `wdc_create_backup` | mutate |
| `wdc_restore_backup` / `wdc_restore_backup_by_id` (intent `backup_restore`) / `wdc_delete_backup` (intent `backup_delete`), all capability `backup_restore` | **destructive** |

### Settings (2)
| Tool | Type |
|---|---|
| `wdc_get_settings` | read |
| `wdc_update_settings` (operator-only keys: capability `settings_operator`; `mcp.*` never) | mutate |

### Cloudflare (9)
| Tool | Type |
|---|---|
| `wdc_get_cloudflare_config` / `wdc_verify_cloudflare_token` / `wdc_list_cloudflare_zones` / `wdc_list_cloudflare_dns` / `wdc_list_cloudflare_tunnels` / `wdc_get_cloudflare_subdomain_suggestion` | read |
| `wdc_save_cloudflare_config` (credential fields: capability `cloudflare_credentials`, intent `cloudflare_credentials`) / `wdc_create_cloudflare_dns` (capability `cloudflare_dns`) | mutate |
| `wdc_delete_cloudflare_dns` (intent `dns_record_delete`) | **destructive** |

### Approvals (2)
| Tool | Type |
|---|---|
| `wdc_get_confirmation_status` | read (whether the user approved an intent yet) |
| `wdc_request_confirmation` | mutate (asks the user to approve one destructive operation in the WDC app) |

### Deploy (16)
Registered per deploy scope from the port file's `scope:` line (`deploy:read`, `deploy:write`, `deploy:admin`; all when absent).

| Tool | Type |
|---|---|
| `wdc_deploy_list_targets` / `wdc_deploy_get_status` / `wdc_deploy_list_releases` / `wdc_deploy_get_release` / `wdc_deploy_get_logs` / `wdc_deploy_list_snapshots` / `wdc_deploy_list_groups` / `wdc_deploy_group_status` | read |
| `wdc_deploy_create_intent` | mutate (asks the user to approve a deploy, rollback, cancel or restore) |
| `wdc_deploy_preflight` (dry run; uses up a deploy intent) | mutate |
| `wdc_deploy_site` / `wdc_deploy_rollback` / `wdc_deploy_cancel` / `wdc_deploy_group_start` / `wdc_deploy_group_rollback` / `wdc_deploy_restore_snapshot` | **destructive**, need an approved `intentToken` |

`wdc_deploy_list_targets` reads the site's deploy settings (which also hold hooks, webhooks and env vars), so it needs capability `deploy_config`. A group deploy is approved with one deploy intent for the first host of the group. Rollback, cancel and snapshot restore can also be requested with `wdc_request_confirmation` (`deploy_rollback`, `deploy_cancel`, `deploy_restore`, target = site domain).

## Daemon discovery

The MCP server reads the WDC daemon's port + MCP token from
`$TMPDIR/nks-wdc-mcp.port`, which the daemon writes on start. It never
reads the GUI token file (`nks-wdc-daemon.port`): that token can approve
intents.

On Unix the port file is only used when it belongs to the current user and has no group/other permissions.

If the daemon isn't running, every tool call returns:

```
NKS WDC daemon is not running. Start the WDC GUI or run `wdc daemon start`.
```

The token rotates per daemon restart — the MCP server re-reads the port file automatically on the first 401.

## Security

- **Local-only:** stdio transport means no network exposure; the MCP server is a parent-child process under your AI client
- **Bearer token:** every daemon request is auth-gated by the per-session token from the port file
- **Human approval for destructive operations:** with the MCP token, the daemon refuses destructive routes (drop database, SQL that can write, import a database, delete site, stop or restart a service, overwrite a service config file, revoke certificate, delete DNS record, install binary or plugin, uninstall plugin, restore or delete a backup, drop a MySQL account or reset its password, create or grant a MySQL account with the `admin` preset or a non-local host, reset or change the MySQL root password, reset the PostgreSQL root password, start a Node.js site process, deploy/rollback) unless the request carries an intent the user approved in the WDC app (or a trust grant the user created covers it). Call `wdc_request_confirmation`, let the user approve the banner, then call the tool again with `intentToken`. The MCP token cannot approve intents, create trust grants or change `mcp.*` settings, and `X-Allow-Unconfirmed` is ignored for it. Read-only SQL through `wdc_query` needs no approval; the daemon checks it independently. Changing the MySQL root password and creating admin or non-local MySQL accounts also need the `database_admin` capability. The table browser takes no filter from the MCP token. Values of argument keys containing `password`, `pwd`, `secret` or `token` are redacted in the audit log.
- **Capabilities the operator enables:** with the MCP token, everyday routes (the ones most tools use, including every read) are always allowed. Riskier areas are capability groups the user enables in WDC → Settings → AI clients (MCP) (`mcp.capabilities.<id>`, all off by default): `settings_operator`, `service_config`, `hosts_file`, `node_process`, `site_advanced_fields`, `cloudflare_credentials`, `cloudflare_dns`, `plugin_install`, `composer`, `database_admin`, `backup_restore`, `deploy_config`, `daemon_admin`, `unlisted_routes`. A disabled group answers 403 `mcp_capability_disabled` with a `hint` naming the setting; tools pass that text on, so tell the user what to enable. An enabled group still needs an approved intent for destructive calls (`wdc_request_confirmation`). `GET /api/mcp/capabilities` lists the groups and their state. `mcp.*` settings, approving intents and managing grants are never available to the MCP token. `daemon-routes.json` lists the daemon routes the server calls; `src/daemonRoutes.test.ts` and the daemon's `McpTokenPolicyTests` keep it, the sources and the daemon's tiers in sync.
- **Destructive tools:** also require explicit `confirm: "YES"` in the input
- **Read-only mode:** `--readonly` flag skips registering mutation tools (they don't appear in `tools/list`)
- **Daemon validation:** all input flows through the daemon's existing validators (`ValidateDomain`, `IsValidDatabaseName`, etc.) — MCP is a thin transport, not the security boundary

## Approvals and errors

1. Call `wdc_request_confirmation` with the operation, its target and a one-sentence reason. The WDC app shows an Approve banner. Pass `waitSeconds` (up to 120) to wait for the decision in the same call.
2. `wdc_get_confirmation_status(intentId)` reports `pending`, `ready`, `consumed`, `expired` or `revoked`, plus `expiresAt`.
3. Once it is `ready`, call the destructive tool with `intentToken`. Each token works once, for exactly that operation and target.

When a trust grant the user created in WDC covers the operation, the daemon approves the intent at once: `wdc_request_confirmation` returns `status: "ready"` with `autoApproved: true` (and `matchedGrantId`), and the tool can be retried right away.

When the daemon refuses a call, the tool error carries the daemon's `error`, `reason`, `detail` and `hint`, plus a `Next step:` line (for example: request a confirmation, wait for approval, or ask the user to enable MCP or an MCP capability in WDC → Settings → AI clients (MCP)). Oversized log results keep the newest entries; other lists keep the first ones. Both mark the result `truncated`.

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
