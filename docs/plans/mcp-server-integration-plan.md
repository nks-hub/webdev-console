# NKS WebDev Console — MCP Server Integration Plan

**Date:** 2026-04-13
**Status:** Plan / Analysis (no implementation yet)
**Goal:** Expose the entire NKS WDC daemon as an MCP (Model Context Protocol) server so AI assistants (Claude Desktop, Claude Code, Cursor, etc.) can fully manage the local dev environment — analyze, run, create, read, write — without going through the GUI or CLI.

---

## 1. Why MCP

Today NKS WDC has three control surfaces:

| Surface | Audience | Limits |
|---|---|---|
| **Electron GUI** | Humans clicking | Slow for bulk ops, no scripting |
| **`wdc` CLI** | Humans + shell scripts | Imperative, no structured returns, one-shot |
| **REST API** | Programmatic clients | Custom auth, no AI discovery, no schemas |

An **MCP server** adds a fourth surface: **AI agents** with structured tool discovery, typed inputs/outputs, and built-in capability negotiation. An AI assistant connected to the WDC MCP server can:

- "Create a new Laravel site at api.myapp.loc with PHP 8.3 and SSL" → one tool call
- "Why is my Apache instance crashing?" → fetch logs + analyze + suggest fix
- "Migrate all my MAMP sites" → discover + import + report
- "Backup before this risky operation" → call `create_backup` then proceed
- "Show me sites with Cloudflare tunnels expiring soon" → query metadata + format

This is **not** a replacement for GUI/CLI/REST — it's an additional consumer that benefits from the existing daemon's REST contracts (the underlying truth) while presenting them in MCP's tool/resource/prompt model.

---

## 2. MCP Protocol Primer (relevant subset)

The Model Context Protocol (Anthropic, open spec) defines three primitives:

| Primitive | Purpose | NKS WDC mapping |
|---|---|---|
| **Tools** | Function calls with typed inputs + outputs | All daemon mutation endpoints (`create_site`, `start_service`, `install_binary`, `apply_config_validation`, …) |
| **Resources** | Read-only addressable content (URIs) | Site TOML files, vhost configs, access logs, php.ini files, certificates, plugin manifests |
| **Prompts** | Reusable prompt templates with parameters | "Diagnose service crash", "Plan a stack upgrade", "Audit hosts file" |

Transports:
- **stdio** — process-to-process, used by Claude Desktop, Claude Code, Cursor. Default for local tools.
- **HTTP+SSE** — for remote/cloud-hosted MCP servers. Not needed initially.

Discovery: an MCP client calls `tools/list`, `resources/list`, `prompts/list` at startup. The server returns JSON Schema for each tool's input + a description for the AI to reason about when to call it.

---

## 3. Architecture Options

### Option A — Embedded MCP server in daemon (C# implementation)

The daemon already runs as a long-lived HTTP server with REST endpoints, auth middleware, and DI. Adding a second listener that speaks MCP-over-HTTP would reuse all existing infrastructure.

**Pros:**
- Single process to manage (already running)
- Shares auth token with REST clients (no separate credential)
- Direct access to `IServiceProvider`, `SiteManager`, `BinaryManager` — no IPC overhead
- Adding a tool = adding a method, just like adding a REST endpoint
- Distributed with the daemon binary, no extra install

**Cons:**
- C# MCP SDK is less mature than TypeScript/Python
- Adds protocol surface to the daemon (more attack surface — but already auth-gated)
- HTTP+SSE transport, doesn't fit Claude Desktop's stdio default out of the box
- Mixes "human REST API" and "AI tool API" in one process — versioning gets harder

### Option B — Sidecar MCP server in TypeScript/Python (HTTP client to daemon)

A separate process (e.g., `services/mcp-server/`) speaks MCP-over-stdio to the AI client and translates each tool call into a REST request to the daemon's existing `/api/*` endpoints.

