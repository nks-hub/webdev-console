# DevForge - Complete Implementation Plan & Documentation

**Version:** 1.0.0-draft  
**Date:** 2026-04-09  
**Status:** Implementation Plan  
**Compiled from:** 15 parallel specialist agents + web research  

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [Market Analysis & Competitive Landscape](#2-market-analysis--competitive-landscape)
3. [Technology Stack Decision](#3-technology-stack-decision)
4. [System Architecture](#4-system-architecture)
5. [Core Engine - Service Management](#5-core-engine---service-management)
6. [Configuration Pipeline](#6-configuration-pipeline)
7. [Virtual Host Manager](#7-virtual-host-manager)
8. [PHP Version Manager](#8-php-version-manager)
9. [Network & DNS Architecture](#9-network--dns-architecture)
10. [SSL/TLS Module](#10-ssltls-module)
11. [Database Schema](#11-database-schema)
12. [CLI Interface & API Specification](#12-cli-interface--api-specification)
13. [UI/UX Design Specification](#13-uiux-design-specification)
14. [Plugin Architecture](#14-plugin-architecture)
15. [Security Model](#15-security-model)
16. [Performance Targets & Optimization](#16-performance-targets--optimization)
17. [Testing Strategy](#17-testing-strategy)
18. [Packaging & Distribution](#18-packaging--distribution)
19. [Legal & Licensing](#19-legal--licensing)
20. [Implementation Roadmap](#20-implementation-roadmap)
21. [Risk Analysis](#21-risk-analysis)

---

## 1. Executive Summary

**DevForge** is a modern, portable local development server management tool designed to replace MAMP PRO, XAMPP, WampServer, and similar tools. It addresses critical pain points discovered through real-world usage of existing tools:

- **Config corruption** (MAMP PRO's SQLite stores vhosts with empty values → Apache syntax errors)
- **SSL complexity** (manual cert generation, trust store management)
- **PHP OpenSSL failures** on Windows (EC key generation broken in MAMP's bundled OpenSSL)
- **Two-config confusion** (MAMP's dual httpd.conf files)
- **No automation** (no CLI interface in MAMP PRO)
- **Heavy footprint** (Docker-based tools consume 1-2GB+ baseline)

### Core Value Proposition

| Feature | MAMP PRO | XAMPP | Laragon | FlyEnv | **DevForge** |
|---------|----------|-------|---------|--------|-------------|
| Multi-PHP (5.6-8.4) | 2 versions | Single | Multiple | Multiple | **Multiple, per-site** |
| Virtual Hosts | GUI only | Manual | Auto .test | GUI | **CLI + GUI + API** |
| SSL Certs | Manual | Manual | 1-click mkcert | Auto | **Auto mkcert + CA mgmt** |
| Config Validation | None | None | None | Unknown | **3-stage: parse→render→dry-run** |
| Portable | Partial | Partial | Yes | Yes | **Yes (copy folder)** |
| CLI | None | None | None | Limited | **Full JSON-RPC CLI** |
| Plugin System | None | None | None | Custom modules | **Sandboxed Lua plugins** |
| Docker Integration | None | None | None | Podman | **Traefik reverse proxy** |
| Config Storage | SQLite (fragile) | INI files | INI files | Unknown | **TOML per-site + SQLite state** |
| Open Source | No (Pro) | Yes | Yes | Yes (BSD-3) | **Yes (Apache-2.0)** |

### Target Metrics

- **Startup:** < 3 seconds to all services running
- **Memory:** < 250MB idle (tool + all services)
- **Site creation:** < 1 second
- **PHP switch:** < 2 seconds
- **Supported sites:** Unlimited (tested to 50+)

---

## 2. Market Analysis & Competitive Landscape

### Existing Tools (2025-2026)

#### Tier 1 — Established Players

| Tool | Stack | Platform | License | Key Strength | Key Weakness |
|------|-------|----------|---------|-------------|-------------|
| **MAMP PRO** | Native (Obj-C/C++) | macOS, Win | Proprietary ($69) | Polished GUI, CMS installer | Config corruption, no CLI, OpenSSL bugs |
| **XAMPP** | C/Perl | Win, macOS, Linux | Apache-2.0 | Multi-platform, Perl support | Dated GUI, manual vhosts, single PHP |
| **WampServer** | Native (C++) | Windows only | GPL | Easy PHP switching, tray menu | Windows only, no SSL auto, security defaults |
| **Laragon** | Native (C++) | Windows only | LGPL | Portable, auto .test, fast | Windows only, unclear future (author inactive) |

#### Tier 2 — Modern Contenders

| Tool | Stack | Platform | License | Key Strength | Key Weakness |
|------|-------|----------|---------|-------------|-------------|
| **FlyEnv** | Electron + Vue + TS | Win, macOS, Linux | BSD-3 | 50+ modules, AI integration, native binaries | Electron memory overhead, 2.7k stars |
| **ServBay** | Native | macOS, Win | Freemium | Multi-language, DNS manager, sleek GUI | Partially paid, newer |
| **Laravel Herd** | Native (Swift/C++) | macOS, Win | Free/Pro ($99/yr) | Laravel-focused, fast, Valet-based | Laravel-centric, Pro features locked |
| **DDEV** | Go + Docker | All | Apache-2.0 | Docker isolation, CI/CD friendly | Docker overhead (1-2GB RAM), 15-45s startup |

#### Tier 3 — Specialized/Lightweight

| Tool | Platform | Notes |
|------|----------|-------|
| **Laravel Valet** | macOS only | CLI-only, lightweight, dnsmasq-based |
| **LocalWP** | Win, macOS | WordPress-only, site isolation |
| **DevKinsta** | Win, macOS | WordPress + Kinsta hosting |

### Key Market Insights

1. **FlyEnv is the closest competitor** — 50+ modules, cross-platform, BSD-3, but uses Electron (heavy)
2. **No tool validates configs before applying** — this is DevForge's killer feature
3. **Laragon is beloved but Windows-only** with uncertain future
4. **Docker tools (DDEV) are powerful but too heavy** for simple PHP dev
5. **Per-site PHP version** via FPM pools is now table stakes
6. **CLI + automation** is increasingly demanded (CI/CD, scripting)

### DevForge Positioning

**"The Laragon experience, everywhere, with the safety of config validation and the power of a plugin ecosystem."**

Target audience: PHP developers who want native performance (not Docker overhead), multiple PHP versions, and scriptable automation.

---

## 3. Technology Stack Decision

### Core Engine: Go (Recommended) or Rust

| Criteria | Go | Rust | C# (.NET) | Node.js |
|----------|------|------|-----------|---------|
| Process management | Excellent (`os/exec`, goroutines) | Excellent (async, tokio) | Moderate (heavy runtime) | Poor (single-threaded) |
| Cross-compilation | `GOOS=windows go build` | `cargo build --target` | Requires .NET runtime | Requires Node.js |
| Binary size | ~10MB | ~5MB | ~50MB+ (with runtime) | N/A (needs runtime) |
| Startup speed | Fast | Fastest | Slow (JIT) | Moderate |
| Developer ecosystem | Large, familiar | Growing | Large (Windows) | Largest |
| Concurrency | Goroutines (easy) | async/await (complex) | async/await | Event loop |
| Template engine | `text/template` (built-in) | Tera (Jinja2-like) | Razor | Handlebars |
| SQLite | `modernc.org/sqlite` (pure Go) | `rusqlite` | EF Core | better-sqlite3 |

**Recommendation: Go** for the daemon. Pragmatic choice — goroutines map directly to per-service supervisors, `os/exec` handles process management cleanly, single static binary with zero dependencies. Cross-compilation from any platform.

**Alternative: Rust** if maximum performance and memory safety are prioritized. Adds development complexity but produces smaller binaries and eliminates GC pauses.

### GUI Framework: Full Evaluation

| Framework | Bundle Size | Memory | Platform | Language | Rendering | Verdict |
|-----------|------------|--------|----------|----------|-----------|---------|
| **Tauri v2** | **5-10 MB** | **30-80 MB** | Win/macOS/Linux | Rust + Web (Svelte/React) | Native WebView2/WebKit | **RECOMMENDED** |
| Electron | 130-160 MB | 250-400 MB | Win/macOS/Linux | Node.js + Web | Bundled Chromium | Too heavy |
| Neutralinojs | ~6 MB | ~40 MB | Win/macOS/Linux | C++ + Web | Native WebView | Lacks ecosystem |
| Qt (C++) | 30-50 MB | ~50 MB | Win/macOS/Linux | C++ / QML | Native widgets | Steep learning curve |
| .NET MAUI | 50+ MB | ~60 MB | Win/macOS | C# + XAML | Native controls | .NET runtime required |
| Flutter Desktop | 20+ MB | ~40 MB | Win/macOS/Linux | Dart + widgets | Custom Skia engine | Platform channel complexity |
| Sciter | ~5 MB | ~20 MB | Win/macOS/Linux | C++ + HTML/CSS | Custom engine | Proprietary license |
| GTK4 + libadwaita | ~15 MB | ~30 MB | Linux (Win/macOS limited) | C/Vala/Rust | Native GTK | Linux-centric |
| SwiftUI | 0 (system) | ~20 MB | macOS only | Swift | Native Apple | Apple-only |
| FLTK | ~1 MB | ~10 MB | Win/macOS/Linux | C++ | Custom lightweight | Too basic for modern UI |

**Decision: Tauri v2 with Svelte 5** — validated by prototype (21 files in `prototype/gui/`).

Key reasons:
- **5-10 MB** installer vs Electron's 130-160 MB
- WebView2 ships with Windows 10/11 — no bundled browser
- Rust backend enables system tray, privilege elevation, named pipe IPC
- Sidecar feature launches Go daemon as managed child process
- **CAUTION**: Tauri NSIS installer triggers same AV false positives as Go — use **MSI (WiX)** instead

### Configuration Storage: Hybrid

- **Site configs: TOML files** (one per site) — human-readable, diffable, survives corruption
- **Runtime state: SQLite** — PIDs, health log, events, service status
- **Rationale:** Solves MAMP PRO's single-DB-corruption problem. Each site is independent.

### IPC: JSON-RPC 2.0 over local sockets

- **Windows:** Named pipe `\\.\pipe\devforge-daemon`
- **macOS/Linux:** Unix domain socket `~/.devforge/daemon.sock`
- **Protocol:** JSON-RPC 2.0 with streaming support for logs/events

---

## 4. System Architecture

### Layered Architecture

```
┌─────────────────────────────────────────────────────┐
│  PRESENTATION LAYER                                  │
│  ┌──────────────────┐  ┌─────────────────────────┐  │
│  │  GUI (Tauri +    │  │  CLI (devforge binary)  │  │
│  │  Svelte 5)       │  │                         │  │
│  └────────┬─────────┘  └──────────┬──────────────┘  │
│           │                       │                  │
├───────────┴───────────────────────┴──────────────────┤
│  API LAYER — JSON-RPC 2.0 over local socket/pipe     │
├──────────────────────────────────────────────────────┤
│  CORE ENGINE (devforged daemon)                       │
│  ┌───────────┬────────────┬───────────┬───────────┐  │
│  │ Service   │ Config     │ VHost     │ Plugin    │  │
│  │ Manager   │ Pipeline   │ Manager   │ Host      │  │
│  ├───────────┼────────────┼───────────┼───────────┤  │
│  │ PHP Mgr   │ SSL/Cert   │ DNS/Hosts │ DB Mgr   │  │
│  └───────────┴────────────┴───────────┴───────────┘  │
│  ┌──────────────────────────────────────────────────┐ │
│  │  Event Bus (pub/sub) + Health Monitor            │ │
│  └──────────────────────────────────────────────────┘ │
├──────────────────────────────────────────────────────┤
│  PLATFORM ABSTRACTION LAYER (PAL)                     │
│  (process spawning, privilege elevation,              │
│   file permissions, service registration)             │
└──────────────────────────────────────────────────────┘
```

### Three Binaries

1. **`devforged`** — Background daemon. Owns all child processes. Exposes JSON-RPC API. Runs as user-level process (NOT admin/root).
2. **`devforge`** — CLI client. Thin RPC client, no direct file or process manipulation.
3. **`DevForge`** — GUI app (Tauri). Embeds daemon or connects to existing one.

### Critical Design Principle

**The daemon is the single source of truth.** Neither CLI nor GUI ever modify config files or spawn services directly. All mutations go through the daemon's API.

---

## 5. Core Engine - Service Management

### Service Process Model

Each managed service (Apache, Nginx, MySQL, MariaDB, PHP-FPM) is represented as a `ServiceUnit`:

```go
type ServiceState int
const (
    StateStopped ServiceState = iota
    StateStarting
    StateRunning
    StateStopping
    StateCrashed
    StateRestarting
    StateDisabled
)

type ServiceProcess struct {
    ID           string
    State        ServiceState
    PID          int
    Cmd          *exec.Cmd
    LogBuffer    *RingBuffer    // last 1000 lines
    RestartCount int
    LastCrash    time.Time
    Config       ServiceConfig
}
```

### State Machine

```
STOPPED ──start()──► STARTING ──ready()──► RUNNING
                          │                    │
                     fail()│              crash()│
                          ▼                    ▼
                       CRASHED ◄──────── CRASHED
                          │
              restartPolicy│ (within threshold)
                          ▼
                     RESTARTING ──► STARTING

RUNNING ──stop()──► STOPPING ──done()──► STOPPED
                        │
                   timeout(10s)
                        ▼
                    SIGKILL ──► STOPPED
```

### Crash Recovery

```go
type RestartPolicy struct {
    MaxRestarts    int           // 5
    WindowDuration time.Duration // 60s
    BackoffBase    time.Duration // 2s
    BackoffMax     time.Duration // 30s
}
```

If `RestartCount > MaxRestarts` within `WindowDuration` → transition to `StateDisabled`, fire `service.degraded` event.

### Startup Sequence (Target: < 3 seconds)

```
T=0ms    : Daemon starts, read cached config index
T=0ms    : [PARALLEL] MySQL + dnsmasq launch
T=0ms    : [PARALLEL] Config validation (pre-generated)
T=~800ms : MySQL socket accepting connections
T=~800ms : [PARALLEL] PHP-FPM pools start
T=~1200ms: PHP-FPM pools ready (sockets listening)
T=~1200ms: Apache/Nginx start (depends on PHP-FPM)
T=~1600ms: Apache/Nginx ready, all vhosts responding
T=~2000ms: Health checks pass → "All services ready"
```

**Key optimizations:**
- Direct process launch, NOT Windows Services (saves 400-800ms)
- Pre-generate all configs at site creation, not startup
- PHP-FPM starts in parallel with MySQL (no dependency)
- Debounce config reloads (500ms window)

### Shutdown Sequence

```
1. Stop accepting IPC connections
2. Fire "daemon.stopping" event
3. Reverse order: Apache/Nginx → PHP-FPM → MySQL → dnsmasq
4. Each: SIGTERM → wait 10s → SIGKILL
5. Flush event log
6. Release PID lock
```

---

## 6. Configuration Pipeline

This is **DevForge's killer feature** — the architectural centerpiece that eliminates config corruption.

### Three-Stage Pipeline

```
TOML Source Files           Templates              Rendered Config
(sites/*.toml)    +    (templates/*.hbs)    →    (generated/*.conf)
                                                        │
                                                   VALIDATE
                                              (httpd -t / nginx -t)
                                                        │
                                               ┌────────┴────────┐
                                             PASS              FAIL
                                               │                 │
                                         Atomic rename     Return error
                                         (temp → live)     (no change)
                                               │
                                         Reload signal
                                         (graceful restart)
                                               │
                                         Version archive
                                         (keep last 5)
```

### Site TOML Format

```toml
[site]
hostname = "myapp.test"
aliases = ["www.myapp.test"]
document_root = "C:\\work\\sites\\myapp\\www"

[php]
version = "8.2"
extensions = ["xdebug", "intl", "gd"]
ini_overrides = { memory_limit = "512M", display_errors = "On" }

[ssl]
enabled = true
# cert auto-generated if not specified

[server]
type = "apache"   # or "nginx"
custom_directives = """
<Directory "${document_root}">
    AllowOverride All
</Directory>
"""
```

### Critical Invariant

The `generated/` directory is always fully reconstructible from `devforge.toml` + `sites/*.toml`. Users can delete it entirely and run `devforge config:rebuild`.

### Atomic Config Updates

```go
func (c *ConfigEngine) ApplyVhostConfig(site SiteConfig) error {
    rendered, err := c.renderTemplate(site)
    if err != nil { return err }

    tmpPath := site.ConfigPath + ".tmp"
    os.WriteFile(tmpPath, rendered, 0644)

    // VALIDATE before applying
    if err := c.validateConfig(site.Server, tmpPath); err != nil {
        os.Remove(tmpPath)
        return fmt.Errorf("config validation failed: %w", err)
    }

    c.archiveCurrent(site.ConfigPath)     // keep last 5 versions
    return os.Rename(tmpPath, site.ConfigPath)  // atomic
}
```

---

## 7. Virtual Host Manager

### CRUD Flow

```
CLI: devforge site:create myapp.test --php=8.2 --docroot=C:\work\sites\myapp\www
  │
  ▼
API: site.create →
  1. Validate hostname (RFC 952, check conflicts)
  2. Write sites/myapp.test.toml
  3. Run Config Pipeline (parse → render → dry-run → atomic apply)
  4. If SSL enabled → trigger SSL module (generate cert)
  5. Trigger DNS module (add hosts entry)
  6. Reload affected services (graceful, not restart)
  7. Return success + site summary
```

### Domain Validation

Strict regex enforced at DB level AND application level:
```
^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)*$
```

Rejects: spaces, newlines, null bytes, path traversal sequences. Eliminates the injection vector found in MAMP PRO.

---

## 8. PHP Version Manager

### Architecture

Each PHP version is self-contained under `bin/php-X.Y.Z/`. PHP-FPM runs per-version, listening on dedicated ports:

```
PHP 8.2 FPM → 127.0.0.1:9082  (or Unix socket)
PHP 8.3 FPM → 127.0.0.1:9083
PHP 5.6 FPM → 127.0.0.1:9056
```

Apache uses `mod_proxy_fcgi` to route to the correct FPM pool. Nginx uses `fastcgi_pass`. **No mod_php** — eliminates module conflicts entirely.

### OpenSSL Fix

DevForge bundles its own OpenSSL 3.x binaries per platform and sets `OPENSSL_CONF` and `OPENSSL_MODULES` environment variables per PHP process. EC key generation is tested as part of post-install validation.

### PHP-FPM Pool Configuration (Development Optimized)

```ini
pm = ondemand            # NOT static/dynamic (saves 400-600MB)
pm.max_children = 3
pm.process_idle_timeout = 30s
pm.max_requests = 500
```

Memory per site: ~8MB idle (master only), ~60MB active (with workers).

| Active Sites | PHP-FPM Memory | Notes |
|-------------|---------------|-------|
| 5 | ~250MB | Baseline |
| 10 | ~350MB | Negligible impact |
| 25 | ~500MB | Still under 3s startup |
| 50 | ~700MB | Approaches limits |

---

## 9. Network & DNS Architecture

### Strategy Per Platform

| Platform | Primary | Fallback | Wildcard |
|----------|---------|----------|---------|
| **Windows** | Hosts file modification | Acrylic DNS Proxy | Optional |
| **macOS** | `/etc/resolver/test` + dnsmasq:53535 | Hosts file | Native |
| **Linux** | systemd-resolved split-DNS | Hosts file | Native |

### Windows Hosts File Management

Hosts file editing requires elevation. DevForge uses a minimal elevation helper (`devforge-elevate.exe`) that performs ONLY the hosts file write. The daemon itself runs unprivileged.

Managed block with markers:
```
# >>> DevForge Managed - DO NOT EDIT <<<
127.0.0.1  myapp.test
127.0.0.1  api.local
# <<< DevForge Managed >>>
```

### Multi-Site Networking

**Name-based virtual hosts on 127.0.0.1** (recommended). All sites share the same IP, differentiated by `Host:` header — same as production. No per-site loopback IPs needed.

### Port Conflict Resolution Algorithm

```
1. Check port availability (netstat/lsof/ss)
2. If FREE → bind and return
3. If IN USE → identify owner process
4. Classify: KNOWN_WEB_SERVER | KNOWN_COMMS_APP | OWN_PROCESS | UNKNOWN
5. Offer: stop conflicting process OR use fallback port
6. Fallback map: 80→8080, 443→8443, 3306→3307
```

### Reverse Proxy (Optional)

Caddy or Traefik as optional reverse proxy layer. Benefits: single SSL termination point, Docker container routing, automatic cert management for all sites.

---

## 10. SSL/TLS Module

### mkcert Integration

First run:
1. `mkcert -install` — installs local CA into system trust store (one-time elevation)
2. CA files stored in `ssl/ca/`

Per-site cert generation:
```bash
mkcert -cert-file ssl/certs/myapp.test.pem \
       -key-file ssl/certs/myapp.test-key.pem \
       myapp.test "*.myapp.test"
```

### Security Hardening

- **CA key protection:** Windows DPAPI, macOS Keychain, Linux libsecret
- **Name Constraints:** CA limited to `.test`, `.local`, `.localhost` domains
- **Short-lived certs:** 30-day validity with auto-renewal
- **User warning:** Clear non-dismissable warning at CA creation

### Storage Layout

```
ssl/
├── ca/
│   ├── rootCA.pem         (CA cert, read-only)
│   └── rootCA-key.pem     (CA key, chmod 600)
└── sites/
    ├── myapp.test/
    │   ├── cert.pem
    │   └── key.pem
    └── shop.test/
        ├── cert.pem
        └── key.pem
```

---

## 11. Database Schema

### SQLite Configuration Database

```sql
PRAGMA journal_mode = WAL;
PRAGMA foreign_keys = ON;
PRAGMA busy_timeout = 5000;
PRAGMA synchronous = NORMAL;
```

### Core Tables

#### sites

```sql
CREATE TABLE sites (
    id                INTEGER PRIMARY KEY,
    domain            TEXT NOT NULL UNIQUE CHECK (
                        length(domain) > 0 AND domain NOT GLOB '* *'
                      ),
    aliases           TEXT NOT NULL DEFAULT '',
    document_root     TEXT NOT NULL CHECK (length(document_root) > 0),
    webserver_type    TEXT NOT NULL DEFAULT 'apache'
                      CHECK (webserver_type IN ('apache', 'nginx')),
    php_version_id    INTEGER REFERENCES php_versions(id) ON DELETE SET NULL,
    ssl_enabled       INTEGER NOT NULL DEFAULT 0 CHECK (ssl_enabled IN (0, 1)),
    certificate_id    INTEGER REFERENCES certificates(id) ON DELETE SET NULL,
    custom_directives TEXT NOT NULL DEFAULT '',
    status            TEXT NOT NULL DEFAULT 'active'
                      CHECK (status IN ('active', 'disabled')),
    sort_order        INTEGER NOT NULL DEFAULT 0,
    created_at        TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    updated_at        TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    CHECK (ssl_enabled = 0 OR certificate_id IS NOT NULL)
);
```

#### php_versions

```sql
CREATE TABLE php_versions (
    id            INTEGER PRIMARY KEY,
    version       TEXT NOT NULL UNIQUE CHECK (version GLOB '[0-9]*.[0-9]*.[0-9]*'),
    install_path  TEXT NOT NULL CHECK (length(install_path) > 0),
    status        TEXT NOT NULL DEFAULT 'installed'
                  CHECK (status IN ('installed', 'downloading', 'broken', 'removed')),
    extensions_json TEXT NOT NULL DEFAULT '[]' CHECK (json_valid(extensions_json)),
    ini_overrides TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(ini_overrides)),
    is_default    INTEGER NOT NULL DEFAULT 0 CHECK (is_default IN (0, 1)),
    created_at    TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
);

-- Only one default PHP version
CREATE UNIQUE INDEX idx_php_default ON php_versions(is_default) WHERE is_default = 1;
```

#### services, certificates, databases, plugins, settings, config_history

(Full DDL with CHECK constraints, foreign keys, JSON validation, triggers for audit trail — see [Database Schema appendix](#appendix-a-full-ddl))

### Key Design Decisions

- **WAL mode** for concurrent reads during service operation
- **JSON columns** with `json_valid()` CHECK constraints
- **Audit trail** via `config_history` table with triggers on UPDATE/DELETE
- **Migration system** with up/down SQL, checksum verification, automatic backup
- **Export format** as portable JSON for configuration transfer

---

## 12. CLI Interface & API Specification

### CLI Command Tree

```
devforge daemon:start|stop|status
devforge site:create <domain> [--php=X.Y] [--server=apache|nginx] [--docroot=PATH] [--ssl]
devforge site:list [--json]
devforge site:delete <domain>
devforge site:info <domain>
devforge site:open <domain>
devforge site:log <domain>
devforge start [service]
devforge stop [service]
devforge restart [service]
devforge status
devforge php:list
devforge php:install <version>
devforge php:remove <version>
devforge php:default <version>
devforge php:ext <version> <extension> [--enable|--disable]
devforge db:list
devforge db:create <name>
devforge db:drop <name>
devforge db:import <name> <file>
devforge db:export <name> [file]
devforge ssl:trust
devforge ssl:create <domain>
devforge ssl:status
devforge dns:status
devforge dns:flush
devforge config:get <key>
devforge config:set <key> <value>
devforge config:list
devforge config:rebuild
devforge plugin:list
devforge plugin:install <name>
devforge plugin:remove <name>
```

### Global Flags

| Flag | Description |
|------|-------------|
| `--json` | Machine-readable JSON output |
| `--no-color` | Disable ANSI colors |
| `--quiet` / `-q` | Suppress progress, errors only |
| `--verbose` / `-v` | Debug output |

### JSON-RPC 2.0 API

Transport: Named pipe (Win) / Unix domain socket (macOS/Linux).

**Key API Methods:**

| Method | Params | Returns |
|--------|--------|---------|
| `site.create` | `{domain, root, php, ssl, server}` | `{domain, config_path, fpm_socket}` |
| `site.list` | `{filter?}` | `{sites: [...]}` |
| `site.delete` | `{domain, remove_db?, remove_files?}` | `{deleted: true}` |
| `service.start` | `{name?}` | `{started: [...]}` |
| `service.stop` | `{name?}` | `{stopped: [...]}` |
| `service.status` | `{name?}` | `{services: [...]}` |
| `php.install` | `{version}` | `{installed, path}` (+ progress stream) |
| `php.set_default` | `{version}` | `{default}` |
| `db.create` | `{name, engine?}` | `{created, engine}` |
| `db.import` | `{name, file}` | `{imported, statements}` |
| `ssl.create` | `{domain, wildcard?}` | `{domain, cert, expires}` |
| `events.subscribe` | `{topics[]}` | SSE stream |

### Error Codes

| Code | Meaning |
|------|---------|
| `-32001` | Service not running |
| `-32002` | Domain not found |
| `-32003` | Domain already exists |
| `-32004` | PHP version not installed |
| `-32005` | Permission denied |
| `-32006` | Operation conflict |

### Event System

Topics: `service.*`, `site.*`, `php.*`, `config.*`, `health.*`

```json
{
  "event": "service.started",
  "id": "<uuid>",
  "timestamp": "2026-04-09T10:00:00Z",
  "data": { "name": "nginx", "pid": 12345 }
}
```

---

## 13. UI/UX Design Specification

### Design Language

- **Dark mode default** (developer preference), light mode available
- **Font:** Inter (UI) + JetBrains Mono (code/paths)
- **Border radius:** 6px (inputs/buttons), 10px (cards), 16px (modals)
- **Status colors:** Green (#22c55e) running, Red (#ef4444) stopped, Yellow (#f59e0b) warning, Blue (#38bdf8) info

### Color Palette (Dark Mode)

| Token | Hex | Usage |
|-------|-----|-------|
| `--bg-base` | `#0f1117` | App background |
| `--bg-surface` | `#1a1d27` | Cards, panels, sidebar |
| `--bg-elevated` | `#242736` | Hover states |
| `--text-primary` | `#e8eaf0` | Primary text |
| `--text-secondary` | `#8b90a7` | Metadata |
| `--accent-blue` | `#4f87ff` | Primary actions |

### Screen Layout

```
┌────────────┬────────────────────────────────┐
│            │                                │
│  SIDEBAR   │       CONTENT AREA             │
│  (56px     │                                │
│  collapsed │  Dashboard / Sites / PHP /     │
│  / 200px   │  Database / SSL / Terminal /   │
│  expanded) │  Settings                      │
│            │                                │
│  Dashboard │                                │
│  Sites     │                                │
│  PHP       │                                │
│  Database  │                                │
│  SSL       │                                │
│  Terminal  │                                │
│  Settings  │                                │
│            │                                │
└────────────┴────────────────────────────────┘
```

### Key Screens

1. **System Tray** — Status dot, Start/Stop All, recent sites, open terminal
2. **Dashboard** — Service status cards, resource usage, activity log, quick actions
3. **Sites Manager** — Table with status, domain, PHP, SSL; detail panel on select
4. **PHP Manager** — Installed versions, extensions toggle, download new versions
5. **Database** — MySQL/MariaDB instances, create/import/export, phpMyAdmin access
6. **SSL Manager** — Per-site cert status, one-click generation, CA trust status
7. **Terminal** — Embedded terminal with DevForge CLI integration
8. **Settings** — Ports, DNS, updates, plugins, themes

### Keyboard Shortcuts

- `Ctrl+K` — Command palette
- `Ctrl+T` — New terminal tab
- `Ctrl+1-8` — Switch sidebar sections
- `Ctrl+N` — New site
- `F5` — Refresh status
- `Space` — Toggle selected service

### Accessibility

- All status colors paired with shape/text (never color alone)
- WCAG 2.1 AA contrast ratios
- `aria-live="polite"` for service state changes
- All interactive elements min 44x44px touch target
- Respects `prefers-reduced-motion`

---

## 14. Plugin Architecture

### Plugin Types

1. **Service Plugins** — Add Redis, Memcached, MongoDB, PostgreSQL, Elasticsearch
2. **Framework Plugins (Drivers)** — Auto-detect Laravel, WordPress, Nette, Symfony
3. **GUI Extension Plugins** — Dashboard widgets, site detail panels
4. **CLI Extension Plugins** — `devforge laravel:new`, `devforge wp:install`
5. **Hook Plugins** — React to events (before/after site creation, service start/stop)

### Plugin Manifest (`plugin.toml`)

```toml
[plugin]
id = "devforge-redis"
name = "Redis"
version = "1.2.0"
type = "service"
min_devforge_version = "1.0.0"

[permissions]
network = true
filesystem = ["${DEVFORGE_HOME}/plugins/redis/"]
process = true
gui = false

[capabilities.service]
binary_name = "redis-server"
default_port = 6379
config_template = "templates/redis.conf.hbs"
health_check_cmd = "redis-cli ping"
health_check_ok = "PONG"
```

### Plugin Runtime

Plugins written in **Lua 5.4** (embedded via LuaJIT), sandboxed per-plugin. Host exposes stable API:

```lua
devforge.services.register(spec)     -- register a service
devforge.frameworks.register(driver) -- register framework detector
devforge.cli.register(command)       -- add CLI command
devforge.events.on(event, handler)   -- subscribe to events
devforge.store.set(key, value)       -- persistent storage
devforge.fs.read(path)               -- filesystem (restricted)
devforge.process.spawn(binary, args) -- process management
devforge.ipc.call(method, params)    -- daemon RPC
```

### Security Model

| Trust Level | Source | Signing | User Prompt |
|---|---|---|---|
| `marketplace` | Official registry | Required (registry key) | Permissions only |
| `community` | Third-party | Required (author key) | Permissions + key warning |
| `local` | Local directory | Not required | Full warning |
| `dev` | `--dev-plugin` flag | Not required | Suppressed |

All `devforge.fs.*` and `devforge.process.*` calls mediated by host — paths validated against declared permissions.

---

## 15. Security Model

### Threat Assessment Summary

| Area | Severity | Key Mitigation |
|------|----------|----------------|
| Privilege Escalation | **CRITICAL** | Split-process architecture |
| Process Isolation | **HIGH** | Per-site uid, open_basedir |
| Certificate Security | **HIGH** | Platform keystore for CA key |
| Configuration Integrity | **MEDIUM** | SHA-256 manifest, atomic writes |
| Network Security | **HIGH** | Default localhost-only binding |
| Plugin Security | **HIGH** | Sandboxed Lua, code signing |
| Data Protection | **MEDIUM** | Platform-native secrets storage |
| Supply Chain | **HIGH** | Pinned hashes, GPG verification |
| Update Security | **MEDIUM** | TUF framework, signed updates |

### Split-Process Architecture (CRITICAL)

```
┌──────────────────────────────────┐
│  Unprivileged Frontend           │  ← Runs as current user
│  (GUI/CLI, config editing,       │
│   plugin execution)              │
└──────────┬───────────────────────┘
           │ Local socket (HMAC-authenticated)
┌──────────▼───────────────────────┐
│  Privileged Helper (minimal)     │  ← Runs elevated
│  Commands allowed:               │
│  1. Write hosts file entries     │
│  2. Bind privileged ports        │
│  3. Install CA certificate       │
│  NOTHING ELSE                    │
└──────────────────────────────────┘
```

Platform integration:
- **Windows:** Helper as Windows Service, UAC at install. Named pipes with ACL.
- **macOS:** `SMJobBless` privileged helper. Authorization Services per operation.
- **Linux:** polkit with custom `.policy` files.

### MySQL Security Default

**CRITICAL CHANGE from competitors:** MySQL root gets a **randomly generated password** at first launch, displayed once, stored in platform secrets manager. No more empty root passwords.

### Default Network Binding

All services bind to `127.0.0.1` exclusively. Explicit user action + warning dialog required for `0.0.0.0`.

---

## 16. Performance Targets & Optimization

### Targets

| Metric | Target | Current Competitors |
|--------|--------|-------------------|
| Full stack cold start | < 3s | MAMP 4-8s, DDEV 15-45s |
| Site creation | < 1s | MAMP ~5s |
| PHP version switch | < 2s | MAMP ~10s |
| Memory (tool itself) | < 100MB | Electron 80-150MB |
| Memory (all services, 10 sites) | < 500MB | MAMP 300-500MB, DDEV 800-1500MB |

### Development-Optimized Defaults

```ini
# PHP-FPM (per-site pool)
pm = ondemand
pm.max_children = 3
pm.process_idle_timeout = 30s

# MySQL
innodb_buffer_pool_size = 128M
max_connections = 20
performance_schema = OFF
skip_log_bin = ON

# Nginx
worker_processes = 2
worker_connections = 256
```

### Top 3 Optimizations (Ranked by Impact)

1. **`ondemand` PHP-FPM pools** — saves 400-600MB across 10+ sites
2. **`performance_schema = OFF` in MySQL** — saves 100-200MB
3. **Parallel service startup** — saves 1-3 seconds

---

## 17. Testing Strategy

### Test Categories & Coverage Targets

| Module | Branch Coverage | Line Coverage |
|--------|----------------|--------------|
| Config template engine | 90% | 95% |
| Domain/port validation | 95% | 95% |
| Service lifecycle | 80% | 85% |
| CLI argument parser | 90% | 95% |
| GUI components | 70% | 75% |

### Test Matrix (CI/CD)

| Category | Win 2022 | macOS 14 | Ubuntu 24.04 |
|----------|----------|----------|-------------|
| Unit | YES | YES | YES |
| Integration | YES | YES | YES |
| E2E (CLI) | YES | YES | YES |
| E2E (GUI) | YES | YES | YES |
| Performance | YES | YES | YES |
| Security | NO | YES | YES |
| Chaos | NO | YES | YES |

### Critical Test Cases

1. **Config injection guard** — reject domain with newline/null byte/path traversal
2. **PHP version switch** — verify new version active within 5s, old socket cleaned
3. **Startup benchmark** — cold start < 3000ms, warm reload < 500ms
4. **Crash recovery** — kill Apache → auto-restart within 3s, site accessible
5. **Full E2E** — create site → enable SSL → browser loads HTTPS without warning

### Quality Gates

- Coverage below module targets → build blocked
- Security test passes malicious input → build blocked
- Startup exceeds 3000ms by >10% → build blocked
- Playwright screenshot diff >0.5% → build blocked

---

## 18. Packaging & Distribution

### Portable Directory Structure

```
DevForge/
├── bin/
│   ├── php/
│   │   ├── 5.6.40/    (30MB)
│   │   ├── 7.4.33/    (35MB)
│   │   ├── 8.2.21/    (45MB)
│   │   ├── 8.3.9/     (48MB)
│   │   └── 8.4.1/     (50MB)
│   ├── apache/2.4.x/  (15MB)
│   ├── nginx/1.26/    (3MB)
│   ├── mysql/8.0/     (100MB)
│   └── mariadb/11.4/  (80MB)
├── etc/
│   ├── devforge.toml
│   ├── sites/          (one TOML per site)
│   └── generated/      (ephemeral, reconstructible)
├── data/
│   ├── mysql/
│   └── state.db
├── ssl/
│   ├── ca/
│   └── sites/
├── log/
├── plugins/
├── devforged[.exe]     (daemon)
├── devforge[.exe]      (CLI)
└── DevForge[.exe]      (GUI)
```

### Installers

| Platform | Format | Size |
|----------|--------|------|
| Windows | NSIS installer + portable .7z | ~1.2GB compressed |
| macOS | DMG + Homebrew cask | ~800MB |
| Linux | AppImage + .deb/.rpm | ~600MB |

### Auto-Update System

- Update manifest JSON on CDN (24h cache)
- Delta updates for changed components
- Code-signed with Ed25519 (TUF framework)
- Rollback: keep last 2 versions
- Channels: stable, beta, nightly

### Binary Integrity

All downloaded binaries verified against pinned SHA-256 hashes. GPG verification where available (PHP, MySQL).

### Windows Antivirus False Positive Mitigation (CRITICAL)

Go binaries on Windows trigger Microsoft Defender false positives (Wacatac.B!ml / Wacapew.C!ml). This is a **known, unresolved issue** — [microsoft/go#1255](https://github.com/microsoft/go/issues/1255) is OPEN as of Sep 2025. Microsoft Go team confirmed: **no compiler-level fix possible**, it's a Defender ML heuristic problem.

**Key findings from research:**
- Code signing has **minimal impact** — "signing doesn't really make much of a difference, it seems random"
- Garble obfuscation **worsens** detection (flagged as "WinGo/Packed.Obfuscated.D")
- `-ldflags="-s -w"` alone can **trigger** more detections
- Tauri/Electron NSIS installers have the **same problem** ([tauri#2486](https://github.com/tauri-apps/tauri/issues/2486))
- **MSI installers raise fewer false positives** than NSIS .exe installers

**Multi-layer mitigation strategy (all required):**

1. **EV Code Signing Certificate** (~$350-700/year) — provides SmartScreen reputation faster than OV
2. **MSI installer instead of NSIS** — fewer AV triggers (WiX toolset for MSI generation)
3. **`-trimpath` build flag** — removes local paths from binary
4. **DO NOT use `-ldflags="-s -w"`** — stripping debug info paradoxically triggers more detections
5. **Microsoft Defender submission portal** — submit every release binary for allowlisting
6. **VirusTotal pre-check** — scan in CI before publishing release
7. **Windows SmartScreen reputation building** — consistent signing with same EV cert builds reputation over time
8. **User documentation** — include "antivirus false positive" FAQ with restore instructions

```bash
# Production build (DO NOT strip symbols — increases false positives!)
go build -trimpath -o devforged.exe ./cmd/daemon
signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 devforged.exe

# Verify with VirusTotal before release
vt scan file devforged.exe
```

**Budget:** EV Code Signing Certificate ~$350-700/year. Microsoft submission: free.

| Binary | Risk Level | Reason | Installer Format |
|--------|-----------|--------|-----------------|
| devforged.exe | **HIGH** | Long-running daemon, spawns processes, named pipes | Bundled in MSI |
| devforge.exe | MEDIUM | CLI tool, short-lived | Bundled in MSI |
| DevForge installer | **HIGH** | Writes to Program Files | **MSI (not NSIS)** |

**Sources:** [microsoft/go#1255](https://github.com/microsoft/go/issues/1255), [tauri#2486](https://github.com/tauri-apps/tauri/issues/2486), [Tauri False Positives Guide](https://tauri.by.simon.hyll.nu/concepts/security/false_positives/)

### CDN Infrastructure

- Primary: AWS S3 + CloudFront
- Fallback: GitHub Releases
- Estimated: ~5TB/month for 10K active users (~$310/month)

---

## 19. Legal & Licensing

### License Compatibility Matrix

| Component | License | Permissive? | Commercial OK? | Must Share Modifications? | Source Offer Required? |
|---|---|---|---|---|---|
| Apache HTTP Server | Apache-2.0 | Yes | Yes | No | No (NOTICE only) |
| Nginx | BSD-2-Clause | Yes | Yes | No | No |
| PHP | PHP-3.01 | Yes | Yes | No | No |
| MySQL Community | GPLv2 (+FOSS Exception) | **No** | **Conditional** | **Yes** | **Yes** |
| MariaDB | GPLv2 | **No** | **Conditional** | **Yes** | **Yes** |
| phpMyAdmin | GPLv2 | **No** | **Conditional** | **Yes** | **Yes** |
| mkcert | BSD-3-Clause | Yes | Yes | No | No |
| dnsmasq | GPLv2/v3 | **No** | **Conditional** | **Yes** | **Yes** |
| OpenSSL 3.x | Apache-2.0 | Yes | Yes | No | No |
| Tauri | Apache-2.0/MIT | Yes | Yes | No | No |

### Critical Compatibility Notes

**Apache-2.0 + GPLv2:** GPLv2 components and Apache-2.0 components coexist as **separate programs** in aggregate distribution (GPLv2 Section 2, "mere aggregation" clause). DevForge launching MySQL as a separate process ≠ derivative work. Same model used by MAMP, XAMPP, Laragon.

**Redis 7.4+ (RSALv2/SSPL):** Prohibits managed service offering. **Recommendation:** Bundle Valkey (BSD-3-Clause fork) instead of Redis >= 7.4.

**PHP License v3.01:** Naming restriction — cannot use "PHP" in derivative product names without permission.

### Recommended Distribution Model: Open Core

| Layer | License | Content |
|-------|---------|---------|
| **Core** | Apache-2.0 | CLI, daemon, config engine, basic GUI |
| **Premium** | Proprietary | Cloud sync, team management, advanced SSL, GUI themes |
| **GPL binaries** | GPLv2 (unmodified) | MySQL, MariaDB, phpMyAdmin, dnsmasq |

### GPL Compliance Requirements

1. **Source code offer** — written offer valid 3 years, covering exact versions bundled
2. **No modification lock-in** — patches to GPL components must be published under GPLv2
3. **Host source archives** or provide download links to upstream exact versions
4. **NOTICE file** — list all components, versions, licenses in `THIRD-PARTY-LICENSES`

### Trademark Usage

| Mark | Owner | Usage Rule |
|------|-------|-----------|
| PHP | The PHP Group | Descriptive OK, cannot name product "*PHP*" |
| MySQL | Oracle Corp. | Descriptive OK + ™ symbol, no logo |
| Apache | ASF | Must say "Apache HTTP Server" (not just "Apache") |
| Nginx | F5, Inc. | Descriptive OK, no endorsement implication |
| MariaDB | MariaDB Foundation | Descriptive OK, logo requires permission |

### Privacy & Telemetry

- **GDPR:** Opt-in consent, data minimization, right to deletion, DPA with analytics processor
- **CCPA:** Disclosure at collection, opt-out right
- **Minimum:** Privacy policy disclosing what/why/who/retention/rights

### Contributor License Agreement

- **Apache ICLA recommended** — ensures relicensing for proprietary premium layer
- **Originality warranty** — prohibits pasting GPL code into Apache-2.0 codebase
- **DCO (Developer Certificate of Origin)** as lighter alternative

---

## 20. Implementation Roadmap

### Phase 1 — Foundation (Weeks 1-3)

- [ ] Go workspace: `devforged`, `devforge-cli`, `devforge-pal`, `devforge-config`
- [ ] Platform Abstraction Layer: process spawning, Job Objects (Win), elevation
- [ ] Config pipeline: TOML parsing, template rendering, atomic writes
- [ ] JSON-RPC service + basic daemon skeleton
- [ ] SQLite schema + migration system

### Phase 2 — Core Services (Weeks 4-6)

- [ ] Apache ServiceUnit: config gen, `httpd -t` validation, start/stop/reload
- [ ] PHP-FPM management: multi-version install, per-site pools
- [ ] MySQL ServiceUnit: config gen, data dir init, start/stop
- [ ] Health check loop + auto-restart logic
- [ ] Port conflict detection

### Phase 3 — Site Management (Weeks 7-8)

- [ ] VHost CRUD through API
- [ ] SSL module (mkcert integration)
- [ ] DNS/hosts module with elevation helper
- [ ] CLI client: all `site:*`, `service:*`, `php:*` commands
- [ ] Import/export configuration

### Phase 4 — GUI (Weeks 9-11)

- [ ] Tauri app scaffold with Svelte 5
- [ ] Dashboard, Sites Manager, PHP Manager screens
- [ ] System tray integration
- [ ] Live log streaming via JSON-RPC
- [ ] Command palette (Ctrl+K)

### Phase 5 — Polish & Plugins (Weeks 12-14)

- [ ] Nginx ServiceUnit
- [ ] MariaDB ServiceUnit
- [ ] Plugin host (Lua runtime)
- [ ] First plugins: Redis, Laravel driver, WordPress driver
- [ ] Installer/updater (NSIS Win, DMG macOS)

### Phase 6 — Beta & Launch (Weeks 15-18)

- [ ] Public beta
- [ ] MAMP PRO migration tool
- [ ] Documentation site (Docusaurus)
- [ ] Plugin marketplace
- [ ] Community feedback integration
- [ ] v1.0.0 release

### Estimated Effort

- **MVP (Phase 1-3):** 2 developers × 8 weeks
- **Full v1.0 (all phases):** 2-3 developers × 18 weeks
- **Ongoing:** 1 developer for maintenance + community

---

## 21. Risk Analysis

| Risk | Probability | Impact | Mitigation |
|------|------------|--------|------------|
| Windows UAC friction | High | Medium | Minimize elevation requests, batch operations |
| PHP binary compatibility | Medium | High | Test matrix across Windows versions, VC++ bundles |
| Apache/Nginx config complexity | Medium | Medium | Template validation catches errors pre-apply |
| Electron alternative (Tauri) maturity | Low | High | Tauri v2 is stable; fallback to Electron possible |
| FlyEnv as competitor | Medium | Medium | Focus on config validation + CLI as differentiators |
| MySQL licensing changes | Low | Medium | MariaDB as default, MySQL optional |
| Plugin security vulnerabilities | Medium | High | Lua sandbox + permission model + code signing |
| Cross-platform parity | High | Medium | PAL abstraction + comprehensive CI matrix |

---

## Appendices

### Appendix A: Full DDL

(See Section 11 for core tables. Full DDL including triggers, views, seed data, and migration system available in `docs/schema/`)

### Appendix B: API Reference

(Complete JSON-RPC method documentation with request/response examples in `docs/api/`)

### Appendix C: Design Tokens

(Full CSS custom property definitions for dark/light themes in `docs/design/tokens.css`)

### Appendix D: Documentation Suite (Created)

The Technical Writer agent created **10 documentation files** (~15,000 words total):

| File | Words | Content |
|------|-------|---------|
| `docs/README.md` | 1,200 | Project overview, features, quick start |
| `docs/getting-started.md` | 3,200 | Installation (3 OS × 3 methods), first-run wizard |
| `docs/troubleshooting.md` | 2,800 | 40+ solutions in 8 categories |
| `docs/migration-mamp-pro.md` | 2,400 | 10-step migration, DB backup, config updates |
| `docs/TABLE-OF-CONTENTS.md` | 1,900 | Complete 40+ topic roadmap |
| `docs/DOCUMENTATION-SUMMARY.md` | 2,000 | Quality metrics, phase roadmap |
| `docs/DELIVERY-REPORT.md` | 2,000 | Success criteria, platform coverage |
| `docs/00-START-HERE.md` | 250 | Quick orientation entry point |
| `docs/INDEX.md` | 400 | Topic-based navigation |
| `docs/FILES-MANIFEST.md` | 500 | Complete file catalog |

**Content highlights:** 80+ code examples, 40+ troubleshooting entries, 9 installation methods, 25+ CLI commands documented.

### Appendix E: Legal Analysis Summary

Full legal analysis covering license compatibility, distribution models, GPL compliance, trademark usage, GDPR/CCPA requirements, and CLA recommendations. Key decision: **Open Core model** (Apache-2.0 core + proprietary premium + GPL binaries as separate processes).

---

## Sources

- [Kinsta - 8 Best MAMP Alternatives in 2026](https://kinsta.com/blog/mamp-alternative/)
- [DEV.to - XAMPP vs Laragon vs Laravel Herd](https://dev.to/nassiry/xampp-vs-laragon-vs-laravel-herd-which-one-should-you-use-for-php-and-laravel-projects-4j8k)
- [FlyEnv GitHub - 2.7k stars, 50+ modules](https://github.com/xpf0000/FlyEnv)
- [FlyEnv Documentation](https://www.flyenv.com/guide/what-is-flyenv.html)
- [Tauri v2 Architecture](https://v2.tauri.app/concept/architecture/)
- [ServBay - PHP 5.6-8.5 support](https://www.servbay.com/)
- [AlternativeTo - MAMP Alternatives](https://alternativeto.net/software/mamp/)
- [ServBay vs Laragon Comparison](https://www.servbay.com/vs/laragon)
- [Laravel Herd Alternatives](https://alternativeto.net/software/laravel-herd/)

---

*Document generated by 15 parallel specialist agents: Technical Researcher, Code Architect, Security Auditor, UI Designer, Backend Architect, Performance Engineer, API Designer, Database Architect, Network Engineer, Architecture Modernizer, Technical Writer, Test Engineer, Deployment Engineer, Legal Advisor, and Web Research.*
