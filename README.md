# NKS WebDev Console

[![Build & Release](https://github.com/nks-hub/webdev-console/actions/workflows/build.yml/badge.svg)](https://github.com/nks-hub/webdev-console/actions/workflows/build.yml)

Local development environment for **Windows, macOS, and Linux** — manages Apache,
PHP, MySQL/MariaDB, SSL, mail catching, Redis, Node, Cloudflare tunnels and DNS
for `.local` / `.test` domains. Replacement for MAMP PRO, XAMPP, WampServer,
Laragon and similar tools.

> **Status:** v0.2.3 — actively developed. Most features are working; some
> Phase 11 items (Nginx plugin, PostgreSQL plugin, RBAC, Docker Compose support)
> are still on the roadmap.

## Highlights

- **One daemon, many plugins** — Apache, PHP, MySQL, SSL (mkcert), Mailpit,
  Redis, Node, Caddy, Cloudflare and a hosts-file editor are loaded from
  separate plugin DLLs at runtime.
- **Per-site PHP** — each site can pin its own PHP version, INI overrides
  (memory, timeouts, upload limits, Xdebug, OPcache, extensions) and Apache
  Fcgid timeouts.
- **MAMP PRO migration** — built-in importer reads MAMP PRO's SQLite database,
  recreates virtual hosts, copies SSL certs and migrates databases from MAMP's
  MySQL (custom port supported).
- **Three ways to drive it** — Electron GUI, `wdc` CLI, or REST API
  (Bearer-token auth, port + token written to `~/.wdc/daemon.port` /
  `%TEMP%\nks-wdc-daemon.port`).
- **MCP server** — 47 tools that let Claude Code (or any MCP client) read and
  control your local stack: list sites, create databases, query SQL, tail
  access logs, toggle services, install PHP versions, manage Cloudflare
  tunnels, etc.
- **Cross-platform packaging** — single portable executable on Windows,
  signed `.dmg` + `.zip` on macOS (Apple Silicon), AppImage on Linux. Daemon
  is published self-contained — no separate .NET runtime needed.

## Repository layout

```
nks-ws/
├── src/
│   ├── daemon/                    # C# .NET 9 — REST API, plugin host, CLI
│   │   ├── NKS.WebDevConsole.Daemon       # ASP.NET Core service
│   │   ├── NKS.WebDevConsole.Core         # shared services + models
│   │   ├── NKS.WebDevConsole.Cli          # `wdc` System.CommandLine binary
│   │   └── NKS.WebDevConsole.Plugin.SDK   # plugin contract
│   ├── plugins/                   # 10 plugin projects (Apache, PHP, …)
│   └── frontend/                  # Electron 34 + Vue 3 + Element Plus + Tailwind v4
├── services/
│   ├── catalog-api/               # Python FastAPI sidecar — binary/version catalog
│   └── mcp-server/                # TypeScript stdio MCP server (47 tools)
├── tests/                         # xUnit tests for Daemon + Core
├── scripts/                       # build helpers (stage-plugins, verify-release, …)
├── prototype/                     # exploratory spike code (database, dns, ssl)
├── docs/                          # internal status/plan docs (NOT user docs)
├── WebDevConsole.sln              # full Visual Studio solution
└── .github/workflows/             # CI matrix — Windows, macOS, Linux
```

### Related repositories

NKS WebDev Console ships as four co-operating repositories. Pushes to this
repo's `v*` tag fan out to the other three via workflows.

| Repo | Purpose | Triggered how |
|------|---------|---------------|
| [`nks-hub/webdev-console-plugins`](https://github.com/nks-hub/webdev-console-plugins) | Extracted plugin csprojs (13 plugins) | consumes SDK `.nupkg` published here on every `v*` tag; own `auto-release.yml` cuts per-plugin releases when `plugin.json.version` bumps |
| [`nks-hub/wdc-catalog-api`](https://github.com/nks-hub/wdc-catalog-api) | FastAPI backend (binaries + plugins catalog, SSO, admin UI) | daemon pulls `/api/v1/plugins/catalog` + `/api/v1/catalog` at runtime (F95 auto-sync) |
| [`nks-hub/webdev-console-binaries`](https://github.com/nks-hub/webdev-console-binaries) | Versioned runtime binaries (PHP, Apache, Nginx, Caddy, Mailpit, cloudflared) | mirrored into catalog-api; daemon `BinaryManager` installs from these releases |

### Release train

A single `v0.x.y` tag push to this repo fires in parallel:

1. **Build & Release** (`build.yml`) — Windows/macOS/Linux installers + GH
   Release + `latest.yml` for the auto-updater.
2. **Publish Plugin SDK** (`publish-sdk.yml`) — packs `NKS.WebDevConsole.Plugin.SDK`
   with `PackageVersion=<tag>`, pushes to `nuget.pkg.github.com/nks-hub`, and
   attaches the same `.nupkg` to the GH Release as an asset so cross-repo
   consumers can fetch it without a PAT.
3. **Defender Submission** + **Release Security Scan** — Windows SmartScreen
   false-positive queue + VirusTotal pre-release scan, wired via `workflow_run`.

## Building from source

The only supported install method right now is building from source. There is
no Homebrew tap, winget package or apt repository — distribution channels are
on the Phase 12 roadmap.

### Prerequisites

| Tool       | Version | Notes                                                   |
| ---------- | ------- | ------------------------------------------------------- |
| .NET SDK   | 9.0+    | macOS: `brew install dotnet@9`                          |
| Node.js    | 20 LTS+ | Tested with 22 (CI) and 23.x                            |
| Python     | 3.11+   | Optional — only needed if you want the catalog-api      |
| Git        | any     |                                                         |

macOS-specific: `build/icon.icns` is regenerated from `icon.png` by the CI
workflow; if you build locally and the file is missing, run
`sips -s format icns src/frontend/build/icon.png --out src/frontend/build/icon.icns`.

### Build steps

```bash
git clone https://github.com/nks-hub/webdev-console.git nks-ws
cd nks-ws

# 1. Build the .NET solution (daemon + 10 plugins + tests).
#    This is REQUIRED before the Electron packaging step — stage-plugins.mjs
#    expects build/plugins/ to exist.
dotnet build WebDevConsole.sln -c Release

# 2. Install frontend dependencies (~150 MB Electron binary download on first run).
cd src/frontend
npm install

# 3. Build the platform installer.
npm run dist          # Windows (NSIS installer + portable .exe)
npm run dist:mac      # macOS (DMG + ZIP, arm64 only)
npm run dist:linux    # Linux (AppImage)
```

Output lands in `src/frontend/release/`.

### Running in development

```bash
# Terminal 1 — start the daemon
dotnet run --project src/daemon/NKS.WebDevConsole.Daemon

# Terminal 2 — start Electron with hot-reload
cd src/frontend
npm run dev
```

Or, on Windows, just run `run.cmd` from the repo root (kills previous, builds,
starts daemon + Electron in separate windows). Flags: `--no-build`,
`--daemon-only`, `--frontend-only`.

## CLI

The `wdc` binary (System.CommandLine) is published alongside the daemon. A few
common commands:

```bash
wdc site list                                 # list all sites
wdc site create --name my-app --php 8.3 \
                --domain my-app.local --ssl   # create + provision SSL
wdc service start apache
wdc database list
wdc binaries catalog                          # available PHP / Apache / MySQL versions
wdc doctor                                    # 13 health checks
wdc --json site list                          # machine-readable output for scripts
```

All write commands have `HttpRequestException` handling, all list commands
output tab-separated data when piped to another process.

## REST API + auth

The daemon writes its listening port and a fresh Bearer token at startup:

| OS      | Port file                                |
| ------- | ---------------------------------------- |
| Windows | `%TEMP%\nks-wdc-daemon.port`             |
| macOS   | `$TMPDIR/nks-wdc-daemon.port`            |
| Linux   | `/tmp/nks-wdc-daemon.port`               |

Format: line 1 = port (e.g. `5000`), line 2 = base64-encoded token. The token
rotates every restart. Use it as `Authorization: Bearer <token>` on every
request.

```bash
PORT=$(sed -n 1p $TMPDIR/nks-wdc-daemon.port)
TOKEN=$(sed -n 2p $TMPDIR/nks-wdc-daemon.port)
curl -H "Authorization: Bearer $TOKEN" http://localhost:$PORT/api/status
# → {"status":"running","version":"0.2.0","plugins":10,"uptime":215437}
```

OpenAPI is exposed at `/openapi/v1.json` (Microsoft.AspNetCore.OpenApi).

## MCP server (Claude Code integration)

The MCP server lives at `services/mcp-server/`. Build and register it like
this:

```bash
cd services/mcp-server
npm install && npm run build

# Register as a user-scope MCP in Claude Code
claude mcp add --scope user nks-wdc node "$(pwd)/dist/index.js"
```

Tool catalog (47 tools):

| Module      | Count | Examples                                              |
| ----------- | ----- | ----------------------------------------------------- |
| sites       | 6     | list, get, create, delete, get_metrics, get_access_log |
| services    | 3     | list, start, stop                                     |
| system      | 3     | status, system_info, recent_activity                  |
| databases   | 6     | list, create, drop, tables, query, execute            |
| ssl         | 4     | list_certs, install_ca, generate_cert, revoke_cert    |
| php         | 2     | set_default, toggle_extension                         |
| binaries    | 5     | list_catalog, list_installed, install, uninstall, refresh |
| plugins     | 5     | list, get, enable, disable, …                         |
| backup      | 3     | create, list, restore                                 |
| settings    | 2     | get, update                                           |
| cloudflare  | 9     | tunnels, dns, routes, …                               |

Destructive tools (`drop_database`, `delete_site`, `revoke_cert`,
`restore_backup`, `execute`) require `confirm: "YES"` in the tool input. The
daemon's domain validators (`ValidateDomain`, `IsValidDatabaseName`, …) are
the security boundary; the MCP server is just a typed transport.

E2E harness: `node services/mcp-server/e2e-test.mjs` (27 assertions).

## Plugins

| Plugin     | Purpose                                                              |
| ---------- | -------------------------------------------------------------------- |
| Apache     | httpd 2.4 + mod_fcgid, vhost templates, per-site PHPRC, error logs   |
| PHP        | Multi-version manager (7.4, 8.1, 8.3, 8.4 verified), per-site INI    |
| MySQL      | MySQL/MariaDB lifecycle, root password DPAPI/keychain, my.cnf editor |
| SSL        | mkcert wrapper — local CA + per-site certs                           |
| Caddy      | Optional Caddy v2 reverse proxy                                      |
| Mailpit    | SMTP catcher for `mail()` testing                                    |
| Redis      | Single-instance Redis server                                         |
| Node       | nvm-style multi-version Node manager                                 |
| Cloudflare | `cloudflared` tunnels, DNS records, route management                 |
| Hosts      | Atomic hosts-file editor for `.local` / `.test` domains              |

Plugins are loaded from `build/plugins/` at startup via `AssemblyLoadContext`
isolation. Plugin SDK at `src/daemon/NKS.WebDevConsole.Plugin.SDK` defines the
`IPlugin` contract, `UiSchemaBuilder` for settings UI, and shared service
contracts. See `docs/plugin-sdk-reference.md` for plugin authoring.

## Migration from MAMP PRO

The `MampMigrator` (built into the daemon) reads MAMP PRO's
`%APPDATA%\Appsolute\MAMPPRO\userdb\mamp.db` SQLite database
(`VirtualHosts` + `VirtualHostServerAlias` tables) and recreates the sites
inside NKS WDC. Databases are migrated from MAMP's MySQL (port `3308` by
default — pass the actual port if yours differs).

Verified migration: 17 sites + 66 MySQL databases from a real MAMP PRO install.

```bash
wdc migrate mamp --port 3308              # dry-run by default
wdc migrate mamp --port 3308 --apply      # actually do it
```

## CI / Releases

`.github/workflows/build.yml` runs on every push of a `v*` tag and on manual
`workflow_dispatch`. Matrix:

| OS              | Daemon RID  | Output                                       |
| --------------- | ----------- | -------------------------------------------- |
| `windows-2022`  | `win-x64`   | `*-setup-x64.exe` (NSIS) + `*-portable-x64.exe` |
| `macos-14`      | `osx-arm64` | `*-mac-arm64.dmg` + `*-mac-arm64.zip`        |
| `ubuntu-24.04`  | `linux-x64` | `*.AppImage`                                 |

Tagged builds are uploaded as a GitHub Release via
`softprops/action-gh-release@v2` with `generate_release_notes: true`.

To cut a release:

```bash
git tag v0.2.3
git push --tags
# → GitHub Actions builds all three platforms, uploads to Releases
```

Code signing on macOS uses the local self-signed identity (TeamID DG3SLRLF7A)
when run on a developer machine; CI runs without signing identity unless one
is configured via `CSC_LINK` / `CSC_KEY_PASSWORD` secrets. Notarization is
skipped unless `APPLE_ID`, `APPLE_APP_SPECIFIC_PASSWORD` and `APPLE_TEAM_ID`
are present.

## Tests

```bash
dotnet test WebDevConsole.sln           # 595+ tests across daemon + core
cd services/catalog-api && pytest       # FastAPI sidecar tests
cd services/mcp-server && node e2e-test.mjs    # MCP smoke tests
```

CI runs the test job on every push (see `.github/workflows/test.yml`).

## Documentation

- [`docs/getting-started.md`](docs/getting-started.md) — installation and first-site walkthrough
- [`docs/troubleshooting.md`](docs/troubleshooting.md) — known issues and fixes
- [`docs/migration-mamp-pro.md`](docs/migration-mamp-pro.md) — MAMP PRO import guide
- [`docs/plugin-sdk-reference.md`](docs/plugin-sdk-reference.md) — write your own plugin
- [`docs/release-update-runbook.md`](docs/release-update-runbook.md) — release process
- [`SPEC.md`](SPEC.md) — implementation spec

## License

MIT. See [`LICENSE`](LICENSE) once it is added — the project does not yet
ship a LICENSE file at the repo root.

## Contributing

This is currently developed in a private repo (`nks-hub/webdev-console`). If
you have access, the workflow is:

1. Branch from `main`
2. `dotnet test` + `pytest` + frontend build must stay green
3. Open a PR — CI runs the matrix build for all three platforms

Issues and feature requests via the GitHub issue tracker on the same repo.
