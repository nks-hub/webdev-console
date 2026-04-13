# @nks-wdc/mcp-server

MCP (Model Context Protocol) server exposing the **NKS WebDev Console** daemon as AI-callable tools and resources. Lets Claude Desktop, Claude Code, Cursor, and other MCP clients fully manage your local development environment.

> **48 tools across 11 modules** — built on the modern `McpServer.registerTool()` API with Zod input schemas, tool annotations (`readOnlyHint` / `destructiveHint` / `idempotentHint` / `openWorldHint`), shared response formatting with 25KB truncation, and per-tool `response_format` switch (`json` / `markdown`). See [`docs/plans/mcp-server-integration-plan.md`](../../docs/plans/mcp-server-integration-plan.md) for the full roadmap.

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

### Databases (5)
| Tool | Type |
|---|---|
| `wdc_list_databases` / `wdc_database_tables` | read |
| `wdc_create_database` / `wdc_query` | mutate |
| `wdc_drop_database` | **destructive** |

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
