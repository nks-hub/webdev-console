[![Build & Release](https://github.com/nks-hub/webdev-console/actions/workflows/build.yml/badge.svg)](https://github.com/nks-hub/webdev-console/actions/workflows/build.yml)
[![CI](https://github.com/nks-hub/webdev-console/actions/workflows/ci.yml/badge.svg)](https://github.com/nks-hub/webdev-console/actions/workflows/ci.yml)
[![Latest Release](https://img.shields.io/github/v/release/nks-hub/webdev-console?label=release)](https://github.com/nks-hub/webdev-console/releases)
[![License](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-9.0-512BD4)](https://dotnet.microsoft.com/)
[![Electron](https://img.shields.io/badge/Electron-34-47848F)](https://www.electronjs.org/)
[![Vue 3](https://img.shields.io/badge/Vue-3-42b883)](https://vuejs.org/)
[![Cross-platform](https://img.shields.io/badge/platform-Windows%20%7C%20macOS%20%7C%20Linux-blue)](https://github.com/nks-hub/webdev-console/releases)

# NKS WebDev Console

Local development environment for **Windows, macOS, and Linux** — manages Apache, PHP, MySQL/MariaDB, SSL, mail catching, Redis, Node, Cloudflare tunnels and DNS for `.local` / `.test` domains. A modern replacement for MAMP PRO, XAMPP, WampServer, Laragon and similar tools.

## Features

- ✅ **Cross-platform** — Windows (NSIS + portable), macOS (DMG + ZIP, Apple Silicon), Linux (AppImage)
- ✅ **One daemon, many plugins** — Apache, PHP, MySQL, MariaDB, SSL (mkcert), Mailpit, Redis, Node, Caddy, Cloudflare and Hosts editor loaded as isolated `.NET 9` plugin DLLs
- ✅ **Per-site PHP** — each site can pin its own PHP version, INI overrides (memory, timeouts, upload limits, Xdebug, OPcache, extensions) and Apache Fcgid timeouts
- ✅ **MAMP PRO migration** — built-in importer reads MAMP PRO's SQLite database, recreates vhosts, copies SSL certs and migrates databases (verified on 17 sites + 66 databases)
- ✅ **Three interfaces** — Electron GUI, `wdc` CLI, or REST API with Bearer-token auth
- ✅ **MCP server** — 67 tools that let Claude Code (or any MCP client) manage your local stack: sites, databases, services, SSL, plugins, backups, Cloudflare tunnels, deployments. Full audit trail of every tool call surfaces in the GUI's MCP → Activity tab (24h timeline, top tools, p50/p95/p99 latency, CSV export). See `src/daemon/NKS.WebDevConsole.Daemon/Mcp/README.md` for the operator guide.
- ✅ **Cloudflare integration** — built-in tunnel management, DNS records, route configuration via `cloudflared`
- ✅ **Elevated by default on Windows** — single UAC prompt at launch, no per-operation prompts for hosts-file edits
- ✅ **Self-contained daemon** — .NET 9 published with `--self-contained`, no separate runtime install needed
- ✅ **Type-safe** — strict TypeScript on the frontend, `nullable enable` on the backend, 595+ xUnit tests

## Requirements

### Running the pre-built app

- **Windows** 10 / 11 (x64) — administrator rights for hosts-file editing
- **macOS** 14+ (Apple Silicon, arm64)
- **Linux** — any glibc 2.28+ distribution (Ubuntu 22.04+, Debian 12+, Fedora 38+)

### Building from source

| Tool     | Version | Notes                                                |
| -------- | ------- | ---------------------------------------------------- |
| .NET SDK | 9.0+    | macOS: `brew install dotnet@9`                       |
| Node.js  | 20 LTS+ | Tested with 22 (CI) and 23.x                         |
| Python   | 3.11+   | Optional — only for running the catalog-api locally  |
| Git      | any     |                                                      |

## Installation

### Pre-built releases

Download the latest installer from [GitHub Releases](https://github.com/nks-hub/webdev-console/releases):

- Windows: `NKS.WebDev.Console-<version>-setup-x64.exe` (NSIS) or `*-portable-x64.exe`
- macOS: `NKS.WebDev.Console-<version>-mac-arm64.dmg` or `*.zip`
- Linux: `NKS.WebDev.Console-<version>-linux-x86_64.AppImage`

### From source

```bash
git clone https://github.com/nks-hub/webdev-console.git
cd webdev-console

# 1. Build the .NET solution (daemon + plugin SDK + tests)
dotnet build WebDevConsole.sln -c Release

# 2. Install frontend dependencies (~150 MB Electron binary on first run)
cd src/frontend
npm install

# 3. Build the platform installer
npm run dist          # Windows (NSIS + portable)
npm run dist:mac      # macOS (DMG + ZIP)
npm run dist:linux    # Linux (AppImage)
```

Output lands in `src/frontend/release/`.

## Quick Start

Start the daemon + Electron in development:

```bash
# Terminal 1 — daemon
dotnet run --project src/daemon/NKS.WebDevConsole.Daemon

# Terminal 2 — Electron with hot-reload
cd src/frontend && npm run dev
```

On Windows, `run.cmd` from the repo root does both (supports `--no-build`, `--daemon-only`, `--frontend-only`).

Create your first site:

```bash
wdc site create --name my-app --php 8.3 --domain my-app.local --ssl
wdc service start apache
```

Open `https://my-app.local/` — the site is served from `~/wdc/sites/my-app/` with PHP 8.3 through Apache + mod_fcgid and a mkcert-signed SSL certificate.

## Usage

### CLI

The `wdc` binary (System.CommandLine) is published alongside the daemon:

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

All write commands have `HttpRequestException` handling; list commands emit TSV when piped.

### REST API + auth

The daemon writes its listening port + fresh Bearer token at startup:

| OS      | Port file                          |
| ------- | ---------------------------------- |
| Windows | `%TEMP%\nks-wdc-daemon.port`       |
| macOS   | `$TMPDIR/nks-wdc-daemon.port`      |
| Linux   | `/tmp/nks-wdc-daemon.port`         |

Format: line 1 = port (e.g. `5000`), line 2 = base64 token. The token rotates on every restart.

```bash
PORT=$(sed -n 1p $TMPDIR/nks-wdc-daemon.port)
TOKEN=$(sed -n 2p $TMPDIR/nks-wdc-daemon.port)
curl -H "Authorization: Bearer $TOKEN" http://localhost:$PORT/api/status
# → {"status":"running","version":"0.2.3","plugins":13,"uptime":215437}
```

OpenAPI spec is exposed at `/openapi/v1.json`.

### MCP server (Claude Code)

```bash
cd services/mcp-server
npm install && npm run build
claude mcp add --scope user nks-wdc node "$(pwd)/dist/index.js"
```

| Module      | Tools | Examples                                                   |
| ----------- | ----- | ---------------------------------------------------------- |
| sites       | 6     | list, get, create, delete, get_metrics, get_access_log     |
| services    | 3     | list, start, stop                                          |
| system      | 3     | status, system_info, recent_activity                       |
| databases   | 6     | list, create, drop, tables, query, execute                 |
| ssl         | 4     | list_certs, install_ca, generate_cert, revoke_cert         |
| php         | 2     | set_default, toggle_extension                              |
| binaries    | 5     | list_catalog, list_installed, install, uninstall, refresh  |
| plugins     | 5     | list, get, enable, disable, info                           |
| backup      | 3     | create, list, restore                                      |
| settings    | 2     | get, update                                                |
| cloudflare  | 9     | tunnels, dns, routes, …                                    |

Destructive tools (`drop_database`, `delete_site`, `revoke_cert`, `restore_backup`, `execute`) require `confirm: "YES"`.

### MAMP PRO migration

```bash
wdc migrate mamp --port 3308              # dry-run
wdc migrate mamp --port 3308 --apply      # actually migrate
```

Reads `%APPDATA%\Appsolute\MAMPPRO\userdb\mamp.db` and rebuilds sites + databases in WDC.

## Architecture

```
webdev-console/
├── src/
│   ├── daemon/                              # C# .NET 9
│   │   ├── NKS.WebDevConsole.Daemon         # ASP.NET Core REST service
│   │   ├── NKS.WebDevConsole.Core           # shared services + models
│   │   ├── NKS.WebDevConsole.Cli            # `wdc` CLI (System.CommandLine)
│   │   └── NKS.WebDevConsole.Plugin.SDK     # plugin contract
│   └── frontend/                            # Electron 34 + Vue 3 + Element Plus + Tailwind v4
├── services/
│   ├── catalog-api/                         # FastAPI sidecar (binary + version catalog)
│   └── mcp-server/                          # TypeScript stdio MCP server (67 tools, Phase 8 audit log)
├── tests/                                   # xUnit tests (Daemon + Core)
├── scripts/                                 # build helpers
└── .github/workflows/                       # CI matrix — Windows, macOS, Linux
```

### Related repositories

NKS WebDev Console ships as four co-operating public repositories:

| Repo | Purpose |
|------|---------|
| [`nks-hub/webdev-console`](https://github.com/nks-hub/webdev-console) | This repo — daemon, CLI, Electron frontend, Plugin SDK |
| [`nks-hub/webdev-console-plugins`](https://github.com/nks-hub/webdev-console-plugins) | 13 official plugins (Apache, PHP, MySQL, …) |
| [`nks-hub/webdev-console-binaries`](https://github.com/nks-hub/webdev-console-binaries) | Versioned runtime binaries (PHP, Apache, Nginx, Caddy, …) |
| [`nks-hub/wdc-catalog-api`](https://github.com/nks-hub/wdc-catalog-api) | FastAPI catalog + config-sync service |

A single `v*` tag push to this repo triggers **Build & Release** (Win/macOS/Linux installers), **Publish Plugin SDK** (GitHub Packages NuGet), **Defender Submission** and **Release Security Scan** in parallel.

## Development

### Tests

```bash
dotnet test WebDevConsole.sln                  # 595+ tests (daemon + core)
cd services/catalog-api && pytest              # FastAPI sidecar tests
cd services/mcp-server && node e2e-test.mjs    # MCP smoke tests (27 assertions)
```

### Cutting a release

```bash
git tag v0.2.3
git push --tags
```

GitHub Actions builds all three platforms and uploads to Releases. macOS code-signing uses `CSC_LINK` + `CSC_KEY_PASSWORD` secrets when present; notarization requires `APPLE_ID`, `APPLE_APP_SPECIFIC_PASSWORD`, `APPLE_TEAM_ID`.

### CI matrix

| OS             | Daemon RID  | Output                                            |
| -------------- | ----------- | ------------------------------------------------- |
| `windows-2022` | `win-x64`   | `*-setup-x64.exe` (NSIS) + `*-portable-x64.exe`   |
| `macos-14`     | `osx-arm64` | `*-mac-arm64.dmg` + `*-mac-arm64.zip`             |
| `ubuntu-24.04` | `linux-x64` | `*.AppImage`                                      |

## Contributing

Contributions are welcome! For major changes please open an issue first.

1. Fork the repository
2. Create your feature branch (`git checkout -b feat/amazing-feature`)
3. Keep `dotnet test` + frontend build green
4. Commit your changes — one-line conventional commit messages, no AI attribution
5. Open a Pull Request — CI runs the matrix build for all three platforms

## Support

- 📧 **Email:** dev@nks-hub.cz
- 🐛 **Bug reports:** [GitHub Issues](https://github.com/nks-hub/webdev-console/issues)
- 💬 **Discussions:** [GitHub Discussions](https://github.com/nks-hub/webdev-console/discussions)

## License

MIT License — see [LICENSE](LICENSE) for details.

---

<p align="center">
  Made with ❤️ by <a href="https://github.com/nks-hub">NKS Hub</a>
</p>
