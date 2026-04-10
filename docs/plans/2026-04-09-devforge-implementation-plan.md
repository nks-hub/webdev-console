# NKS WebDev Console - Complete Implementation Plan & Documentation

**Version:** 1.0.0  
**Date:** 2026-04-09  
**Status:** Implementation Plan  
**Compiled from:** SPEC.md + interview results + Avalonia ecosystem research  

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

**NKS WebDev Console** is a modern, portable local development server management tool designed to replace MAMP PRO, XAMPP, WampServer, and similar tools. It addresses critical pain points discovered through real-world usage of existing tools:

- **Config corruption** (MAMP PRO's SQLite stores vhosts with empty values → Apache syntax errors)
- **SSL complexity** (manual cert generation, trust store management)
- **PHP OpenSSL failures** on Windows (EC key generation broken in MAMP's bundled OpenSSL)
- **Two-config confusion** (MAMP's dual httpd.conf files)
- **No automation** (no CLI interface in MAMP PRO)
- **Heavy footprint** (Docker-based tools consume 1-2GB+ baseline)

### Core Value Proposition

| Feature | MAMP PRO | XAMPP | Laragon | FlyEnv | **NKS WebDev Console** |
|---------|----------|-------|---------|--------|-------------|
| Multi-PHP (5.6-8.4) | 2 versions | Single | Multiple | Multiple | **Multiple, per-site** |
| Virtual Hosts | GUI only | Manual | Auto .test | GUI | **CLI + GUI + API** |
| SSL Certs | Manual | Manual | 1-click mkcert | Auto | **Auto mkcert + CA mgmt** |
| Config Validation | None | None | None | Unknown | **3-stage: parse→render→dry-run** |
| Portable | Partial | Partial | Yes | Yes | **Yes (copy folder)** |
| CLI | None | None | None | Limited | **Full gRPC CLI** |
| Plugin System | None | None | None | Custom modules | **Extensible C# plugins via AssemblyLoadContext** |
| Reverse Proxy | None | None | None | Podman | **Traefik integration** |
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
| **DDEV** | Docker | All | Apache-2.0 | Docker isolation, CI/CD friendly | Docker overhead (1-2GB RAM), 15-45s startup |

#### Tier 3 — Specialized/Lightweight

| Tool | Platform | Notes |
|------|----------|-------|
| **Laravel Valet** | macOS only | CLI-only, lightweight, dnsmasq-based |
| **LocalWP** | Win, macOS | WordPress-only, site isolation |
| **DevKinsta** | Win, macOS | WordPress + Kinsta hosting |

### Key Market Insights

1. **FlyEnv is the closest competitor** — 50+ modules, cross-platform, BSD-3, but uses Electron (heavy)
2. **No tool validates configs before applying** — this is NKS WebDev Console's killer feature
3. **Laragon is beloved but Windows-only** with uncertain future
4. **Docker tools (DDEV) are powerful but too heavy** for simple PHP dev
5. **Per-site PHP version** via FPM pools is now table stakes
6. **CLI + automation** is increasingly demanded (CI/CD, scripting)

### NKS WebDev Console Positioning

**"The Laragon experience, everywhere, with the safety of config validation and the power of a plugin ecosystem."**

Target audience: PHP developers who want native performance (not Docker overhead), multiple PHP versions, and scriptable automation.

---

## 3. Technology Stack Decision

### Core Engine: C# / .NET 9 (FINAL DECISION)

**Single-language, unified stack for daemon + GUI + CLI**

| Criteria | Go | Rust | **C# (.NET 9)** | Node.js |
|----------|------|------|-----------|---------|
| Process management | Excellent (`os/exec`, goroutines) | Excellent (async, tokio) | **Excellent** (`System.Diagnostics.Process`, Task-based) | Poor (single-threaded) |
| Cross-compilation | `GOOS=windows go build` | `cargo build --target` | **Native (platform-specific publish)** | Requires Node.js |
| Binary size | ~10MB | ~5MB | **15-25MB** (self-contained, NO trimming) | N/A (needs runtime) |
| Startup speed | Fast | Fastest | **Moderate (JIT warm-up)** | Moderate |
| Developer ecosystem | Large, familiar | Growing | **Large (Windows-centric, cross-platform improving)** | Largest |
| Concurrency | Goroutines (easy) | async/await (complex) | **async/await (first-class, mature)** | Event loop |
| Dependency injection | Manual | Manual | **Built-in (Microsoft.Extensions)** | npm |
| SQLite | `sqlite` (C FFI) | `rusqlite` | **Microsoft.Data.Sqlite + Dapper** | better-sqlite3 |
| IPC | Named pipe (native) | TcpStream | **gRPC + named pipes (built-in Kestrel)** | TCP |
| Daemon framework | Manual goroutine loops | Manual tokio | **IHostedService (mature pattern)** | Manual event loop |

**Decision Rationale:**
1. **AV False Positive Safety** — Go binaries trigger Microsoft Defender heuristics (Wacatac.B!ml, Wacapew.C!ml); .NET Framework Dependent Execution (FDE) not flagged even on unsigned binaries. Critical for product reliability.
2. **Single Language Everywhere** — Daemon (Worker Service) + GUI (Avalonia UI XAML/C#) + CLI (System.CommandLine, Spectre.Console) = zero polyglot friction, unified testing, shared domain models.
3. **Native Process Management** — `System.Diagnostics.Process` + `TaskCompletionSource` is gold standard for Windows process control; `Task` model elegantly maps to per-service lifecycle management.
4. **IPC/RPC Excellence** — gRPC + protobuf over named pipes provides better performance and type safety than JSON-RPC; works natively on Windows with Kestrel named pipe listener.
5. **GUI Maturity** — Avalonia UI 12.x reaches production-grade stability with Fluent Design System, native tray support, and DataGrid control built-in. CommunityToolkit.Mvvm ecosystem is robust.
6. **Developer Productivity** — C# async/await, LINQ, and dependency injection reduce ceremony vs. Go; IDE support (Visual Studio Community, Rider) is unmatched.

### GUI Framework: Avalonia UI 12.x (SELECTED)

**Avalonia UI v12.0.0+ (April 2026 release)**

| Aspect | Details |
|--------|---------|
| **Framework** | Avalonia UI 12.0.0 |
| **Styling** | Fluent Dark/Light theme (built-in `RequestedThemeVariant`) |
| **System Tray** | Native `TrayIcon` control with `NativeMenu` |
| **Charts** | LiveCharts2.SkiaSharp.Avalonia (2.0.0; **VERIFY Avalonia 12 compat day 1**) |
| **DataGrid** | `Avalonia.Controls.DataGrid` (separate NuGet) |
| **MVVM** | CommunityToolkit.Mvvm (source generators) |
| **Hot Reload** | HotAvalonia 3.1.0 (Debug only) |

**Why Avalonia (vs. Flutter / PySide6 / Slint):**
- **No WebView** — native rendering via Skia; matches developer preference ("neni to prava nativni appka")
- **C# native** — unified with daemon and CLI
- **TrayIcon built-in** — must-have per interview
- **Fluent theme** — professional dark/light switch
- **MIT license** — commercial-friendly
- **$3M Devolutions backing** — long-term stability

**Known issues & mitigations:**
- LiveCharts2 2.0.0 declares `>= Avalonia 11.0.0` but Avalonia 12 compat unconfirmed. **Day-1 verification checklist:** Test LiveCharts2 against Avalonia 12.0.0 in a minimal app. Fallback options: ScottPlot.Avalonia, OxyPlot.Avalonia (both tested against 12.x).
- No official XAML hot reload in open-source; HotAvalonia provides community solution.
- Linux system tray varies by desktop environment (Wayland unsupported in many DE's).

### IPC Protocol: gRPC (NOT JSON-RPC)

**Benefits vs. JSON-RPC 2.0:**
- **Typed contracts** — protobuf defines request/response schema; no stringly-typed JSON
- **Binary efficiency** — gRPC compression reduces bandwidth vs. JSON
- **Streaming** — server-sent events (logs, metrics) via `stream` keyword
- **HTTP/2 multiplexing** — multiple concurrent RPC calls on single connection
- **Tooling** — `grpcurl` CLI for debugging, code generation from `.proto` files

**Transport (platform-specific):**
- Windows: Named pipe `\\.\pipe\nks-wdc-daemon`
- macOS/Linux: Unix domain socket `~/.nks-wdc/daemon.sock`

Both use standard gRPC over HTTP/2; no custom wire protocol.

### Key NuGet Packages (Core Stack)

**NKS.WebDevConsole.Core (shared contracts):**
- `Google.Protobuf` 3.x
- `Grpc.Tools` (code generation)
- `Tomlyn` (TOML parsing for config files)

**NKS.WebDevConsole.Daemon (service):**
- `Grpc.AspNetCore` 2.76.0+ (gRPC server via Kestrel)
- `Microsoft.Data.Sqlite` 9.0+ (SQLite client)
- `Dapper` 2.1+ (micro-ORM for queries)
- `Scriban` 7.1.0+ (template engine for Apache/Nginx configs)
- `CliWrap` 3.10.1+ (subprocess: `httpd -t`, `mysqladmin`, `mkcert`)
- `Microsoft.Extensions.Hosting` (Worker Service)
- `Microsoft.Extensions.Logging` (structured logging)
- `Serilog` 4.3+ + `Serilog.Sinks.File` 7.0+ (file logging)
- `dbup-sqlite` 6.0+ (schema migrations)

**NKS.WebDevConsole.Gui (Avalonia app):**
- `Avalonia` 12.0.0
- `Avalonia.Desktop` 12.0.0
- `Avalonia.Themes.Fluent` 12.0.0
- `Avalonia.Controls.DataGrid` 12.0.0
- `LiveChartsCore.SkiaSharpView.Avalonia` 2.0.0 (**with day-1 compat check**)
- `Grpc.Net.Client` 2.76.0+
- `CommunityToolkit.Mvvm` 8.x (source generators)
- `HotAvalonia` 3.1.0 (Debug conditional)

**NKS.WebDevConsole.Cli (command-line):**
- `System.CommandLine` 2.0.5+ (command parsing)
- `Spectre.Console` 0.55.0 (output formatting: tables, progress bars, colors)
- `Grpc.Net.Client` 2.76.0+
- `CliWrap` 3.10.1+

**NKS.WebDevConsole.Tests:**
- `xunit` 2.6+
- `Moq` 4.x
- `Avalonia.Headless.XUnit`
- `Microsoft.Data.Sqlite` (in-memory for DB tests)

---

## 4. System Architecture

### Core Principle: Daemon Is Source of Truth

Neither the GUI nor CLI ever modify config files, spawn services, or touch the hosts file directly. Every mutation goes through the daemon's gRPC API. The daemon is the single source of truth.

### Component Diagram

```
┌──────────────────────────────┐   ┌────────────────────────────────┐
│  NKS.WebDevConsole.Gui (Avalonia)     │   │  NKS.WebDevConsole.Cli (System.CommandLine)
│  - Main window               │   │  - wdc start apache       │
│  - System tray               │   │  - wdc new myapp.loc      │
│  - LiveCharts2 metrics       │   │  - wdc db:import mydb ... │
└──────────┬───────────────────┘   └────────────┬───────────────────┘
           │ gRPC over named pipe               │ gRPC over named pipe
           │ (Windows: \\.\pipe\nks-wdc)       │ (macOS/Linux: unix socket)
           └──────────┬────────────────────────┘
                      │
           ┌──────────▼──────────────────────────────────────┐
           │  NKS.WebDevConsole.Daemon (Worker Service)                │
           │                                                  │
           │  ProcessManager   HealthMonitor   MetricsCollector  │
           │  ┌────────────┐  ┌────────────┐  ┌───────────────┐ │
           │  │ Apache     │  │ Nginx      │  │ MySQL         │ │
           │  │ Module     │  │ Module     │  │ Module        │ │
           │  ├────────────┤  ├────────────┤  ├───────────────┤ │
           │  │ PHP-FPM    │  │ Redis      │  │ Mailpit       │ │
           │  │ Module     │  │ Module     │  │ Module        │ │
           │  └────────────┘  └────────────┘  └───────────────┘ │
           │                                                  │
           │  ConfigEngine     SslManager    HostsFileManager    │
           │  PluginLoader     DbManager     DnsFlush            │
           │                                                  │
           │  SQLite (state.db)                                   │
           └──────────────────────────────────────────────────────┘
```

### Solution Structure

```
WebDevConsole.sln
src/
├── NKS.WebDevConsole.Core/              # Shared types, interfaces, config models
│   ├── Models/                 # Site, Service, PhpVersion, Certificate, Database
│   ├── Interfaces/             # IServiceModule, IConfigProvider, IHostsManager
│   ├── Configuration/          # AppConfig, SiteConfig, TOML loading
│   └── Proto/                  # .proto files for gRPC (shared between daemon and clients)
│
├── NKS.WebDevConsole.Daemon/            # Background service — owns all child processes
│   ├── Program.cs              # Worker Service host entry point
│   ├── Services/               # ProcessManager, HealthMonitor, MetricsCollector
│   ├── Modules/                # ApacheModule, NginxModule, MySqlModule, PhpFpmModule, RedisModule
│   ├── Grpc/                   # gRPC server implementations
│   ├── Config/                 # TemplateEngine (Scriban), ConfigValidator, AtomicWriter
│   ├── Ssl/                    # MkcertManager, CertificateTracker
│   ├── Dns/                    # HostsFileManager, DnsFlush
│   ├── Db/                     # DatabaseManager, BackupScheduler
│   └── Plugin/                 # PluginLoader (AssemblyLoadContext), PluginHost
│
├── NKS.WebDevConsole.Gui/               # Avalonia desktop application
│   ├── App.axaml               # Application entry, theme registration
│   ├── ViewModels/             # MVVM ViewModels (CommunityToolkit.Mvvm)
│   ├── Views/                  # .axaml views per screen
│   ├── Controls/               # Reusable controls: ServiceCard, SiteCard, PhpVersionBadge
│   └── Services/               # GrpcClientService, ThemeService, NotificationService
│
├── NKS.WebDevConsole.Cli/               # CLI client (System.CommandLine)
│   ├── Program.cs
│   └── Commands/               # SiteCommand, ServiceCommand, PhpCommand, DbCommand, SslCommand
│
└── NKS.WebDevConsole.Tests/
    ├── Core.Tests/
    ├── Daemon.Tests/
    ├── Cli.Tests/
    └── Gui.Tests/              # Avalonia Headless testing
```

### IPC Transport

| Platform | Transport |
|---|---|
| Windows | Named pipe `\\.\pipe\nks-wdc-daemon` |
| macOS / Linux | Unix domain socket `~/.nks-wdc/daemon.sock` |

gRPC runs over these transports using standard Kestrel listeners configured at daemon startup:

```csharp
builder.WebHost.ConfigureKestrel(opts =>
{
    if (OperatingSystem.IsWindows())
        opts.ListenNamedPipe("nks-wdc-daemon");
    else
    {
        var sock = Path.Combine(Path.GetTempPath(), "nks-wdc.sock");
        if (File.Exists(sock)) File.Delete(sock);
        opts.ListenUnixSocket(sock);
    }
    opts.ConfigureEndpointDefaults(o => o.Protocols = HttpProtocols.Http2);
});
```

### Daemon Lifecycle

1. Check for existing PID lock (`~/.nks-wdc/daemon.pid`). If stale, clean up.
2. Write PID lock.
3. Open SQLite database, run pending migrations.
4. Start gRPC server on transport.
5. Load plugins via AssemblyLoadContext.
6. Start services marked `auto_start = 1` in parallel.
7. Start HealthMonitor loop (5-second interval).
8. Block until cancellation token fired (SIGTERM / Windows stop).
9. Shutdown in reverse order: web servers → PHP-FPM → MySQL → others.
10. Release PID lock.

---

## 5. Core Engine - Service Management

### ProcessManager & ServiceUnit

Each service (Apache, Nginx, MySQL, PHP-FPM, Redis, Mailpit) is represented as a `ServiceUnit`:

```csharp
public enum ServiceState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Crashed,
    Restarting,
    Disabled
}

public class ServiceUnit
{
    public string Id { get; set; }
    public ServiceState State { get; set; }
    public int? Pid { get; set; }
    public Process? Process { get; set; }
    public RingBuffer<string> LogBuffer { get; set; }  // last 1000 lines
    public int RestartCount { get; set; }
    public DateTime? LastCrash { get; set; }
    public IServiceModule Module { get; set; }
}
```

**State Machine:**

```
STOPPED → start() → STARTING → ready() → RUNNING
                    ↓ fail()              ↓ crash()
                  CRASHED ←─────────── CRASHED
                    ↓ (within restart threshold)
                  RESTARTING → STARTING

RUNNING → stop() → STOPPING → done() → STOPPED
                   ↓ timeout(10s)
                   SIGKILL → STOPPED
```

**Restart Policy:**

```csharp
public class RestartPolicy
{
    public int MaxRestarts { get; set; } = 5;
    public TimeSpan Window { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan BackoffBase { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan BackoffMax { get; set; } = TimeSpan.FromSeconds(30);
}
```

If `RestartCount > MaxRestarts` within `Window` → transition to `Disabled`, fire `service.degraded` event.

**Windows Job Objects:** On Windows, wrap each spawned process in a Job Object with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`. This ensures child processes (e.g., Apache worker processes) are killed when the daemon exits, even if killed ungracefully.

### IServiceModule Interface

Every service (Apache, Nginx, MySQL, PHP-FPM, Redis, etc.) implements this interface:

```csharp
public interface IServiceModule
{
    string ServiceId { get; }
    string DisplayName { get; }
    ServiceType Type { get; }

    Task<ValidationResult> ValidateConfigAsync(CancellationToken ct);
    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
    Task ReloadAsync(CancellationToken ct);       // graceful config reload
    Task<ServiceStatus> GetStatusAsync(CancellationToken ct);
    Task<IReadOnlyList<string>> GetLogsAsync(int lines, CancellationToken ct);

    // Optional: CLI commands registered by this module
    IReadOnlyList<CliCommandDefinition>? CliCommands => null;
}

public record ServiceStatus(
    ServiceState State,
    int? Pid,
    TimeSpan Uptime,
    int RestartCount,
    double CpuPercent,
    long MemoryBytes);
```

### HealthMonitor

Every 5 seconds per service:
- Check PID is alive (`Process.GetProcessById(pid)`)
- HTTP check on the service port if applicable (Apache: HEAD request to localhost)
- MySQL: `SELECT 1` via `MySqlConnector`
- PHP-FPM: ping via FastCGI

On failure: transition state to `Crashed`, trigger `RestartPolicy`.

Metrics collected per service:
- CPU % (via `Process.TotalProcessorTime`)
- Memory bytes (via `Process.WorkingSet64`)
- Uptime seconds
- Request count (Apache/Nginx: tail access log)

---

## 6. Configuration Pipeline

### Design Principle: Render → Validate → Apply

All config generation follows this 3-stage pipeline:

```
1. RENDER (Scriban template + user data)
       ↓
2. VALIDATE (httpd -t, syntax checks, schema validation)
       ↓
3. APPLY (atomic write, backup previous gen, notify services)
```

**Example: Apache VirtualHost generation**

Template (`vhost.conf.scriban`):
```
<VirtualHost *:{{ port }}>
    ServerName {{ site.domain }}
    ServerAlias {{ site.aliases | join ", " }}
    DocumentRoot "{{ site.root }}"
    
    {{ if site.ssl }}
    SSLEngine on
    SSLCertificateFile "{{ site.cert_path }}"
    SSLCertificateKeyFile "{{ site.key_path }}"
    {{ end }}
    
    {{ if site.php_enabled }}
    <FilesMatch "\.php$">
        SetHandler "proxy:unix:/var/run/php{{ site.php_version | replace "." "" }}-fpm.sock|fcgi://localhost"
    </FilesMatch>
    {{ end }}
    
    {{ for redirect in site.redirects }}
    Redirect 301 {{ redirect.from }} {{ redirect.to }}
    {{ end }}
</VirtualHost>
```

Rendering (`Scriban.Template.Parse(templateText).Render(model)`):
```csharp
var template = Template.Parse(templateText);
var config = await template.RenderAsync(new
{
    site = siteModel,
    port = 443,
    redirects = new[] { ... }
});
```

Validation:
```csharp
// Stage 2: Validate Apache config syntax
var validateCmd = CliWrap.Cli.Wrap("httpd")
    .WithArguments(new[] { "-t", "-f", tempConfigPath });
var result = await validateCmd.ExecuteAsync();
if (result.ExitCode != 0) throw new ConfigValidationException(result.StandardError);

// Stage 3: Atomic write (backup old, write new)
BackupConfig(configPath);
await File.WriteAllTextAsync(configPath, config);
```

### Per-Site Config Files

Instead of a single monolithic `httpd.conf`, NKS WebDev Console generates:

```
~/.nks-wdc/apache/
├── httpd.conf               (generated base config)
├── vhosts/
│   ├── nks-web.loc.conf     (site 1)
│   ├── chatujme.loc.conf    (site 2)
│   └── ...
├── backups/
│   ├── vhosts/
│   │   ├── nks-web.loc.conf.1  (generation N-1)
│   │   └── ...
```

**Benefit:** Changing one vhost never corrupts others. Backup generations prevent "restart overwrites my fix" bugs.

### ConfigValidator

Validates not only syntax but also:
- Site root directory exists
- PHP version file exists
- SSL cert file readable
- Port not in use by another site
- DNS entries resolvable

---

## 7. Virtual Host Manager

### VirtualHost Model

```csharp
public class SiteConfig
{
    public string Domain { get; set; }               // e.g., "nks-web.loc"
    public string Root { get; set; }                 // e.g., "/home/user/projects/nks-web/public"
    public string[] Aliases { get; set; }            // e.g., ["*.nks-web.loc"]
    public PhpVersion PhpVersion { get; set; }      // e.g., "8.2"
    public bool Ssl { get; set; }                    // HTTPS enabled
    public string? CertPath { get; set; }            // mkcert-generated cert
    public string? KeyPath { get; set; }
    public int HttpPort { get; set; } = 80;
    public int HttpsPort { get; set; } = 443;
    public string[] Redirects { get; set; }          // old domain → new domain
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? Framework { get; set; }           // "Nette", "Laravel", "WordPress", detected on create
}
```

### Site Wizard (CLI + GUI)

**CLI:**
```bash
wdc new myapp.loc \
    --root ~/projects/myapp \
    --php=8.2 \
    --ssl \
    --db \
    --framework=nette
```

**GUI:**
1. Enter domain (e.g., `myapp.loc`)
2. Select root folder (file picker) — auto-detect framework
3. Select PHP version (dropdown)
4. Toggle SSL (auto-generates cert)
5. Toggle Create DB (auto-creates + sets `DB_HOST=127.0.0.1`)
6. Review configuration → Create

**Auto-detect Framework:**
- Nette: look for `app/` + `src/` dirs
- Laravel: look for `artisan` script
- WordPress: look for `wp-config.php`
- Generic PHP: any `index.php` in root

---

## 8. PHP Version Manager

### Per-Site PHP Version

Each site can target a specific PHP version via `php_version` in SiteConfig. The daemon routes PHP-FPM requests via socket pool:

```
~/.nks-wdc/php/
├── 7.4/
│   ├── bin/php
│   ├── etc/php.ini         (managed, NOT user-edited)
│   └── var/run/php74-fpm.sock
├── 8.2/
│   ├── bin/php
│   ├── etc/php.ini
│   └── var/run/php82-fpm.sock
└── 8.4/
    ├── bin/php
    ├── etc/php.ini
    └── var/run/php84-fpm.sock
```

### CLI Aliases

NKS WebDev Console creates shell aliases in `~/.nks-wdc/bin/`:

```bash
~/.nks-wdc/bin/php74    → ~/.nks-wdc/php/7.4/bin/php
~/.nks-wdc/bin/php82    → ~/.nks-wdc/php/8.2/bin/php
~/.nks-wdc/bin/php84    → ~/.nks-wdc/php/8.4/bin/php
```

User adds `~/.nks-wdc/bin/` to their `$PATH` to use: `php82 -v`, `composer install` (uses default PHP), etc.

### php.ini Management

Each PHP version gets a managed `php.ini` with:
- `error_log = ~/.nks-wdc/logs/php82-error.log`
- `extension_dir = ~/.nks-wdc/php/8.2/lib/php/extensions/`
- `pdo_mysql.default_socket = /tmp/mysql.sock` (or Windows named pipe)
- `upload_tmp_dir = ~/.nks-wdc/tmp/`

NKS WebDev Console **never** modifies user-provided php.ini; it generates a clean base and user can add custom overrides in a separate file that's sourced.

---

## 9. Network & DNS Architecture

### Hosts File Management

NKS WebDev Console manages the system `hosts` file (Windows: `C:\Windows\System32\drivers\etc\hosts`, Unix: `/etc/hosts`).

**Single-responsibility model:** NKS WebDev Console only adds/removes lines between markers:

```
# NKS WebDev Console START — do not edit manually
127.0.0.1 nks-web.loc
127.0.0.1 chatujme.loc *.chatujme.loc
::1       nks-web.loc
::1       chatujme.loc *.chatujme.loc
# NKS WebDev Console END
```

**Atomicity:** Read file → update → write atomically. Race condition with editor: acceptable (user shouldn't edit hosts during NKS WebDev Console operations).

### DNS Flush

After modifying hosts, flush OS DNS cache:
- **Windows:** `ipconfig /flushdns`
- **macOS:** `dscacheutil -flushcache`
- **Linux:** `systemd-resolve --flush-caches` (or `sudo resolvectl flush-caches` on newer systemd)

Called via `CliWrap` after every hosts change.

### Wildcard DNS Support

NKS WebDev Console creates:
```
127.0.0.1 *.nks-web.loc
```

This allows subdomains (e.g., `api.nks-web.loc`, `admin.nks-web.loc`) to resolve without explicit entries.

---

## 10. SSL/TLS Module

### mkcert Integration

NKS WebDev Console bundles or downloads `mkcert` (platform-specific binary) and uses it to generate trusted certificates:

```bash
mkcert -install                          # Create local CA (one-time)
mkcert -cert-file cert.pem \
       -key-file key.pem \
       nks-web.loc *.nks-web.loc        # Create per-site cert
```

On Windows with OpenSSL 3.x (bundled), mkcert avoids the EC key generation bug that breaks MAMP PRO.

### Certificate Tracking

SQLite table (`certificates`):
```sql
CREATE TABLE certificates (
    id INTEGER PRIMARY KEY,
    domain TEXT,
    cert_path TEXT,
    key_path TEXT,
    expires_at TIMESTAMP,
    created_at TIMESTAMP
);
```

**HealthMonitor checks daily.** If cert expires within 7 days:
- Log warning
- Regenerate via `mkcert`
- Update database
- Restart Apache/Nginx

### Certificate Authority Management

mkcert stores the root CA at `~/.local/share/mkcert/` (Linux/macOS) or user AppData (Windows). NKS WebDev Console tracks which certificates are under its control and handles renewal automatically.

---

## 11. Database Schema

### Core Tables

**`sites`** (virtual host registry):
```sql
CREATE TABLE sites (
    id INTEGER PRIMARY KEY,
    domain TEXT UNIQUE NOT NULL,
    root TEXT NOT NULL,
    php_version TEXT,
    ssl BOOLEAN DEFAULT 1,
    http_port INTEGER DEFAULT 80,
    https_port INTEGER DEFAULT 443,
    enabled BOOLEAN DEFAULT 1,
    framework TEXT,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);
```

**`services`** (process registry):
```sql
CREATE TABLE services (
    id INTEGER PRIMARY KEY,
    service_id TEXT UNIQUE NOT NULL,
    display_name TEXT NOT NULL,
    type TEXT NOT NULL,  -- "WebServer", "Database", "Cache", etc.
    auto_start BOOLEAN DEFAULT 1,
    restart_policy TEXT,  -- JSON: {max_restarts: 5, window: 60}
    config_path TEXT,
    enabled BOOLEAN DEFAULT 1,
    created_at TIMESTAMP,
    updated_at TIMESTAMP
);
```

**`config_history`** (change audit):
```sql
CREATE TABLE config_history (
    id INTEGER PRIMARY KEY,
    entity_type TEXT,      -- "site", "service", "ssl"
    entity_id TEXT,
    change_type TEXT,      -- "create", "update", "delete"
    before_state TEXT,     -- JSON
    after_state TEXT,      -- JSON
    changed_by TEXT,       -- "cli", "gui", "api"
    changed_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);
```

**`certificates`** (SSL tracking):
```sql
CREATE TABLE certificates (
    id INTEGER PRIMARY KEY,
    domain TEXT,
    cert_path TEXT,
    key_path TEXT,
    expires_at TIMESTAMP,
    created_at TIMESTAMP
);
```

**`databases`** (MySQL/SQLite registry):
```sql
CREATE TABLE databases (
    id INTEGER PRIMARY KEY,
    name TEXT UNIQUE NOT NULL,
    engine TEXT,           -- "mysql", "sqlite"
    created_at TIMESTAMP
);
```

**`settings`** (key-value config):
```sql
CREATE TABLE settings (
    id INTEGER PRIMARY KEY,
    category TEXT,         -- "backup", "ssl", "php", etc.
    key TEXT,
    value TEXT,
    UNIQUE(category, key)
);
```

### Views for Reporting

**`v_service_status`** (with CPU/RAM metrics from HealthMonitor):
```sql
CREATE VIEW v_service_status AS
SELECT
    s.id, s.service_id, s.display_name,
    sm.state, sm.pid, sm.uptime, sm.restart_count,
    sm.cpu_percent, sm.memory_bytes
FROM services s
LEFT JOIN service_metrics sm ON s.id = sm.service_id;
```

**`v_expiring_certs`** (monitoring):
```sql
CREATE VIEW v_expiring_certs AS
SELECT domain, expires_at
FROM certificates
WHERE expires_at < datetime('now', '+30 days')
ORDER BY expires_at ASC;
```

---

## 12. CLI Interface & API Specification

### Using System.CommandLine 2.0.5+

CLI structure (no `Spectre.Console.Cli` — only `System.CommandLine` for parsing):

```csharp
var rootCommand = new RootCommand("NKS WebDev Console local dev server manager");

var startCommand = new Command("start", "Start services");
startCommand.AddOption(new Option<string[]>("--service", "Service to start"));
startCommand.SetHandler(async (services) =>
{
    // Call daemon via gRPC
    using var channel = GrpcChannel.ForAddress($"http://{GetDaemonAddress()}");
    var client = new Daemon.DaemonClient(channel);
    foreach (var svc in services)
        await client.StartServiceAsync(new StartServiceRequest { ServiceId = svc });
}, /* option binding */);

rootCommand.Add(startCommand);
return await rootCommand.InvokeAsync(args);
```

Output formatting via `Spectre.Console`:

```csharp
// Instead of plain Console.WriteLine:
AnsiConsole.MarkupLine("[green]✓[/] Apache started (PID 12345)");

var table = new Table();
table.AddColumn("Service").AddColumn("State").AddColumn("PID");
foreach (var svc in services)
    table.AddRow(svc.Id, svc.State.ToString(), svc.Pid?.ToString() ?? "—");
AnsiConsole.Write(table);
```

### Core Commands

**Site Management:**
```bash
wdc new myapp.loc --php=8.2 --db --ssl
nks-wdc list                        # Show all sites
nks-wdc remove myapp.loc
nks-wdc domain rename old.loc new.loc
nks-wdc domain set-root new.loc /path/to/root
nks-wdc domain enable myapp.loc
nks-wdc domain disable myapp.loc
```

**Service Management:**
```bash
wdc start apache|mysql|redis|all
wdc stop apache|mysql|all
nks-wdc restart apache
wdc status                      # Show all services + metrics
nks-wdc logs apache [--lines=50]
```

**PHP:**
```bash
wdc php list                    # Show installed versions
wdc php set-default 8.2         # Global default
wdc php set-site myapp.loc 8.4  # Per-site override
wdc php info 8.2
```

**Database:**
```bash
wdc db list
wdc db create mydb
wdc db drop mydb
wdc db backup mydb [--output=mydb.sql]
wdc db import mydb mydb.sql
wdc db restore mydb [--from-backup]
```

**SSL:**
```bash
wdc ssl list
wdc ssl renew myapp.loc
wdc ssl revoke myapp.loc
```

**Config:**
```bash
wdc config validate         # Pre-flight check
wdc config export           # Export current state as JSON
wdc config import file.json # Bulk import from backup
```

### gRPC API (Daemon Service)

`.proto` file:

```protobuf
service DaemonService {
    rpc StartService(StartServiceRequest) returns (ServiceStatus);
    rpc StopService(StopServiceRequest) returns (ServiceStatus);
    rpc GetServiceStatus(GetServiceStatusRequest) returns (ServiceStatus);
    rpc GetAllServices(Empty) returns (ServiceList);
    
    rpc CreateSite(CreateSiteRequest) returns (Site);
    rpc UpdateSite(UpdateSiteRequest) returns (Site);
    rpc DeleteSite(DeleteSiteRequest) returns (Empty);
    rpc GetSite(GetSiteRequest) returns (Site);
    rpc ListSites(Empty) returns (SiteList);
    
    rpc StreamLogs(StreamLogsRequest) returns (stream LogEntry);
    rpc GetMetrics(GetMetricsRequest) returns (Metrics);
}

message StartServiceRequest {
    string service_id = 1;
}

message ServiceStatus {
    string service_id = 1;
    string state = 2;
    int32 pid = 3;
    int64 uptime_seconds = 4;
    double cpu_percent = 5;
    int64 memory_bytes = 6;
}

message StreamLogsRequest {
    string service_id = 1;
    int32 lines = 2;
}

message LogEntry {
    string message = 1;
    int64 timestamp_unix = 2;
    string level = 3;  // "info", "warn", "error"
}

message Metrics {
    int64 cpu_percent_daemon = 1;
    int64 memory_bytes_daemon = 2;
    int64 disk_free_bytes = 3;
    repeated ServiceMetric services = 4;
}

message ServiceMetric {
    string service_id = 1;
    double cpu_percent = 2;
    int64 memory_bytes = 3;
}
```

**Server-side streaming example (logs):**

```csharp
public override async Task StreamLogs(StreamLogsRequest request, IServerStreamWriter<LogEntry> responseStream, ServerCallContext context)
{
    var serviceUnit = _processManager.GetService(request.ServiceId);
    if (serviceUnit == null)
        throw new RpcException(new Status(StatusCode.NotFound, "Service not found"));

    var lastLines = serviceUnit.LogBuffer.GetLastN(request.Lines);
    foreach (var line in lastLines)
    {
        await responseStream.WriteAsync(new LogEntry { Message = line });
        if (context.CancellationToken.IsCancellationRequested)
            break;
    }
}
```

---

## 13. UI/UX Design Specification

### Avalonia Application Layout

**Main Window:**

```
┌─────────────────────────────────────────────────────────────┐
│ ☰  NKS WebDev Console                                      🌙 ⚙ −□✕  │
├─────────────────────────────────────────────────────────────┤
│                                                             │
│ ┌────────────────────────────────────────────────────────┐ │
│ │  Dashboard                                  [+ New Site] │ │
│ ├────────────────────────────────────────────────────────┤ │
│ │                                                        │ │
│ │  Services Status                                       │ │
│ │  ┌──────────────────────────────────────────────────┐ │ │
│ │  │ Service       State    PID      CPU    RAM       │ │ │
│ │  ├──────────────────────────────────────────────────┤ │ │
│ │  │ Apache        ▲ Running  5432   2.1%   45 MB    │ │ │
│ │  │ MySQL         ▲ Running  5467   1.8%   120 MB   │ │ │
│ │  │ PHP-FPM 8.2   ▲ Running  5502   0.3%   32 MB    │ │ │
│ │  │ Redis         ▲ Running  5521   0.1%   8 MB     │ │ │
│ │  │ Mailpit       ▲ Running  5548   0.0%   15 MB    │ │ │
│ │  └──────────────────────────────────────────────────┘ │ │
│ │                                                        │ │
│ │  CPU / Memory Over Time                                │ │
│ │  ┌──────────────────────────────────────────────────┐ │ │
│ │  │                                         ╱╲       │ │ │
│ │  │    ╱╲      ╱╲                        ╱╲╱  ╲      │ │ │
│ │  │   ╱  ╲╱╲  ╱  ╲     ╱╲              ╱      ╲╱╲  │ │ │
│ │  │  ╱        ╱╲          ╲            ╱           │ │ │
│ │  │_╱_______________╲____╱╲___________╱___________ │ │ │
│ │  │ 0 min                              60 min       │ │ │
│ │  └──────────────────────────────────────────────────┘ │ │
│ │                                                        │ │
│ └────────────────────────────────────────────────────────┘ │
│                                                             │
└─────────────────────────────────────────────────────────────┘

Sidebar (left, collapsed by default):
  📋 Dashboard
  🌐 Sites (count badge)
  ⚙️  Services
  💾 Databases
  🔒 SSL Certificates
  🔌 Plugins
  ⚙️  Settings

System Tray Menu:
  ☑️  Open Dashboard
  ────────────────
  Apache:  ▲ Running
  MySQL:   ▲ Running
  PHP-FPM: ▲ Running
  ────────────────
  ⚙️  Settings
  ❌ Quit
```

**Sites Tab:**

```
┌──────────────────────────────────────┐
│ Sites (3)              [+ New Site] │
├──────────────────────────────────────┤
│ ┌────────────────────────────────┐  │
│ │ 🌐 nks-web.loc             ⋮  │  │
│ │   Root: /home/user/projects/nks-web/public
│ │   PHP 8.2 | SSL ✓ | ↑ Running
│ │   https://nks-web.loc
│ └────────────────────────────────┘  │
│
│ ┌────────────────────────────────┐  │
│ │ 🌐 chatujme.loc            ⋮  │  │
│ │   Root: /home/user/projects/chatujme/public
│ │   PHP 8.4 | SSL ✓ | ↑ Running
│ │   https://chatujme.loc
│ └────────────────────────────────┘  │
│
│ ┌────────────────────────────────┐  │
│ │ 🌐 wp.loc                 ⋮  │  │
│ │   Root: /home/user/sites/wp
│ │   PHP 7.4 | SSL ✗ | ↓ Stopped
│ │   http://wp.loc
│ └────────────────────────────────┘  │
└──────────────────────────────────────┘

Site Detail (right panel, if selected):
  Domain: nks-web.loc
  Root: /home/user/projects/nks-web/public
  Aliases: *.nks-web.loc

  PHP Version: 8.2 (global default 8.4)
  [Change PHP]

  SSL:
    ✓ Enabled (expires 2026-08-09)
    [Renew] [Revoke]

  Actions:
    [Open in Browser] [Open in Editor] [Delete]
```

**Services Tab (detailed):**

```
┌──────────────────────────────────────┐
│ Services                             │
├──────────────────────────────────────┤
│ ┌────────────────────────────────┐  │
│ │ Apache (httpd)                 │  │
│ │ ▲ Running | PID: 5432          │  │
│ │                                 │  │
│ │ CPU:    ████░░░░ 2.1%          │  │
│ │ Memory: ███████░░ 45 MB        │  │
│ │ Uptime: 2h 15m                 │  │
│ │                                 │  │
│ │ [Logs] [Stop] [Reload Config]  │  │
│ └────────────────────────────────┘  │
│
│ ┌────────────────────────────────┐  │
│ │ MySQL 5.7                      │  │
│ │ ▲ Running | PID: 5467          │  │
│ │                                 │  │
│ │ CPU:    ██░░░░░░░ 1.8%         │  │
│ │ Memory: █████████░ 120 MB      │  │
│ │ Uptime: 5h 44m                 │  │
│ │                                 │  │
│ │ [Logs] [Stop] [Backup]         │  │
│ └────────────────────────────────┘  │
└──────────────────────────────────────┘
```

### Theme & Styling

- **Avalonia.Themes.Fluent** (v12.0.0)
- Dark/Light toggle via `Application.Current.RequestedThemeVariant`
- FluentTheme only supports Dark and Light (no custom palettes)
- Colors follow Microsoft Fluent Design tokens

### Controls

**ServiceCard:** Reusable card displaying service name, state, metrics, actions
**SiteCard:** Reusable card displaying site domain, PHP version, SSL status, framework
**PhpVersionBadge:** Colored badge showing PHP version (e.g., "8.2")

---

## 14. Plugin Architecture

### Mechanism: AssemblyLoadContext + IServiceModule

NKS WebDev Console plugins are .NET assemblies loaded dynamically via `AssemblyLoadContext`. No external scripting language (e.g., Lua) — everything is .NET.

**Plugin Interface:**

```csharp
public interface Inks-wdcPlugin
{
    string Name { get; }
    Version Version { get; }
    void Configure(IServiceCollection services);
}
```

**Plugin Discovery & Loading:**

```csharp
public class PluginLoader
{
    public async Task LoadPluginsAsync(string pluginsDir)
    {
        foreach (var dll in Directory.GetFiles(pluginsDir, "*.Plugin.dll"))
        {
            var ctx = new PluginLoadContext(dll);
            var asm = ctx.LoadFromAssemblyPath(dll);

            var pluginType = asm.GetExportedTypes()
                .FirstOrDefault(t => typeof(Inks-wdcPlugin).IsAssignableFrom(t) && !t.IsAbstract);

            if (pluginType == null)
                continue;

            var plugin = (Inks-wdcPlugin)Activator.CreateInstance(pluginType)!;
            _logger.LogInformation("Loaded plugin: {Name} v{Version}", plugin.Name, plugin.Version);

            // Plugin configures services
            plugin.Configure(_serviceCollection);
        }
    }
}

public class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginPath) : base(isCollectible: true)
        => _resolver = new AssemblyDependencyResolver(pluginPath);

    protected override Assembly? Load(AssemblyName name)
    {
        var path = _resolver.ResolveAssemblyToPath(name);
        return path != null ? LoadFromAssemblyPath(path) : null;
    }
}
```

### Plugin Manifest (plugin.json)

Each plugin ships a `plugin.json` describing its capabilities:

```json
{
  "name": "MyCustomModule",
  "version": "1.0.0",
  "description": "Custom service module for X",
  "author": "NKS WebDev Console Community",
  "license": "Apache-2.0",
  "entry_point": "MyNamespace.MyPlugin",
  "required_nks-wdc_version": "1.0.0+",
  "dependencies": {
    "MyCustomModule": "1.0.0"
  },
  "permissions": [
    "process:start",
    "config:write",
    "database:read"
  ]
}
```

### Plugin Development Example

Create a custom module (e.g., for a proprietary service):

```csharp
// MyNamespace.MyPlugin.cs
public class MyCustomModule : Inks-wdcPlugin, IServiceModule
{
    public string Name => "MyCustomModule";
    public Version Version => new(1, 0, 0);
    public string ServiceId => "my-custom-service";
    public string DisplayName => "My Custom Service";
    public ServiceType Type => ServiceType.Custom;

    private ILogger<MyCustomModule> _logger;

    public void Configure(IServiceCollection services)
    {
        services.AddSingleton<IServiceModule>(this);
    }

    public async Task<ValidationResult> ValidateConfigAsync(CancellationToken ct)
    {
        // Custom validation logic
        return new ValidationResult(true, Array.Empty<string>());
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _logger.LogInformation("Starting custom service");
        // Launch via System.Diagnostics.Process
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _logger.LogInformation("Stopping custom service");
    }

    // ... other interface members
}
```

### Security: No Sandbox

`AssemblyLoadContext` provides **no sandbox**. A loaded plugin can call any .NET API. Mitigation:
- Code-sign plugin DLLs with a trusted certificate
- Verify signature before loading
- Document that plugins are trusted code
- For first-party NKS WebDev Console plugins only

For untrusted plugins, run them in a separate process communicating via gRPC.

### Hot Reload (Future)

Plugins can be unloaded and reloaded by:
1. Clearing all references to the `AssemblyLoadContext`
2. Setting the context to null
3. Calling `GC.Collect()` and `GC.WaitForPendingFinalizers()`
4. Reloading from disk

Requires careful management to avoid `InvalidCastException` when shared types between host and plugin differ.

---

## 15. Security Model

### Principle: Daemon = Privileged, Clients = Unprivileged

The daemon runs with elevated privileges (to manage system services, write `hosts` file, manage SSL certs). The GUI and CLI are unprivileged clients that only call gRPC endpoints.

### Permissions Model (Future / V2)

```csharp
public enum Permission
{
    ProcessStart,
    ProcessStop,
    ConfigRead,
    ConfigWrite,
    DatabaseRead,
    DatabaseWrite,
    SslRead,
    SslWrite,
    HostsWrite,
    PluginLoad
}

public class RoleBasedAccess
{
    public static readonly Dictionary<string, Permission[]> Roles = new()
    {
        ["admin"] = Enum.GetValues<Permission>().ToArray(),
        ["viewer"] = new[] { Permission.ProcessRead, Permission.ConfigRead },
        ["operator"] = new[] { Permission.ProcessStart, Permission.ProcessStop, Permission.ConfigRead }
    };
}
```

In V1, all local clients have full access (implicit trust). Multi-user/remote access requires authentication (out of scope for V1).

### SSL/TLS for Daemon ↔ Client Communication

gRPC over named pipes / Unix socket is OS-level authenticated (ACL on Windows, file mode on Unix). Additional TLS layer is unnecessary for local IPC but can be added for defense-in-depth:

```csharp
// Optional: use self-signed certs even for local pipes
builder.Services
    .AddGrpc()
    .ConfigureKestrelServerOptions(opts =>
    {
        opts.UseHttps("nks-wdc-selfsigned.pfx", "password");
    });
```

### Code Signing (Post-V1)

Unsigned .NET assemblies are not flagged by Defender; signing adds trust markers for end users. Not required for V1 but recommended for production releases.

### Input Validation

All gRPC handlers validate input:

```csharp
public override async Task<Site> CreateSite(CreateSiteRequest request, ServerCallContext context)
{
    if (string.IsNullOrWhiteSpace(request.Domain))
        throw new RpcException(new Status(StatusCode.InvalidArgument, "Domain required"));

    if (!Path.IsPathRooted(request.Root))
        throw new RpcException(new Status(StatusCode.InvalidArgument, "Root must be absolute path"));

    if (!Directory.Exists(request.Root))
        throw new RpcException(new Status(StatusCode.NotFound, "Root directory not found"));

    // ... validation logic
}
```

---

## 16. Performance Targets & Optimization

### Targets

| Metric | Target | Current (Est.) |
|--------|--------|-----------|
| Daemon startup | < 3s | ~1.5s (gRPC init, plugin load, DB migrate) |
| Site creation | < 1s | ~0.8s (render + validate + write) |
| PHP version switch | < 2s | ~1.5s (stop PHP-FPM, update vhost, reload Apache) |
| Memory (daemon + services) | < 250MB | ~180MB (daemon 30MB + Apache 40 + MySQL 80 + PHP 30) |
| GUI responsiveness | 60 FPS | 60 FPS (Avalonia/Skia native) |
| gRPC latency | < 10ms | ~2-5ms (local named pipe) |

### Optimization Strategies

1. **Lazy loading:** Plugins loaded only on demand
2. **Caching:** Service status cached for 1 second; charts downsample data
3. **Async I/O:** All file operations use `async`/`await`
4. **Database indices:** Primary keys on `sites.domain`, `services.service_id`
5. **Circular buffers:** Log buffer (RingBuffer<string>) limits memory for 1000 lines
6. **Streaming APIs:** Server-sent events for logs to avoid buffering entire log file in memory
7. **Binary protocols:** gRPC/protobuf vs. JSON reduces serialization overhead
8. **No trimming:** Do NOT use `PublishTrimmed=true` (causes reflection-based code to break and triggers Defender heuristics)

### Monitoring

NKS WebDev Console exports Prometheus-style metrics (future feature) for integration with monitoring tools. Basic metrics available via gRPC `GetMetrics` RPC.

---

## 17. Testing Strategy

### Test Structure

```
NKS.WebDevConsole.Tests/
├── Core.Tests/
│   ├── Configuration/
│   │   ├── ScribanTemplateTests.cs
│   │   ├── TomlParsingTests.cs
│   │   └── ConfigValidationTests.cs
│   ├── Models/
│   │   └── SiteConfigTests.cs
│   └── Services/
│       └── HostsFileManagerTests.cs
│
├── Daemon.Tests/
│   ├── ProcessManagerTests.cs
│   ├── HealthMonitorTests.cs
│   ├── SslManagerTests.cs
│   └── PluginLoaderTests.cs
│
├── Cli.Tests/
│   ├── CommandParsingTests.cs
│   └── GrpcClientTests.cs
│
└── Gui.Tests/
    ├── ViewModelTests.cs
    └── AvaloniaHeadlessTests.cs
```

### Test Categories

**Unit Tests** (xUnit + Moq):
- Configuration rendering (Scriban templates)
- TOML parsing
- Hosts file parsing/generation
- PHP version detection
- Config validation rules

**Integration Tests:**
- Daemon gRPC API
- Database migrations (in-memory SQLite)
- Multi-service start/stop sequences
- Config apply pipeline

**E2E Tests** (Avalonia Headless):
- GUI open → create site → start service → verify in browser
- CLI commands (via subprocess invocation)

**Performance Tests:**
- Site creation time (target: < 1s)
- Daemon startup time (target: < 3s)
- Memory footprint under load

### CI/CD (GitHub Actions)

```yaml
# .github/workflows/test.yml
on: [push, pull_request]

jobs:
  test:
    runs-on: [ubuntu-latest, windows-latest, macos-latest]
    steps:
      - uses: actions/checkout@v3
      - uses: actions/setup-dotnet@v3
        with:
          dotnet-version: '9.0.x'
      - run: dotnet test
      - run: dotnet build -c Release
```

---

## 18. Packaging & Distribution

### Installer Strategy

| Platform | Installer | Auto-Update |
|----------|-----------|-------------|
| Windows | Inno Setup EXE (portable) + WiX MSI (managed) | Velopack |
| macOS | DMG (drag-to-install) | Velopack |
| Linux | AppImage | Velopack |

### Velopack Setup

**Configuration:**

```bash
dotnet add package Velopack
# Then in Program.cs:
VelopackApp.Build().Run();

# Install vpk CLI tool:
dotnet tool install -g vpk

# Publish and package:
dotnet publish -c Release -r win-x64 --self-contained -o publish/
vpk pack --packId NKS WebDev Console --packVersion 1.0.0 --packDir publish/ --mainExe wdc.exe
# Output: releases/NKS WebDev Console-1.0.0-full.exe, NKS WebDev Console-1.0.0-delta.exe
```

**Benefits:**
- Delta updates (small download)
- Automatic delta generation and application
- Rollback support
- ~2 second update time

### Publish Flags (Self-Contained, Non-Trimmed)

```bash
dotnet publish -c Release \
  -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:ReadyToRun=true \
  -p:PublishTrimmed=false
```

**Why NOT trimmed:**
- Trimming breaks reflection-based code (Avalonia bindings, gRPC code generation)
- Plugin system relies on reflection
- Does NOT improve safety against Defender (no false positive issue with untrimmed)

### Portable vs. Installed

**Portable ("Run Once") — Recommended for users:**
- Download ZIP, extract anywhere
- No installer
- No system integration
- No UAC elevation needed
- Perfect for testing or minimal footprint

**Installed (Installer):**
- WiX MSI for enterprise GPO deployment
- Windows Store package (MSIX) available post-launch
- Start menu shortcuts
- Optional system tray start on login
- Auto-update integration

### Licensing & Code Signing

**License:** Apache-2.0

**Code signing:**
- V1: Optional (not required for .NET, no Defender false positive)
- Post-V1: Recommended for trust markers
- Use standard Authenticode cert (NOT EV; EV cost only justified for Go binaries)

---

## 19. Legal & Licensing

### Compliance Checklist

- ✓ Apache-2.0 license chosen (commercial-friendly, patent grant)
- ✓ COPYING file at repo root
- ✓ Dependencies: all must be compatible with Apache-2.0
  - MIT (Avalonia, Dapper, Scriban): ✓ compatible
  - Apache-2.0 (gRPC, many others): ✓ compatible
  - LGPL (if any): ✓ compatible (static link OK)
- ✓ No GPL code (would require GPL license for entire project)

### Dependency Audit

Key dependencies:
- Avalonia: MIT
- gRPC: Apache-2.0 / MIT dual
- Dapper: Apache-2.0
- Scriban: Apache-2.0
- CommunityToolkit.Mvvm: MIT
- Serilog: Apache-2.0
- Spectre.Console: MIT
- System.CommandLine: MIT

No problematic licenses detected.

### Third-Party Binaries

- `mkcert`: Unlicense (public domain) — bundled or downloaded
- OpenSSL: Apache-2.0 (via mkcert) — transparent to users

---

## 20. Implementation Roadmap

### Phase 0: Day-1 Verification (1 day)

**Checklist before starting Phase 1:**

1. **Avalonia 12 + LiveCharts2 compat**
   - Create minimal Avalonia app with LiveCharts2 2.0.0
   - Render a real-time line chart (100 data points, updated every 500ms)
   - Verify smooth rendering, no exceptions
   - If broken: report issue to LiveCharts2, switch to ScottPlot.Avalonia

2. **gRPC over named pipes**
   - Create minimal gRPC server listening on `\\.\pipe\nks-wdc-daemon`
   - Create client connecting to same pipe
   - Invoke unary + streaming RPC
   - Verify latency < 5ms

3. **Scriban template rendering**
   - Parse Apache VirtualHost template
   - Render with test data
   - Compare output to expected (manual Apache syntax check)

4. **SQLite + Dapper in-memory**
   - Create in-memory DB
   - Define tables + views
   - Insert/update/delete via Dapper
   - Verify query results

5. **System.Diagnostics.Process + child management**
   - Spawn a long-running process (e.g., `sleep 1000`)
   - Capture PID
   - Test `Process.Kill(entireProcessTree: true)`
   - Verify all children killed

6. **Plugin loading (AssemblyLoadContext)**
   - Create a test plugin DLL with `IServiceModule`
   - Load via `AssemblyLoadContext`
   - Instantiate plugin
   - Call a method

If all 6 items pass: proceed to Phase 1.

### Phase 1: Core Daemon & gRPC API (Weeks 1–4)

**Goals:**
- Daemon boots and listens on gRPC
- Process management for Apache + MySQL
- Basic configuration rendering

**Deliverables:**
1. `NKS.WebDevConsole.Core` project with domain models (Site, Service, PhpVersion, etc.)
2. `NKS.WebDevConsole.Daemon` Worker Service
3. ProcessManager + ServiceUnit state machine
4. Apache + MySQL modules implementing `IServiceModule`
5. Basic gRPC service (start/stop/status)
6. SQLite schema + DbUp migrations
7. Unit tests for ProcessManager + HealthMonitor

**Exit Criteria:**
- `wdc start apache` and `wdc stop apache` work via gRPC
- Service state persisted in SQLite
- Logs captured and streamable via gRPC

### Phase 2: Configuration Pipeline (Weeks 5–7)

**Goals:**
- Scriban template rendering for Apache VirtualHost
- 3-stage config pipeline (render → validate → apply)
- Virtual host manager

**Deliverables:**
1. ConfigEngine with Scriban template support
2. ConfigValidator (syntax checks, schema validation)
3. AtomicWriter (backup + write)
4. VirtualHost CRUD gRPC endpoints
5. Site creation CLI command + wizard
6. Config history audit table + gRPC endpoint

**Exit Criteria:**
- Create site via CLI: `wdc new myapp.loc --php=8.2 --ssl`
- VirtualHost config generated, validated, applied
- Site accessible at `https://myapp.loc`

### Phase 3: PHP Version Manager (Weeks 8–9)

**Goals:**
- Multi-PHP version support (7.4, 8.2, 8.4)
- Per-site PHP FPM pools
- CLI aliases

**Deliverables:**
1. PHP module implementing `IServiceModule`
2. PHP FPM pool configuration for each version
3. CLI aliases in `~/.nks-wdc/bin/`
4. `wdc php list|set-default|set-site` commands
5. php.ini template rendering per version

**Exit Criteria:**
- Switch site from PHP 8.2 → 8.4: `wdc php set-site myapp.loc 8.4`
- Verify site uses correct version (phpinfo() on site, CLI `php82 -v`)

### Phase 4: SSL/TLS + DNS (Weeks 10–11)

**Goals:**
- mkcert integration for trusted local certs
- Automatic certificate generation + renewal
- Hosts file management

**Deliverables:**
1. MkcertManager (locate/install/run mkcert)
2. Certificate CRUD + expiry tracking
3. HealthMonitor check for expiring certs (renewal)
4. HostsFileManager (read/write between markers)
5. DNS flush after hosts change
6. SSL gRPC endpoints

**Exit Criteria:**
- Create site with `--ssl` flag: auto-generates cert via mkcert
- Certificate trusted in browser (no warnings)
- DNS entry in hosts file
- Certificate auto-renewed before expiry

### Phase 5: GUI (Weeks 12–13)

**Goals:**
- Avalonia application
- Dashboard with service status
- Sites list with CRUD
- System tray

**Deliverables:**
1. App.axaml + theme setup
2. Main window with sidebar
3. Dashboard tab (LiveCharts2 metrics, service status table)
4. Sites tab (list, create/edit/delete)
5. System tray with menu
6. GrpcClientService wrapper
7. MVVM ViewModels with CommunityToolkit.Mvvm

**Exit Criteria:**
- GUI launches
- Dashboard shows live CPU/RAM charts
- Can create/delete sites
- Tray menu works (open/quit)

### Phase 6: CLI + Documentation (Weeks 14–15)

**Goals:**
- Full CLI implementation
- Help text + man pages
- API documentation
- User guide

**Deliverables:**
1. System.CommandLine command tree (site, service, php, db, ssl, config)
2. Spectre.Console output (tables, progress bars)
3. API documentation (.proto → HTML via protoc plugins)
4. Installation guide
5. User manual (per-site PHP, SSL certs, database backup)
6. Plugin development guide

**Exit Criteria:**
- All CLI commands documented and tested
- Help text: `nks-wdc --help`, `wdc new --help`
- User can run full workflow from CLI

### Phase 7: Database Manager + Plugins (Weeks 16–17)

**Goals:**
- MySQL / SQLite database management
- Plugin system integration
- V1 feature complete

**Deliverables:**
1. Database module (`db create`, `db backup`, `db import`)
2. Plugin discovery + loading
3. Example plugin (custom service module)
4. Plugin development documentation
5. Integration tests for entire system

**Exit Criteria:**
- `wdc db create mydb && wdc db backup mydb`
- Plugin loading without errors
- Example plugin starts and shows in GUI

### Phase 8: Packaging + Release (Weeks 18–19)

**Goals:**
- Installers for Windows/macOS/Linux
- Auto-update via Velopack
- Release candidate testing

**Deliverables:**
1. Velopack configuration + build
2. Inno Setup script (portable EXE)
3. WiX MSI (managed install)
4. CI/CD pipeline (GitHub Actions)
5. Release notes + changelog

**Exit Criteria:**
- Download and run installer
- Auto-update check works
- Publish to GitHub Releases

### Phase 9: Testing + Polish (Weeks 19–20)

**Goals:**
- Test coverage
- Performance profiling
- UX refinements

**Deliverables:**
1. Unit test suite (80%+ coverage)
2. E2E tests (critical user flows)
3. Performance benchmarks
4. Accessibility audit (WCAG AA)
5. Bug fixes from testing

**Exit Criteria:**
- All tests passing
- Startup < 3s
- Memory < 250MB
- Dashboard responsive

### Timeline Summary

**Total:** ~20 weeks (4.5 months) for solo developer
**Start:** 2026-04-09
**Expected Release:** 2026-08-28 (v1.0.0)

---

## 21. Risk Analysis

### Technical Risks

| Risk | Likelihood | Impact | Mitigation |
|------|------------|--------|-----------|
| LiveCharts2 + Avalonia 12 incompatibility | Medium | High | Day-1 verification; fallback to ScottPlot |
| gRPC named pipe timeout / deadlock | Low | High | Performance test during Phase 1; cancellation tokens |
| Hosts file race condition (concurrent edits) | Low | Medium | Atomic write; document not to edit hosts manually during NKS WebDev Console use |
| Plugin loading ALC unload failure | Medium | Medium | Careful reference cleanup; fallback to process restart |
| Windows Job Object not killing children | Very Low | High | Test during Phase 1 ProcessManager tests |
| mkcert OpenSSL 3.x on Windows fails | Very Low | Medium | Fallback to PowerShell cert generation; pre-test on target systems |
| Apache/Nginx config syntax changes | Low | Medium | Validate with `httpd -t` / `nginx -t` in pipeline |
| SQLite journal mode locking | Low | Medium | Enable WAL mode; concurrent reader/writer test in Phase 1 |

### Deployment Risks

| Risk | Likelihood | Impact | Mitigation |
|------|------------|--------|-----------|
| Defender / antivirus false positive | Low | High | .NET FDE not flagged; test on Windows with real AV; optional code signing |
| Installer UAC elevation fails | Medium | Medium | Provide portable ZIP alternative; test on limited user account |
| User has incompatible Apache version | Low | Medium | Version detection on startup; compatibility table in docs |
| Port conflict (80/443 in use) | Medium | Medium | Config validation suggests alternative ports; error message |

### Market Risks

| Risk | Likelihood | Impact | Mitigation |
|------|------------|--------|-----------|
| FlyEnv adds config validation | Medium | Low | First-mover advantage on validation feature; emphasize gRPC API extensibility |
| Laragon becomes active again | Low | Low | Cross-platform story (Laragon Windows-only); CLI + plugin ecosystem |
| Laravel Herd gains market share | Medium | Medium | Position for non-Laravel developers; emphasize multi-language support |
| User expects Docker integration | Medium | Low | Traefik reverse proxy (post-V1) bridges gap; document trade-offs |

### Operational Risks

| Risk | Likelihood | Impact | Mitigation |
|------|------------|--------|-----------|
| Limited maintainer capacity (solo dev) | High | High | Open-source contributions; plugin architecture for extensibility; community-driven features |
| Dependency vulnerability in gRPC/Avalonia | Medium | Medium | Automated dependency scanning (Dependabot); rapid patch releases |
| Config corruption (migration bug) | Low | High | Backup config before migration; rollback procedure; audit trail in `config_history` |
| User misconfigures PHP version mismatch | Medium | Low | Validation checks; clear error messages; documentation |

### Security Risks

| Risk | Likelihood | Impact | Mitigation |
|------|------------|--------|-----------|
| Daemon process runs as SYSTEM (over-privileged) | Medium | High | Principle of least privilege; document minimum permissions needed |
| Plugin arbitrary code execution | Low | High | Code signing requirement; documented that plugins are trusted; process isolation (future) |
| Config files world-readable | Medium | Medium | Secure file permissions on Unix (chmod 600); document security model |
| Hosts file tampering | Very Low | Medium | File integrity checks; audit log |

### Mitigation Summary

1. **Day-1 verification** catches technical blockers early
2. **Phased rollout** allows testing at each stage
3. **Comprehensive testing** (unit + E2E) prevents regressions
4. **Documentation** explains limitations and best practices
5. **Community involvement** shares maintenance burden

---

## Appendix A: Glossary

| Term | Definition |
|------|-----------|
| **Daemon** | Background service (NKS.WebDevConsole.Daemon) running as Worker Service |
| **gRPC** | Remote Procedure Call framework over HTTP/2 (protobuf messages) |
| **Named Pipe** | Windows IPC mechanism (\\.\pipe\name) |
| **Unix Socket** | POSIX IPC mechanism (file path) |
| **VirtualHost** | Apache configuration for a single domain |
| **FPM** | FastCGI Process Manager (PHP-FPM) |
| **mkcert** | CLI tool to generate trusted local TLS certificates |
| **Scriban** | Template engine (Liquid-like syntax) for config generation |
| **TOML** | Configuration file format (INI-like) |
| **AssemblyLoadContext** | .NET mechanism for dynamic assembly loading |
| **IServiceModule** | Interface that all service modules implement |

## Appendix B: References

- [Avalonia UI Documentation](https://docs.avaloniaui.net)
- [gRPC C# Guide](https://grpc.io/docs/languages/csharp/)
- [System.CommandLine Documentation](https://docs.microsoft.com/en-us/dotnet/standard/commandline/)
- [Spectre.Console](https://spectreconsole.net)
- [Scriban Documentation](https://github.com/scriban/scriban)
- [mkcert GitHub](https://github.com/FiloSottile/mkcert)
- [Apache VirtualHost Directive](https://httpd.apache.org/docs/current/mod/core.html#virtualhost)
- [NKS WebDev Console SPEC.md](./SPEC.md) — authoritative technical specification
- [Interview Results](./interview-results.md) — user requirements
- [Avalonia Ecosystem](./avalonia-ecosystem.md) — NuGet package guidance