**Pros:**
- TypeScript MCP SDK is the official reference implementation, full feature parity
- Stdio transport is the default for desktop AI clients (zero config in claude_desktop_config.json)
- Decouples MCP versioning from daemon versioning — can iterate independently
- Daemon REST contract is the only source of truth — tested separately
- Easy to ship as `npx @nks-wdc/mcp-server` for instant install

**Cons:**
- Two processes to manage (mcp-server + daemon)
- Needs to discover the daemon's port + auth token from `~/.wdc/daemon.port` (the same way `wdc` CLI does)
- Each tool call is HTTP overhead vs in-process method call (negligible for local — < 5ms)
- Adds a new language to the repo (TypeScript or Python)

### Option C — Hybrid: thin C# MCP transport + daemon-internal tool registry

The daemon hosts an MCP transport layer (e.g., websocket or HTTP+SSE) that speaks the MCP protocol but the actual tool implementations are direct calls into daemon services. This is essentially Option A with a clean separation of "transport" vs "tool implementation".

**Pros / Cons:** combines A's tight integration with explicit modularity. More code than either pure option.

### Recommendation: **Option B (TypeScript sidecar)**

Reasons:
1. Stdio MCP is what end-users will configure — `claude_desktop_config.json` expects a command, not a URL
2. TypeScript SDK from Anthropic is the reference implementation — minimal protocol risk
3. Daemon REST API is already feature-complete (94 endpoints), no daemon changes needed
4. Independent versioning lets the MCP server evolve without daemon redeploys
5. Same pattern other dev tools use (Postgres MCP, Brave Search MCP, Filesystem MCP — all sidecars)

---

## 4. Implementation Plan — Sidecar MCP Server (Option B)

### 4.1 Project layout

```
services/
├── catalog-api/          (existing Python sidecar)
└── mcp-server/           (new — TypeScript)
    ├── package.json
    ├── tsconfig.json
    ├── src/
    │   ├── index.ts          (stdio entry + MCP server bootstrap)
    │   ├── daemonClient.ts   (HTTP client → daemon REST, port-file discovery)
    │   ├── tools/
    │   │   ├── sites.ts      (create_site, list_sites, update_site, delete_site, …)
    │   │   ├── services.ts   (start, stop, restart, status)
    │   │   ├── databases.ts  (create_db, drop_db, query, import, export)
    │   │   ├── ssl.ts        (install_ca, generate_cert, revoke_cert, list_certs)
    │   │   ├── php.ts        (list_versions, set_default, toggle_extension)
    │   │   ├── binaries.ts   (catalog, install, uninstall, list_installed)
    │   │   ├── plugins.ts    (list, enable, disable, install_marketplace)
    │   │   ├── backup.ts     (create, list, restore, schedule)
    │   │   ├── cloudflare.ts (config, verify_token, list_zones, dns_*, tunnels)
    │   │   ├── settings.ts   (get, put, sync_push, sync_pull, sync_export)
    │   │   ├── activity.ts   (recent activity, history)
    │   │   └── metrics.ts    (live metrics, history time-series)
    │   ├── resources/
    │   │   ├── siteToml.ts        (wdc://sites/{domain}.toml)
    │   │   ├── vhostConfig.ts     (wdc://vhosts/{domain}.conf)
    │   │   ├── accessLog.ts       (wdc://logs/{domain}/access)
    │   │   ├── errorLog.ts        (wdc://logs/{domain}/error)
    │   │   ├── phpIni.ts          (wdc://php/{version}/php.ini)
    │   │   └── certificate.ts     (wdc://ssl/{domain})
    │   ├── prompts/
    │   │   ├── diagnoseService.ts (template for "service X keeps crashing")
    │   │   ├── auditSites.ts      (template for "audit my sites for issues")
    │   │   ├── upgradePhp.ts      (template for "plan a PHP upgrade")
    │   │   └── newProject.ts      (template for "scaffold a new dev project")
    │   └── schemas/               (Zod schemas reused for input validation)
    ├── tests/
    │   └── tools.spec.ts          (vitest)
    └── README.md
```

### 4.2 Tool catalog (mapping daemon REST → MCP tools)

