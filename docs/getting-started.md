# Getting Started

This guide walks you through installing **NKS WebDev Console** from a tagged
GitHub release (or building it yourself), running the first-time wizard and
creating your first local site.

## System requirements

| OS      | Tested versions                                     |
| ------- | --------------------------------------------------- |
| Windows | Windows 10 build 19041+, Windows 11                 |
| macOS   | macOS 11 Big Sur or later, Apple Silicon (arm64)    |
| Linux   | Ubuntu 22.04+, Debian 12+, Fedora 39+ (x86_64)      |

Minimum 2 GB RAM (4 GB recommended). 500 MB free disk space for the app +
~2–4 GB for downloaded PHP/Apache/MySQL binaries.

Administrator rights required for the first-run wizard (creates services and
modifies `hosts` file). The daemon itself runs unprivileged after setup.

## Install

There is **no winget package, no Homebrew tap and no apt repository** — the
project ships pre-built artifacts via GitHub Releases.

### Windows

1. Download from <https://github.com/nks-hub/webdev-console/releases/latest>:
   - `NKS WebDev Console-<ver>-setup-x64.exe` — NSIS installer (recommended), or
   - `NKS WebDev Console-<ver>-portable-x64.exe` — single-file portable build
2. Run the installer and accept the UAC prompt.

### macOS (Apple Silicon)

1. Download `NKS WebDev Console-<ver>-mac-arm64.dmg` from the releases page.
2. Open the DMG and drag the app to `/Applications`.
3. The app is signed with a developer-distribution certificate but **not yet
   notarized**. On first launch macOS Gatekeeper may show "cannot be opened
   because the developer cannot be verified" — right-click → **Open** → confirm
   once, and macOS remembers it.

### Linux

1. Download the `.AppImage` from the releases page.
2. `chmod +x NKS\ WebDev\ Console-<ver>.AppImage` and run it.

### Build from source (all platforms)

```bash
git clone https://github.com/nks-hub/webdev-console.git nks-ws
cd nks-ws
dotnet build WebDevConsole.sln -c Release      # daemon + 10 plugins
cd src/frontend
npm install                                     # ~5 min, downloads Electron
npm run dist        # Windows
npm run dist:mac    # macOS  (arm64)
npm run dist:linux  # Linux  (AppImage)
```

Build prerequisites: .NET 9 SDK, Node.js 20+. On macOS install via
`brew install dotnet@9` (the `dotnet` CLI from Microsoft installer also works).

## First run

Launch the app. It auto-starts the bundled `NKS.WebDevConsole.Daemon`
(self-contained .NET 9, no separate runtime needed) and writes its listening
port + Bearer token to:

| OS      | Port file                                     |
| ------- | --------------------------------------------- |
| Windows | `%TEMP%\nks-wdc-daemon.port`                  |
| macOS   | `$TMPDIR/nks-wdc-daemon.port`                 |
| Linux   | `/tmp/nks-wdc-daemon.port`                    |

The Electron renderer reads the same file and connects automatically.

The first-run wizard asks you to:

- pick a **projects directory** (default `~/projects` / `%USERPROFILE%\projects`)
- choose a **default PHP version** (catalog is loaded from the configured
  catalog API — default is the bundled fallback list)
- set a **MySQL root password** (stored DPAPI-encrypted on Windows, in macOS
  Keychain on macOS, plaintext file `chmod 600` on Linux)

The wizard creates `~/.wdc/` (or `%USERPROFILE%\.wdc\`) for daemon state, plus
SQLite databases for sites, services, settings and migrations.

## Create your first site

### From the GUI

1. Click the **+** button on the Sites page.
2. Fill in:
   - **Domain** — e.g. `my-app.local` (`.local`/`.test`/`.localhost` work
     out of the box; the Hosts plugin updates `/etc/hosts` atomically)
   - **Project root** — auto-created if it doesn't exist
   - **PHP version** — pick from installed versions
   - **Web server** — Apache (default) or Caddy
   - **SSL** — leave on; mkcert generates a per-site cert from the local CA
3. Click **Create**.

The site is reachable at `https://my-app.local` within 10–15 seconds. Apache
reload, hosts entry, vhost template render and SSL cert generation happen
inside a single orchestrated transaction (`SiteOrchestrator`).

### From the CLI

The `wdc` binary is published next to the daemon. Add it to your PATH (the
installer does this automatically on Windows; on macOS/Linux it lives inside
the app bundle, e.g. `/Applications/NKS WebDev Console.app/Contents/Resources/daemon/wdc`).

```bash
wdc site create --domain my-app.local --root ~/projects/my-app --php 8.3 --ssl
wdc site list
wdc service start apache
wdc database create my_app_db
wdc doctor                           # 13 health checks
wdc binaries catalog                 # list available PHP/Apache/MySQL versions
wdc --json site list                 # JSON output for scripting
```

Run `wdc --help` for the full command reference.

## Stopping services

```bash
wdc service stop apache
wdc service stop mysql
# or stop everything the daemon manages:
wdc service stop --all
```

The Electron tray icon also exposes a service menu (right-click).

## Troubleshooting

If something doesn't work, check [`troubleshooting.md`](./troubleshooting.md).
The first thing to try is `wdc doctor` — it covers port collisions, DNS,
permissions, missing extensions, daemon connectivity and more.

For implementation details and API surface, see [`../README.md`](../README.md)
and [`../SPEC.md`](../SPEC.md).