**Sites (12 tools)**
- `wdc_list_sites` — GET `/api/sites`
- `wdc_get_site` — GET `/api/sites/{domain}`
- `wdc_create_site` — POST `/api/sites` (params: domain, documentRoot, phpVersion, sslEnabled, aliases, framework, …)
- `wdc_update_site` — PUT `/api/sites/{domain}`
- `wdc_delete_site` — DELETE `/api/sites/{domain}`
- `wdc_detect_framework` — POST `/api/sites/{domain}/detect-framework`
- `wdc_reapply_all_sites` — POST `/api/sites/reapply-all`
- `wdc_discover_mamp` — GET `/api/sites/discover-mamp`
- `wdc_migrate_mamp` — POST `/api/sites/migrate-mamp`
- `wdc_site_history` — GET `/api/sites/{domain}/history`
- `wdc_site_rollback` — POST `/api/sites/{domain}/rollback/{timestamp}`
- `wdc_site_metrics` — GET `/api/sites/{domain}/metrics` (live + history)

**Services (6 tools)**
- `wdc_list_services`, `wdc_get_service`, `wdc_start_service`, `wdc_stop_service`, `wdc_restart_service`, `wdc_get_service_logs`

**Databases (7 tools)**
- `wdc_list_databases`, `wdc_create_database`, `wdc_drop_database`, `wdc_database_tables`, `wdc_database_size`, `wdc_query`, `wdc_export_database`, `wdc_import_database`

**SSL (4 tools)**
- `wdc_list_certs`, `wdc_install_ca`, `wdc_generate_cert`, `wdc_revoke_cert`

**PHP (4 tools)**
- `wdc_list_php_versions`, `wdc_set_default_php`, `wdc_list_php_extensions`, `wdc_toggle_php_extension`

**Binaries (5 tools)**
- `wdc_list_catalog`, `wdc_list_installed_binaries`, `wdc_install_binary`, `wdc_uninstall_binary`, `wdc_refresh_catalog`

**Plugins (5 tools)**
- `wdc_list_plugins`, `wdc_enable_plugin`, `wdc_disable_plugin`, `wdc_get_plugin_marketplace`, `wdc_install_plugin_from_marketplace`

**Backup (4 tools)**
- `wdc_create_backup`, `wdc_list_backups`, `wdc_restore_backup`, `wdc_set_backup_schedule`

**Cloudflare (10 tools)**
- `wdc_get_cloudflare_config`, `wdc_save_cloudflare_config`, `wdc_verify_cloudflare_token`, `wdc_list_zones`, `wdc_list_dns_records`, `wdc_create_dns_record`, `wdc_delete_dns_record`, `wdc_list_tunnels`, `wdc_get_tunnel_config`, `wdc_update_tunnel_ingress`, `wdc_cloudflare_auto_setup`

**Settings + Sync (8 tools)**
- `wdc_get_settings`, `wdc_update_settings`, `wdc_sync_push`, `wdc_sync_pull`, `wdc_sync_export`, `wdc_sync_import`, `wdc_list_devices`, `wdc_push_config_to_device`

**System (5 tools)**
- `wdc_get_system_info`, `wdc_get_status`, `wdc_get_recent_activity`, `wdc_validate_config`, `wdc_uninstall_wdc`

**Total: ~75 tools**

### 4.3 Resources (read-only addressable content)

URI scheme: `wdc://` with hierarchical paths.

- `wdc://sites/` — list all site TOMLs
- `wdc://sites/{domain}.toml` — site config TOML content
- `wdc://vhosts/{domain}.conf` — generated Apache vhost
- `wdc://logs/{domain}/access?lines=100` — tail of access log
- `wdc://logs/{domain}/error?lines=100` — tail of error log
- `wdc://php/{version}/php.ini` — php.ini for a specific version
- `wdc://ssl/{domain}` — certificate file content
- `wdc://services/{id}/log?lines=200` — service-specific log
- `wdc://settings` — full key/value dump
- `wdc://plugins/{id}/manifest` — plugin.json content

The MCP client (Claude Desktop / Cursor) can read these via the `resources/read` protocol message and feed them into the LLM's context.

### 4.4 Prompts (reusable templates)

Each prompt is a parameterized template the AI can invoke when the user asks a related question. The MCP server returns the rendered prompt for the LLM to follow.

Examples:
- **`diagnose_service_crash(serviceId)`** — fetch logs + status + metrics + recent commits, present as structured diagnosis input
- **`audit_sites()`** — list all sites + their SSL status + framework + last-modified, format as audit table
- **`plan_php_upgrade(fromVersion, toVersion)`** — list all sites using fromVersion, check for incompatible extensions, suggest migration steps
- **`scaffold_project(framework, domain)`** — combine site creation + template + database + DNS in one flow

### 4.5 Daemon discovery

Same pattern as `wdc` CLI: read `~/.wdc/daemon.port` for `port\ntoken`. If the file doesn't exist, the daemon isn't running — return a tool error with instructions ("start the WDC GUI or run `wdc daemon start`").

```ts
// daemonClient.ts
function readPortFile(): { port: number, token: string } | null {
  const path = join(homedir(), '.wdc', 'daemon.port')
  if (!existsSync(path)) return null
  const [portStr, token] = readFileSync(path, 'utf8').trim().split('\n')
  return { port: parseInt(portStr), token }
}
```

### 4.6 Auth flow

- MCP server reads daemon port file at startup (and on reconnect)
- Every HTTP request to daemon includes `Authorization: Bearer {token}`
- Token rotates per daemon restart — server detects token mismatch (401) and re-reads the port file

### 4.7 Error handling

All daemon HTTP errors map to MCP tool errors with the response body's `detail` / `error` field as the message. The error-surfacing pattern from the recent daemon endpoint robustness sweep already returns structured `{ error, detail }` JSON for every failure path.

```ts
async function call(path: string, init: RequestInit) {
  const r = await fetch(`http://127.0.0.1:${port}${path}`, {
    ...init,
    headers: { ...init.headers, Authorization: `Bearer ${token}` },
  })
  if (!r.ok) {
    const body = await r.json().catch(() => ({}))
    throw new McpError(ErrorCode.InternalError, body.error || body.detail || `HTTP ${r.status}`)
  }
  return r.json()
}
```

### 4.8 Capability levels (read-only vs mutation)

The MCP server should optionally start in **read-only mode** (e.g., `--readonly` flag) which exposes only `wdc_list_*`, `wdc_get_*`, and `wdc_*_metrics` tools. This lets users connect Claude Desktop in observe-only mode for sensitive environments while still letting the AI inspect state.

Tools split:
- **Always-on (read):** ~30 list/get/metrics tools
- **Mutation (gated by `--readonly` flag):** ~45 create/update/delete tools
- **Destructive (require explicit confirmation in tool input):** `wdc_uninstall_wdc`, `wdc_drop_database`, `wdc_delete_site`, `wdc_revoke_cert`, `wdc_restore_backup` — these tools take a `confirm: "YES"` field that the AI must explicitly set

### 4.9 Installation UX

The user adds NKS WDC MCP to Claude Desktop by editing `claude_desktop_config.json`:

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

That's it. `npx` downloads + caches the package, runs the stdio entry, MCP handshake completes. Claude Desktop discovers the ~75 tools at startup.

For Claude Code: same shape in `~/.claude.json` `mcpServers` section.

For Cursor: matching `.cursor/mcp.json` syntax.

### 4.10 Distribution

- Publish to npm as `@nks-wdc/mcp-server`
- GitHub Action publishes on `npm-publish` workflow trigger
- Version pinned to compatible daemon range (semver minor lock)
- Smoke test: spin up daemon → MCP server stdio → call `wdc_get_status` → expect `{ status: "running" }`

---

## 5. Schema design — illustrative tools

### Tool: `wdc_create_site`

```json
{
  "name": "wdc_create_site",
  "description": "Create a new local development site. The daemon generates the Apache vhost, hosts file entry, optional SSL certificate, and writes the TOML config. Returns the created site object.",
  "inputSchema": {
    "type": "object",
    "required": ["domain", "documentRoot"],
    "properties": {
      "domain": {
        "type": "string",
        "pattern": "^[a-z0-9][a-z0-9.-]*\\.[a-z]{2,}$",
        "description": "Local domain like 'myapp.loc'. Must end in a TLD."
      },
      "documentRoot": {
        "type": "string",
        "description": "Absolute filesystem path to the site's web root."
      },
      "phpVersion": {
        "type": "string",
        "description": "PHP major.minor (e.g. '8.3'). Empty for static or Node sites.",
        "default": ""
      },
      "sslEnabled": { "type": "boolean", "default": false },
      "aliases": {
        "type": "array",
        "items": { "type": "string" },
        "description": "ServerAlias entries — supports leading wildcard like '*.myapp.loc'."
      },
      "framework": {
        "type": "string",
        "enum": ["wordpress", "laravel", "nette", "symfony", "nextjs", "node", "static", ""],
        "description": "Framework hint. Leave empty for auto-detection from documentRoot."
      },
      "nodeUpstreamPort": {
        "type": "integer",
        "minimum": 0,
        "description": "When non-zero, Apache reverse-proxies to localhost:{port} instead of serving DocumentRoot."
      }
    }
  }
}
```

### Tool: `wdc_query` (database)

```json
{
  "name": "wdc_query",
  "description": "Execute a SQL query against a local MySQL database via the daemon. Returns rows as a typed array. The daemon validates the database name against [a-zA-Z0-9_]+ to prevent injection in CLI args.",
  "inputSchema": {
    "type": "object",
    "required": ["database", "sql"],
    "properties": {
      "database": { "type": "string", "pattern": "^[a-zA-Z0-9_]+$" },
      "sql": { "type": "string", "maxLength": 65535 }
    }
  }
}
```

### Resource: `wdc://sites/myapp.loc.toml`

```toml
domain = "myapp.loc"
documentRoot = "C:\\work\\myapp"
phpVersion = "8.3"
sslEnabled = true
aliases = ["www.myapp.loc"]
framework = "laravel"
```

### Prompt: `diagnose_service_crash`

```
The service "{serviceId}" has crashed. Use the available wdc tools to:

1. Call wdc_get_service to confirm current state
2. Read wdc://services/{serviceId}/log?lines=200 for the last log lines
3. Call wdc_get_recent_activity to see what configuration changes happened recently
4. Look for these patterns in logs:
   - "address already in use" → port conflict (suggest wdc_get_system_info to find another port)
   - "permission denied" → file ownership issue
   - "config error" → call wdc_validate_config

Present the root cause and a single recommended fix as a tool call.
```

---

## 6. Security considerations

| Risk | Mitigation |
|---|---|
| **Token leak via stdio** | MCP stdio is parent-child only; no network exposure. Token never leaves the local machine. |
| **Path traversal in resource URIs** | Server validates URI segments against the same regex the daemon uses (e.g. domain `^[a-z0-9.-]+$`) before forwarding to REST |
| **Destructive tools called accidentally** | All destructive tools require `confirm: "YES"` in input; AI must explicitly set it — Claude's safety training will refuse without explicit user approval |
| **Tool input bypassing daemon validation** | All inputs are passed through to daemon REST which has its own validation layer (ValidateDomain, IsValidDatabaseName, etc.). MCP is a thin transport, not a security boundary. |
| **Read-only mode bypass** | When `--readonly` flag is set, mutation tools are simply not registered with the MCP server — they don't appear in `tools/list` and can't be called |
| **Arbitrary SQL via wdc_query** | The daemon's existing query endpoint already restricts to a single database scope, requires daemon-managed root password (commit `1ac8822`), and runs each query in a separate mysql process. MCP just forwards. |
| **Host's MCP client compromised** | If the AI client itself is malicious, the entire stdio session is compromised. MCP doesn't defend against this — it's a trust chain. User's responsibility to use trusted AI clients. |

---

## 7. Phased implementation

### Phase A — MVP (1 week)
- Project scaffold + TypeScript + MCP SDK setup
- Daemon discovery via port file
- 10 most-used tools: `wdc_list_sites`, `wdc_get_site`, `wdc_create_site`, `wdc_delete_site`, `wdc_list_services`, `wdc_start_service`, `wdc_stop_service`, `wdc_get_status`, `wdc_get_recent_activity`, `wdc_query`
- Basic resource: `wdc://sites/{domain}.toml`
- Manual install via `claude_desktop_config.json`

### Phase B — Full coverage (2 weeks)
- All ~75 tools mapped
- All ~10 resources implemented
- 4 prompts (diagnose, audit, upgrade, scaffold)
- vitest suite that mocks daemon HTTP and verifies tool invocation flow
- README with installation steps for Claude Desktop, Claude Code, Cursor

### Phase C — Polish & distribution (1 week)
- npm publish workflow (GitHub Action)
- E2E smoke test: real daemon + MCP server + tool calls
- Read-only mode flag
- Destructive-confirm guards on `wdc_uninstall_wdc`, `wdc_drop_database`, etc.
- Auto-reconnect on daemon restart (token rotation)

### Phase D — Stretch (post-MVP)
- HTTP+SSE transport for remote/cloud-hosted MCP server (optional second binary)
- Structured tool output schemas (currently MCP returns JSON strings; v0.3+ supports typed outputs)
- Prompt argument auto-completion via daemon (e.g., domain list for `diagnose_service_crash`)
- Telemetry: per-tool invocation count to MCP-side log

---

## 8. Open questions

1. **Resource caching:** should the MCP server cache resource reads (e.g., site TOML) for N seconds to reduce daemon load when AI fetches the same file multiple times? Trade-off: stale data vs daemon roundtrip per read.
2. **Streaming logs:** the daemon has a WebSocket log streamer. MCP doesn't natively support streaming resources yet (as of MCP v0.2). Should we offer `wdc_tail_log` as a tool that returns the last N lines on each call instead?
3. **Multi-daemon support:** if the user has multiple WDC instances (test + prod), should the MCP server accept a `--daemon-port-file` argument to point at a non-default port file? Or one MCP server per daemon?
4. **Plugin tools:** plugins can already register their own daemon endpoints. Should plugins also register MCP tools? Would need a plugin manifest field like `mcpTools: [...]`.
5. **Tool naming convention:** prefix every tool with `wdc_` (current proposal) or use namespaced naming like `wdc.sites.create`? MCP allows both but Claude renders them differently in the tool picker UI.
6. **Confirmation pattern:** explicit `confirm: "YES"` in input vs MCP's `humanInTheLoop` capability (still draft spec)?

---

## 9. Effort estimate

| Phase | Effort | Coverage |
|---|---|---|
| Phase A — MVP | 1 dev-week | 10 tools, 1 resource, no prompts |
| Phase B — Full | 2 dev-weeks | 75 tools, 10 resources, 4 prompts |
| Phase C — Polish | 1 dev-week | npm publish, smoke tests, read-only mode |
| **Total** | **~4 weeks** | Full daemon parity over MCP |

---

## 10. Decision needed

Before starting Phase A:

1. ✅ **Stack:** TypeScript sidecar (Option B) — confirmed
2. ❓ **Tool naming:** `wdc_*` flat or `wdc.*.*` namespaced
3. ❓ **Resource scope:** include log files (potentially large) or only metadata
4. ❓ **Distribution:** npm public package or GitHub-only release
5. ❓ **Read-only default:** start in read-only mode with a flag to enable mutations, or vice versa
6. ❓ **Repo location:** `services/mcp-server/` (current repo) or new `nks-wdc-mcp` repo (independent versioning)

Once these are decided, Phase A can start with the MVP scaffold + first 10 tools. Phase B + C can run in parallel sub-sessions per the autonomous loop pattern.
