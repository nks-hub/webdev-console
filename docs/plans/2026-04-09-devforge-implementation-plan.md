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
| SQLite | `modernc.org/sqlite` (pure Go) | `rusqlite` | **EF Core or Dapper** | better-sqlite3 |
| IPC | Named pipe (native) | TcpStream | **gRPC + named pipes (GrpcDotNetNamedPipes)** | TCP |
| Daemon framework | Manual goroutine loops | Manual tokio | **IHostedService (mature pattern)** | Manual event loop |

**Decision Rationale:**
1. **AV False Positive Safety** — Go binaries trigger Microsoft Defender heuristics (Wacatac.B!ml, Wacapew.C!ml); .NET Framework Dependent Execution (FDE) not flagged even on unsigned binaries. Critical for product reliability.
2. **Single Language Everywhere** — Daemon (Worker Service) + GUI (Avalonia UI XAML/C#) + CLI (System.CommandLine, Spectre.Console) = zero polyglot friction, unified testing, shared domain models.
3. **Native Process Management** — `System.Diagnostics.Process` + `TaskCompletionSource` is gold standard for Windows process control; `Task` model elegantly maps to per-service lifecycle management.
4. **IPC/RPC Excellence** — gRPC + protobuf over named pipes provides better performance and type safety than JSON-RPC; works natively on Windows with `GrpcDotNetNamedPipes` NuGet package.
5. **GUI Maturity** — Avalonia UI 12.x reaches production-grade stability with Fluent Design System, native tray support, and DataGrid control built-in. ReactiveUI/CommunityToolkit.MVVM ecosystem is robust.
6. **Developer Productivity** — C# async/await, LINQ, and dependency injection reduce ceremony vs. Go; IDE support (Visual Studio Community, Rider) is unmatched.

### GUI Framework: Avalonia UI 12.x (SELECTED)

**Avalonia UI v12.0.0+ (April 2026 release)**

| Aspect | Details |
|--------|---------|
| **License** | MIT (permissive, commercial OK) |
| **Bundle Size** | ~21 MB base + ~13 MB self-contained runtime = ~34 MB installer (vs. Qt 8-15 MB) |
| **Memory** | 40-80 MB runtime (lower than Electron, comparable to Qt) |
| **Features** | Native tray icon, DataGrid, TreeView, Fluent Design System (dark/light), hot reload in dev |
| **Cross-Platform** | Win, macOS, Linux (native rendering on each platform) |
| **IPC with Daemon** | gRPC over named pipes (Windows) / Unix sockets (macOS/Linux) via `GrpcDotNetNamedPipes` package |
| **MVVM** | ReactiveUI (mature) or CommunityToolkit.MVVM (simpler, Microsoft-endorsed) |
| **Charts/Graphing** | LiveCharts2 (C#, similar to Chart.js) |
| **Keyboard Shortcuts** | Built-in `HotKeyManager` and command binding support |
| **Accessibility** | WCAG 2.1 AA via UIA (Windows) / accessibility APIs (macOS/Linux) |

**Comparison to Qt6:**
- Qt6 (Score: 9.5): Smaller bundle (8-15 MB), LGPL compliance burden, C++ learning curve
- **Avalonia UI (Score: 8.5): Slightly larger bundle, MIT license (simpler), C# ecosystem, unified .NET stack**

### Core Engine Framework: .NET 9 Worker Service

```csharp
// Program.cs entry point
Host.CreateDefaultBuilder(args)
    .ConfigureServices(services => {
        services.AddHostedService<DevForgeWorkerService>();
        services.AddScoped<IServiceManager>();
        services.AddScoped<IConfigurationPipeline>();
        services.AddSingleton<EventBus>();
        services.AddGrpc();
    })
    .Build()
    .Run();
```

**Key Patterns:**
- `IHostedService` lifecycle (StartAsync / StopAsync) for daemon management
- `BackgroundService` base class for continuous monitoring loops
- `CancellationToken` throughout for graceful shutdown
- `Channel<T>` for inter-task communication (replaces goroutine channels)
- Dependency injection for loose coupling

### Configuration Storage & IPC

**Configuration Storage:**
- **Site configs: TOML files** — parsed via `Tomlyn` NuGet package
- **Runtime state: SQLite** — `Dapper` for lightweight ORM, or EF Core for migrations
- **Rationale:** Same as before — survives corruption, independent per-site

**IPC Protocol: gRPC (not JSON-RPC)**
- **Transport:** Named pipe `\\.\pipe\devforge-daemon` (Windows) / Unix socket `~/.devforge/daemon.sock` (Unix)
- **Serialization:** Protocol Buffers (protobuf) — type-safe, binary efficient
- **Benefits vs. JSON-RPC 2.0:**
  - Strongly-typed contracts (compile-time safety)
  - Better performance for streaming logs/events
  - Named pipes have full support via `GrpcDotNetNamedPipes` package
  - Shared proto definitions between daemon and clients

**Proto Example:**
```protobuf
service DevForge {
  rpc CreateSite(CreateSiteRequest) returns (CreateSiteResponse);
  rpc ListSites(Empty) returns (stream SiteInfo);
  rpc SubscribeEvents(EventFilter) returns (stream Event);
}
```

---

## 4. System Architecture

### Layered Architecture (.NET 9 + Avalonia UI)

```
┌───────────────────────────────────────────────────────┐
│  PRESENTATION LAYER                                   │
│  ┌──────────────────────────┐  ┌──────────────────┐  │
│  │  GUI (Avalonia UI)       │  │  CLI             │  │
│  │  XAML + C# ViewModel     │  │  (System.        │  │
│  │  (MVVM/ReactiveUI)       │  │   CommandLine +  │  │
│  │                          │  │   Spectre.       │  │
│  └────────────┬─────────────┘  │   Console)       │  │
│               │                │                  │  │
│               │                └────────┬─────────┘  │
├───────────────┴─────────────────────────┴────────────┤
│  RPC LAYER — gRPC over named pipe/Unix socket        │
│  (Protobuf serialization, strongly-typed contracts)  │
├───────────────────────────────────────────────────────┤
│  CORE ENGINE (DevForge Worker Service)               │
│  ┌───────────────┬──────────────┬─────────────────┐  │
│  │ IServiceMgr   │ IConfigPipe   │ IVHostManager  │  │
│  │ (Process      │ (TOML + tpl + │ (Apache/Nginx) │  │
│  │  lifycycle)   │  validation)  │                │  │
│  ├───────────────┼──────────────┼─────────────────┤  │
│  │ IPhpManager   │ ISslModule    │ IDnsManager    │  │
│  │ (MultiVer)    │ (mkcert)      │ (hosts file)   │  │
│  └───────────────┴──────────────┴─────────────────┘  │
│  ┌─────────────────────────────────────────────────┐ │
│  │  EventBus (Channel<Event>) + HealthMonitor     │ │
│  │  (runs as BackgroundService)                   │ │
│  └─────────────────────────────────────────────────┘ │
├───────────────────────────────────────────────────────┤
│  PLATFORM ABSTRACTION LAYER (C#)                      │
│  IPlatformAbstraction: process spawning,             │
│  privilege elevation (UAC), file ACLs, registries    │
└───────────────────────────────────────────────────────┘
```

### Single .NET Solution Structure

```
DevForge.sln
├── DevForge.Daemon/              # Worker Service (.NET 9)
│   ├── Program.cs               # Startup, DI configuration
│   ├── Services/
│   │   ├── ServiceManager.cs    # System.Diagnostics.Process wrapper
│   │   ├── ConfigPipeline.cs    # TOML + Handlebars rendering
│   │   ├── VHostManager.cs      # Apache/Nginx config
│   │   ├── SslModule.cs         # mkcert integration
│   │   ├── HealthMonitor.cs     # BackgroundService loop
│   │   └── PluginHost.cs        # Lua runtime
│   └── Protos/                  # gRPC service definitions
│       └── devforge.proto       # Service contracts
│
├── DevForge.Gui/                 # Avalonia UI Application
│   ├── App.xaml.cs              # App entry point
│   ├── Views/
│   │   ├── MainWindow.xaml      # Root window
│   │   ├── DashboardView.xaml
│   │   ├── SitesManagerView.xaml
│   │   ├── PhpManagerView.xaml
│   │   └── SettingsView.xaml
│   ├── ViewModels/              # ReactiveUI or MVVM Toolkit
│   │   ├── MainViewModel.cs
│   │   ├── DashboardViewModel.cs
│   │   └── ...
│   └── DaemonClient.cs          # gRPC channel to daemon
│
├── DevForge.Cli/                 # CLI Console Application
│   ├── Program.cs               # System.CommandLine root
│   ├── Commands/
│   │   ├── SiteCommand.cs       # site:create, site:list, etc.
│   │   ├── ServiceCommand.cs
│   │   ├── PhpCommand.cs
│   │   └── ...
│   └── DaemonClient.cs          # Shared gRPC client
│
├── DevForge.Core/                # Shared domain models
│   ├── Models/
│   │   ├── ServiceUnit.cs
│   │   ├── Site.cs
│   │   └── ...
│   ├── Interfaces/
│   │   ├── IServiceManager.cs
│   │   ├── IConfigPipeline.cs
│   │   └── ...
│   ├── Events/
│   │   └── EventBus.cs          # Channel<Event> pub/sub
│   └── Exceptions/
│
└── DevForge.Tests/               # xUnit + Moq
    ├── UnitTests/
    ├── IntegrationTests/
    └── E2eTests/
```

### Three Entry Points (Same .NET Runtime)

1. **`devforged.exe`** — Worker Service daemon
   - Runs as system service (Windows Service) or user process (manual launch)
   - Exposes gRPC endpoint on named pipe
   - Manages all child processes (Apache, PHP-FPM, MySQL)
   
2. **`devforge.exe`** — CLI tool
   - System.CommandLine for command parsing
   - Spectre.Console for rich terminal output
   - Connects to daemon via gRPC (if daemon not running, exits with error)
   - Zero direct process/file manipulation
   
3. **`DevForge.exe`** — GUI application
   - Avalonia UI, native look and feel
   - Launches daemon if not running (or connects to existing)
   - Uses gRPC client to communicate
   - Tray icon integration (TrayIcon control in Avalonia)

### Critical Design Principle

**The daemon is the single source of truth.** Neither CLI nor GUI ever modify config files or spawn services directly. All mutations go through the daemon's gRPC API. Protobuf contracts ensure version compatibility and strict typing.

---

## 5. Core Engine - Service Management

### Service Process Model (C# / System.Diagnostics)

Each managed service (Apache, Nginx, MySQL, MariaDB, PHP-FPM) is represented as a `ServiceUnit`:

```csharp
public enum ServiceState
{
    Stopped = 0,
    Starting = 1,
    Running = 2,
    Stopping = 3,
    Crashed = 4,
    Restarting = 5,
    Disabled = 6
}

public class ServiceUnit
{
    public string Id { get; set; }
    public ServiceState State { get; private set; }
    public int? ProcessId { get; private set; }  // null = no process
    public Process? Process { get; private set; }
    public CircularBuffer<string> LogBuffer { get; } = new(1000);  // last 1000 lines
    public int RestartCount { get; private set; }
    public DateTime? LastCrash { get; private set; }
    public ServiceConfiguration Config { get; set; }
    
    // State machine, crash recovery orchestrated here
    public async Task StartAsync(CancellationToken ct);
    public async Task StopAsync(CancellationToken ct);
    public async Task RestartAsync(CancellationToken ct);
}

public class RestartPolicy
{
    public int MaxRestarts { get; set; } = 5;
    public TimeSpan WindowDuration { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan BackoffBase { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan BackoffMax { get; set; } = TimeSpan.FromSeconds(30);
}
```

### State Machine

```
STOPPED ──StartAsync()──► STARTING ──ReadinessCheck()──► RUNNING
                               │                           │
                          fail()│                   Crash()│
                               ▼                           ▼
                            CRASHED ◄────────────────────CRASHED
                               │
              RestartPolicy.Check (within threshold)
                               ▼
                            RESTARTING ──► STARTING

RUNNING ──StopAsync()──► STOPPING ──ProcessExit()──► STOPPED
                             │
                        WaitForExit(10s)
                             ▼
                         Process.Kill() ──► STOPPED
```

**Key Difference from Go:** 
- Go uses goroutines as lightweight tasks; .NET uses `Task` and `CancellationToken` for the same purpose
- `Process.WaitForExitAsync()` replaces goroutine-based wait loops
- `Channel<T>` enables inter-task signaling (state machine transitions trigger events)

### Crash Recovery Logic

```csharp
private async Task HandleCrashRecoveryAsync(CancellationToken ct)
{
    LastCrash = DateTime.UtcNow;
    RestartCount++;
    
    if (RestartCount > Config.RestartPolicy.MaxRestarts 
        && (DateTime.UtcNow - LastCrashWindow) < Config.RestartPolicy.WindowDuration)
    {
        State = ServiceState.Disabled;
        await _eventBus.PublishAsync(new ServiceDegradedEvent(Id), ct);
        return;
    }
    
    // Exponential backoff: 2s, 4s, 8s, 16s, 30s (max)
    var backoff = TimeSpan.FromMilliseconds(
        Math.Min(
            Config.RestartPolicy.BackoffBase.TotalMilliseconds * Math.Pow(2, RestartCount - 1),
            Config.RestartPolicy.BackoffMax.TotalMilliseconds
        )
    );
    
    await Task.Delay(backoff, ct);
    await RestartAsync(ct);
}

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

### CLI Command Tree (System.CommandLine + Spectre.Console)

```
devforge daemon [start|stop|status]
devforge site create <domain> [--php=X.Y] [--server=apache|nginx] [--docroot=PATH] [--ssl]
devforge site list [--json]
devforge site delete <domain>
devforge site info <domain>
devforge site open <domain>
devforge site log <domain> [--follow] [--lines=100]
devforge service start [service]
devforge service stop [service]
devforge service restart [service]
devforge service status [--json]
devforge php list [--json]
devforge php install <version>
devforge php remove <version>
devforge php default <version>
devforge php extension <version> <name> [--enable|--disable]
devforge database list
devforge database create <name>
devforge database drop <name>
devforge database import <name> <file>
devforge database export <name> [--output=file]
devforge ssl trust
devforge ssl create <domain> [--wildcard]
devforge ssl status
devforge dns status
devforge dns flush
devforge config get <key>
devforge config set <key> <value>
devforge config list
devforge config rebuild
devforge plugin list [--json]
devforge plugin install <name>
devforge plugin remove <name>
```

### Global Flags

| Flag | Description |
|------|-------------|
| `--json` | Machine-readable JSON output (uses System.Text.Json) |
| `--no-color` | Disable Spectre.Console ANSI markup |
| `--quiet` / `-q` | Suppress progress indicators, errors only |
| `--verbose` / `-v` | Debug output (includes stack traces) |
| `--daemon-timeout=<ms>` | Override gRPC timeout (default: 30000ms) |

**Implementation:** System.CommandLine v18.x provides built-in command/option/argument structure. Spectre.Console handles progress bars, tables, syntax highlighting with zero JavaScript.

```csharp
// Example: devforge site create
var rootCommand = new RootCommand();
var siteCommand = new Command("site");

var createCommand = new Command("create")
{
    new Argument<string>("domain", "Site domain (e.g., myapp.local)"),
    new Option<string>("--php", () => "8.2", "PHP version"),
    new Option<string>("--docroot", "Document root path"),
    new Option<string>("--server", () => "apache", "apache or nginx"),
    new Option<bool>("--ssl", () => false, "Enable HTTPS"),
};

createCommand.SetHandler(async (domain, php, docroot, server, ssl, ct) =>
{
    var client = new DevForgeGrpcClient(); // gRPC client
    var response = await client.CreateSiteAsync(
        new CreateSiteRequest 
        { 
            Domain = domain, 
            PhpVersion = php, 
            DocumentRoot = docroot ?? Path.GetCurrentDirectory(),
            ServerType = server,
            SslEnabled = ssl
        },
        cancellationToken: ct
    );
    
    AnsiConsole.MarkupLine($"[green]✓[/] Site created: [bold]{response.Domain}[/]");
    AnsiConsole.MarkupLine($"  Document Root: [yellow]{response.DocumentRoot}[/]");
    AnsiConsole.MarkupLine($"  URL: [link]{$"http{(ssl ? "s" : "")}://{domain}"}[/]");
}, domainArg, phpOpt, docrootOpt, serverOpt, sslOpt);

siteCommand.AddCommand(createCommand);
rootCommand.AddCommand(siteCommand);
```

### gRPC API Specification (Protobuf)

Transport: Named pipe (Win) / Unix domain socket (macOS/Linux).

**Core Services:**

```protobuf
service DevForge {
  // Site Management
  rpc CreateSite(CreateSiteRequest) returns (SiteInfo);
  rpc ListSites(Empty) returns (ListSitesResponse);
  rpc DeleteSite(DeleteSiteRequest) returns (Empty);
  rpc GetSiteInfo(GetSiteInfoRequest) returns (SiteInfo);
  
  // Service Control
  rpc StartService(ServiceRequest) returns (ServiceResponse);
  rpc StopService(ServiceRequest) returns (ServiceResponse);
  rpc RestartService(ServiceRequest) returns (ServiceResponse);
  rpc GetServiceStatus(Empty) returns (ServiceStatusResponse);
  
  // PHP Management
  rpc InstallPhpVersion(PhpVersionRequest) returns (stream ProgressEvent);
  rpc ListPhpVersions(Empty) returns (ListPhpResponse);
  rpc SetDefaultPhp(PhpVersionRequest) returns (Empty);
  
  // Database Operations
  rpc CreateDatabase(CreateDatabaseRequest) returns (Empty);
  rpc ListDatabases(Empty) returns (ListDatabasesResponse);
  rpc ImportDatabase(stream ImportDatabaseRequest) returns (ImportDatabaseResponse);
  
  // SSL/TLS
  rpc CreateCertificate(CertificateRequest) returns (CertificateInfo);
  rpc TrustCertificateAuthority(Empty) returns (Empty);
  
  // Events (server-to-client streaming)
  rpc SubscribeEvents(EventFilter) returns (stream Event);
}

// Event message (supports service.started, site.created, etc.)
message Event {
  string topic = 1;          // e.g., "service.started"
  string id = 2;             // UUID
  google.protobuf.Timestamp timestamp = 3;
  google.protobuf.Struct data = 4;  // Flexible JSON-like payload
}
```

### Error Handling

gRPC uses standard HTTP/2 status codes. Custom app errors via `Rpc.Status` detail field:

```csharp
// Server throws
throw new RpcException(
    new Status(StatusCode.NotFound, "Domain not found"),
    new Metadata { { "error-code", "32002" } }
);

// Client catches
try 
{
    await client.DeleteSiteAsync(new DeleteSiteRequest { Domain = domain });
}
catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
{
    AnsiConsole.MarkupLine("[red]✗ Error:[/] Domain not found");
    Environment.Exit(1);
}
```

### Event System

Both CLI and GUI subscribe to gRPC server streaming events. Event topics: `service.*`, `site.*`, `php.*`, `config.*`, `health.*`

**CLI Usage:**
```csharp
var call = client.SubscribeEvents(new EventFilter { TopicPatterns = { "service.*" } });
await foreach (var @event in call.ResponseStream.ReadAllAsync(ct))
{
    AnsiConsole.MarkupLine($"[blue]{@event.Topic}[/] @ {@event.Timestamp}");
}
```

**GUI Usage:** Avalonia ViewModels bind to event streams via ReactiveUI or MVVM Toolkit, enabling real-time UI updates without polling.

---

## 13. UI/UX Design Specification

### Design Language (Avalonia UI + Fluent Design System)

- **Base Theme:** FluentTheme (built-in to Avalonia)
- **Dark mode default** (developer preference), light mode available
- **Font:** Segoe UI (system font) for UI, JetBrains Mono 12px for code/paths
- **Border radius:** 4px (Fluent standard, inputs/buttons), 8px (cards), 12px (dialogs)
- **Status colors:** Green (#107c10) running, Red (#da3b01) stopped, Yellow (#ffb900) warning, Blue (#0078d4) info
- **Animations:** 200ms easing for state changes (respects `prefers-reduced-motion`)

### Fluent Design System Implementation (Avalonia)

Avalonia 12.x includes `FluentTheme` which provides:
- Native control styling across Win/macOS/Linux
- Built-in dark/light mode switching
- Acrylic blur (Windows), vibrancy (macOS) backgrounds
- System color integration

```xaml
<!-- App.xaml -->
<Application>
  <Application.Styles>
    <FluentTheme Mode="Dark" AccentColor="#0078d4" />
  </Application.Styles>
</Application>
```

### Color Palette

| Token | Light | Dark | Usage |
|-------|-------|------|-------|
| Background | #ffffff | #1f1f1f | Canvas |
| Surface | #f3f3f3 | #2d2d2d | Cards, panels |
| Tertiary | #e0e0e0 | #3d3d3d | Dividers, subtle |
| Text Primary | #000000 | #ffffff | Main text |
| Text Secondary | #424242 | #a0a0a0 | Metadata |
| Success | #107c10 | #107c10 | ✓ Running |
| Error | #da3b01 | #f4573c | ✗ Stopped |
| Warning | #ffb900 | #ffd666 | ⚠ Warning |
| Info | #0078d4 | #4cc2ff | ℹ Info |

### Screen Layout (XAML)

```xaml
<!-- MainWindow.xaml -->
<Window>
  <DockPanel>
    <!-- Sidebar Navigation -->
    <StackPanel DockPanel.Dock="Left" Width="200" Background="{StaticResource SurfaceBrush}">
      <Button Content="Dashboard" Command="{Binding ShowDashboard}" />
      <Button Content="Sites" Command="{Binding ShowSites}" />
      <Button Content="PHP" Command="{Binding ShowPhp}" />
      <Button Content="Database" Command="{Binding ShowDatabase}" />
      <Button Content="SSL/TLS" Command="{Binding ShowSsl}" />
      <Button Content="Terminal" Command="{Binding ShowTerminal}" />
      <Button Content="Settings" Command="{Binding ShowSettings}" />
    </StackPanel>

    <!-- Main Content -->
    <ContentControl Content="{Binding CurrentView}" />
  </DockPanel>
</Window>
```

### Key Screens (Avalonia XAML)

1. **System Tray**
   - `TrayIcon` control in Avalonia (click opens MainWindow)
   - Context menu: Start All, Stop All, recent sites (virtualized list), Exit
   - Status indicator: Green/red circle overlay

2. **Dashboard** (`DashboardView.xaml`)
   - Service status cards (Apache, PHP-FPM, MySQL, dnsmasq)
   - Real-time resource graphs (LiveCharts2 for CPU, RAM, disk)
   - Quick action buttons (Start All, Stop All, Open Site)
   - Activity log (latest 10 events, auto-scrolling)

3. **Sites Manager** (`SitesManagerView.xaml`)
   - DataGrid (Avalonia built-in) with columns: Domain, Status, PHP Version, SSL, Actions
   - Right-click context menu: Open, Edit, Delete, View Logs
   - Right panel shows detailed site config when selected
   - Virtual scrolling for 50+ sites

4. **PHP Manager** (`PhpManagerView.xaml`)
   - List of installed PHP versions with actions (Set Default, Remove)
   - Extensions manager: toggle xdebug, intl, gd per version
   - Download new version UI (combo box + Install button)
   - php.ini editor (syntax highlighting via TextEditor control)

5. **Database Manager** (`DatabaseView.xaml`)
   - MySQL/MariaDB instances
   - Create new database dialog
   - Import/export buttons with file picker
   - phpMyAdmin link (opens browser)
   - Database list with size info

6. **SSL Manager** (`SslView.xaml`)
   - CA trust status (green if trusted, red if not)
   - Per-site cert status table (domain, expiry, trust status)
   - Create Certificate button (generates with mkcert)
   - Trust Certificate Authority button (runs elevated)

7. **Terminal** (`TerminalView.xaml`)
   - Embedded terminal via VT100 emulation (Terminal.Gui or custom ANSI parser)
   - Integrates with `devforge` CLI directly
   - Copy/paste support, scrollback buffer

8. **Settings** (`SettingsView.xaml`)
   - Listen ports (Apache, Nginx, MySQL)
   - Startup behavior (start on boot, minimize to tray)
   - DNS settings (dnsmasq config, domain suffix)
   - Theme selector (Light/Dark/System)
   - About & check for updates

### Keyboard Shortcuts & Input

| Shortcut | Action | Implementation |
|----------|--------|-----|
| `Ctrl+K` | Command palette | Window searches commands via XAML attached behavior |
| `Ctrl+T` | New terminal | Triggered via AcceleratorKey binding |
| `Ctrl+1-8` | Navigate sidebar | Button Click handlers with keyboard focus |
| `Ctrl+N` | New site | Command binding to CreateSiteCommand |
| `F5` | Refresh status | Calls `RefreshStatusAsync()` on ViewModel |
| `Ctrl+,` | Settings | Navigation via router/controller pattern |
| `Ctrl+W` / `Alt+F4` | Close window | Standard Avalonia behavior |

```xaml
<!-- Dashboard.xaml -->
<Window>
  <Window.KeyBindings>
    <KeyBinding Gesture="Ctrl+K" Command="{Binding ShowCommandPalette}" />
    <KeyBinding Gesture="Ctrl+N" Command="{Binding ShowCreateSiteDialog}" />
    <KeyBinding Gesture="F5" Command="{Binding RefreshStatusCommand}" />
  </Window.KeyBindings>
</Window>
```

### Accessibility (WCAG 2.1 AA)

- **Color + Shape:** Status indicators use color + icon (✓ Green, ✗ Red, ⚠ Orange)
- **Contrast Ratios:** Text 4.5:1, UI components 3:1 (verified with Contrast Ratio checker)
- **Focus Management:** Tab order defined via `TabIndex`, visible focus rect on all buttons
- **ARIA equivalents:** `AutomationProperties.Name`, `AutomationProperties.HelpText` on controls
- **Motion Reduction:** Animations respect `MediaQueryListener` for `prefers-reduced-motion`
- **Touch Targets:** All buttons/clickable elements minimum 44x44px
- **Keyboard Navigation:** All features accessible without mouse via Tab + Enter

### MVVM Pattern (ReactiveUI or MVVM Toolkit)

Example using MVVM Toolkit (Microsoft.Mvvm.Toolkit):

```csharp
public partial class DashboardViewModel : ObservableObject
{
    private readonly IDevForgeGrpcClient _client;
    
    [ObservableProperty]
    private ObservableCollection<ServiceStatusModel> services = new();
    
    [ObservableProperty]
    private bool isLoading;
    
    [RelayCommand]
    private async Task RefreshStatusAsync(CancellationToken ct)
    {
        IsLoading = true;
        try 
        {
            var response = await _client.GetServiceStatusAsync(ct);
            Services = new ObservableCollection<ServiceStatusModel>(
                response.Services.Select(s => MapToModel(s))
            );
        }
        finally { IsLoading = false; }
    }
    
    [RelayCommand]
    private async Task StartAllServicesAsync(CancellationToken ct) => 
        await _client.StartServiceAsync(new ServiceRequest { All = true }, cancellationToken: ct);
}
```

### State Management

- **View State** (UI visibility, selected tabs, scroll position) — ViewModel properties
- **App State** (daemon connection, cached site list) — Singleton service
- **Real-time Updates** — gRPC server-streaming events trigger ViewModel property changes

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
├── devforged.exe       (daemon)
├── devforge.exe        (CLI)
├── DevForge.exe        (GUI)
└── .NET 9 runtime files (if not using self-contained)
```

### Build & Publish Strategy (.NET 9)

**Self-Contained Build (Recommended for simplicity):**

```bash
# No external .NET runtime dependency
dotnet publish -c Release -r win-x64 --self-contained

# Output: 25-35 MB per platform (includes .NET runtime)
# Binaries: devforged.exe, devforge.exe, DevForge.exe
```

**Framework-Dependent Build (Smaller):**

```bash
# Requires .NET 9 Desktop Runtime installed
dotnet publish -c Release -r win-x64 --no-self-contained

# Output: 2-3 MB binaries only
# User must install .NET 9 Desktop Runtime from microsoft.com
```

**Decision:** Use **self-contained** for consumer distribution (no prerequisites), **framework-dependent** for enterprise/CI deployments (faster updates).

### Installers

| Platform | Format | Size | Tool |
|----------|--------|------|------|
| Windows | MSI (WiX Toolset) | ~300-500MB compressed | `dotnet new wix` + WiX 4 |
| Windows Portable | .7z archive | ~250-350MB | 7-Zip CLI |
| macOS | DMG + notarization | ~400-600MB | `create-dmg` + Apple signing |
| Linux | AppImage + snap | ~300-500MB | `appimagetool` + snapcraft |

**Windows Installer (WiX MSI) — Recommended Over NSIS**

```xml
<!-- DevForge.wixproj / Product.wxs (WiX 4) -->
<Product>
  <Feature Id="ProductFeature">
    <ComponentRef Id="DevForgeDaemon" />
    <ComponentRef Id="DevForgeCLI" />
    <ComponentRef Id="DevForgeGUI" />
    <ComponentRef Id="PHPBinaries" />
    <ComponentRef Id="ApacheBinaries" />
    <ComponentRef Id="MySQLBinaries" />
  </Feature>
  
  <UI>
    <UIRef Id="WixUI_InstallDir" />
    <Publish Dialog="ExitDialog" Control="Finish" Event="LaunchApplication">
      [WIXUI_EXITDIALOGOPTIONALCHECKBOXCHECKED]
    </Publish>
  </UI>
</Product>

<!-- Build -->
dotnet build DevForge.wixproj
# Outputs: DevForge.msi (signed)
```

**Why MSI over NSIS:**
1. **Lower AV false positive rate** — Windows trusts MSI format more than NSIS .exe stubs
2. **Automatic repair & uninstall** — Windows Installer handles rollback
3. **License & documentation integration** — built-in EULA flow
4. **Per-machine vs. per-user install** — flexible deployment options

### Code Signing (.NET 9 Binaries)

**.NET binaries do NOT trigger Defender heuristics** like Go binaries do. However, code signing still recommended for:
1. SmartScreen reputation building (consumer trust)
2. Chain-of-custody compliance (enterprise requirements)

```bash
# Sign .NET binaries post-publish
signtool sign /f cert.pfx /p password /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 \
  bin/Release/net9.0-windows/publish/*.exe

# Sign MSI
signtool sign /f cert.pfx /p password /fd SHA256 /tr http://timestamp.digicert.com \
  DevForge.msi
```

**Certificate Strategy:**
- **OV (Organization Validation) Code Signing Certificate** ~$200-400/year — sufficient for .NET
- Do NOT use EV certificate (unnecessary cost for .NET; EV required for Go false positives)
- Renewal before expiry to maintain SmartScreen reputation chain

### .NET Framework Dependent vs. Self-Contained Tradeoff

| Aspect | Framework-Dependent | Self-Contained |
|--------|---------------------|-----------------|
| Download size | 2-3 MB binaries | 25-35 MB (includes .NET 9 runtime) |
| Install size | 5-10 MB (+ .NET SDK) | 60-80 MB total |
| First launch | Fast (runtime pre-installed) | Slightly slower (JIT warm-up) |
| Updates | Small binary patches | Full re-download |
| Prerequisites | .NET 9 Desktop Runtime | None |
| Enterprise | Easier (IT manages .NET) | No dependency management |
| Consumer | Requires .NET installer download | Single ZIP/MSI download |

**Recommendation:** Self-contained for initial releases (consumer expectations), transition to framework-dependent after market penetration (~v1.5+).

### Auto-Update System

**Strategy: Delta updates via Sparkle.NET or custom updater**

```csharp
// Custom updater service
public class UpdateService : BackgroundService
{
    private const string UpdateCheckUrl = "https://cdn.devforge.dev/releases/latest.json";
    
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Check for updates every 24 hours
            var response = await _http.GetAsync(UpdateCheckUrl, ct);
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(
                await response.Content.ReadAsStringAsync(ct)
            );
            
            if (manifest.Version > CurrentVersion && manifest.IsSigned)
            {
                // Download delta binary if available, else full binary
                await DownloadAndVerifyAsync(manifest, ct);
                // Restart daemon with graceful handoff of services
                await RestartWithoutDowntimeAsync(ct);
            }
            
            await Task.Delay(TimeSpan.FromHours(24), ct);
        }
    }
}
```

**Update Manifest (CDN-hosted):**
```json
{
  "version": "1.0.5",
  "releaseDate": "2026-04-15T00:00:00Z",
  "downloadUrl": "https://cdn.devforge.dev/releases/v1.0.5/DevForge.msi",
  "sha256": "abc123...",
  "signature": "...", // Ed25519
  "releaseNotes": "Fixed PHP 8.4 compatibility issue",
  "channels": ["stable", "beta", "nightly"]
}
```

**Key features:**
- Signed updates (Ed25519 verify, public key embedded in app)
- Delta patches for minor versions (if binary size substantial)
- Rollback: keep previous 2 versions in `%LocalAppData%\DevForge\versions/`
- Graceful restart: notify connected clients, finish in-flight operations, restart daemon
- Opt-in beta channel: Users can enable pre-release testing

### Binary Integrity & Verification

**Pre-release verification (CI/CD):**

```powershell
# Generate checksums
Get-FileHash bin/Release/net9.0-windows/publish/*.exe | Format-List > checksums.sha256

# Scan with VirusTotal (optional, not required for .NET)
# vt scan file bin/Release/net9.0-windows/publish/devforged.exe

# Sign MSI
dotnet tool run signtool sign /fd SHA256 ...
```

### CDN Infrastructure

- **Primary:** AWS S3 + CloudFront (CDN edge locations globally)
- **Fallback:** GitHub Releases (no bandwidth charges)
- **Estimated costs (10K active users, 3 major releases/month):**
  - S3 storage: ~$5/month
  - CloudFront egress: ~$200-300/month (10TB/month @ 3 releases)
  - Total: ~$250-350/month

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

### Technology Stack Review (C# / .NET 9 + Avalonia UI)

**All implementation phases use single .NET solution** — no polyglot friction.

### Phase 1 — Foundation (Weeks 1-3)

**Deliverables:** Core daemon architecture, gRPC service layer, database schema

- [ ] .NET 9 solution structure (DevForge.Daemon, DevForge.Core, DevForge.Cli, DevForge.Tests)
- [ ] `IHostedService` daemon skeleton with graceful shutdown
- [ ] gRPC service definitions (protos/devforge.proto) with basic stubs
- [ ] `GrpcDotNetNamedPipes` integration for Windows IPC
- [ ] SQLite schema + EF Core migrations (ServiceUnit, Site, PluginMetadata tables)
- [ ] Event bus implementation (`Channel<Event>` pub/sub)
- [ ] IPlatformAbstraction interface (Windows/Unix implementations)
  - Process spawning via `System.Diagnostics.Process`
  - UAC elevation via `ProcessStartInfo.UseShellExecute + Verb="runas"`
  - File ACL management via `FileSecurity` (Windows) / Unix permissions (Linux)

**Effort:** 1 developer, ~12-14 days

### Phase 2 — Core Services (Weeks 4-6)

**Deliverables:** Service management, config pipeline, health monitoring

- [ ] `ServiceManager` implementation
  - Apache/Nginx ServiceUnit (Start/Stop/Reload, config validation via `httpd -t` / `nginx -t`)
  - PHP-FPM ServiceUnit (per-site pool generation, multi-version support)
  - MySQL/MariaDB ServiceUnit (initialization, startup sequence)
  - Health check loop (`BackgroundService`) with exponential backoff restart policy
  - Port conflict detection (scan 80, 443, 3306, 5432, etc.)

- [ ] `ConfigurationPipeline`
  - TOML parsing via `Tomlyn` NuGet package
  - Template rendering via `Scriban` (modern, zero-JS alternative to Handlebars)
  - Config validation: parse → render → execute `httpd -t` → atomic file swap
  - Version archiving (keep last 5 versions, atomic rollback support)

- [ ] Startup sequence optimization (target: < 3 seconds)
  - Parallel MySQL + dnsmasq launch via `Task.WhenAll()`
  - Pre-generate configs at site creation (not at startup)
  - Skip validation on cached configs if timestamp unchanged

**Effort:** 2 developers, ~12-16 days

### Phase 3 — Site Management (Weeks 7-9)

**Deliverables:** VHost CRUD API, SSL/TLS module, DNS integration, CLI client

- [ ] VHost CRUD gRPC endpoints (CreateSite, ListSites, DeleteSite, etc.)
  - TOML file generation per site
  - Apache VirtualHost / Nginx server block generation
  - PHP-FPM pool creation (one pool per site + PHP version combo)

- [ ] SSL module
  - `mkcert` integration via `System.Diagnostics.Process`
  - Certificate authority trust (Windows: CertMgr.exe, Unix: update-ca-certificates)
  - Per-site cert generation + renewal tracking

- [ ] DNS/Hosts module
  - Windows: Direct registry modification (HKEY_LOCAL_MACHINE\System\CurrentControlSet\Services\Tcpip\Parameters\Interfaces) OR hosts file + elevation
  - Unix: /etc/hosts file management with sudo prompt
  - dnsmasq configuration for *.local suffix wildcard resolution

- [ ] CLI client (System.CommandLine + Spectre.Console)
  - All `site:*`, `service:*`, `php:*` commands with rich output
  - `--json` flag for machine-readable output
  - Progress indicators, table rendering, syntax highlighting
  - Context-aware help (e.g., `devforge site create --help`)

- [ ] Configuration import/export (TOML serialization)

**Effort:** 2 developers, ~15-18 days

### Phase 4 — GUI (Weeks 10-12)

**Deliverables:** Avalonia UI application with all core features

- [ ] Avalonia UI scaffolding + FluentTheme setup
  - MainWindow with sidebar navigation
  - MVVM architecture (ReactiveUI or MVVM Toolkit)
  - gRPC client integration (shared with CLI)

- [ ] Core screens (XAML + C# ViewModels)
  - Dashboard: Service status cards, real-time graphs (LiveCharts2)
  - Sites Manager: DataGrid with CRUD operations
  - PHP Manager: Version list, extensions toggle, install new versions
  - Database Manager: MySQL/MariaDB instance mgmt
  - SSL Manager: Per-site cert status, generation
  - Terminal: Embedded terminal (xterm.js-like, or simple Process output pipe)
  - Settings: Ports, startup behavior, theme selector

- [ ] System tray integration
  - `TrayIcon` control in Avalonia (show/hide window, context menu)
  - Status indicator (green/red circle)
  - Recent sites quick launch
  - Start/Stop All buttons

- [ ] Live event streaming
  - Avalonia ViewModels subscribe to gRPC `SubscribeEvents` stream
  - UI updates via `ObservableProperty` bindings
  - No polling, fully reactive

- [ ] Keyboard shortcuts
  - `Ctrl+K`: Command palette (filter commands, execute)
  - `Ctrl+N`: New site dialog
  - `F5`: Refresh status
  - `Ctrl+T`: New terminal tab (if implemented)

**Effort:** 2-3 developers, ~18-21 days

### Phase 5 — Polish & Ecosystem (Weeks 13-15)

**Deliverables:** Additional services, plugin system, installers

- [ ] Nginx ServiceUnit (parity with Apache)
- [ ] MariaDB ServiceUnit (parity with MySQL)
- [ ] Redis/Memcached plugin stubs (for plugin marketplace proof-of-concept)
- [ ] Plugin host implementation
  - Lua 5.4 embedded runtime via `MoonSharp` or `Lua.NET`
  - Plugin sandboxing via `AppDomain` (deprecated in .NET 9, use custom permission model instead)
  - Hook system: `before_site_create`, `after_service_start`, etc.
  - Plugin marketplace scaffolding (HTTP endpoint returning JSON)

- [ ] Installer generation (WiX MSI toolset)
  - Windows MSI with automatic PHP/Apache/MySQL bundling
  - macOS DMG (notarization required)
  - Linux AppImage
  - Portable .7z archive option

- [ ] Auto-update service (Sparkle.NET or custom implementation)
  - Update manifest JSON fetch + signature verification
  - Delta binary download (if binary size > 10MB)
  - Graceful restart with in-flight operation completion

**Effort:** 2 developers, ~14-18 days

### Phase 6 — Documentation & Launch (Weeks 16-18)

**Deliverables:** User documentation, migration tools, beta release

- [ ] User documentation
  - Getting Started guide (3 OSes × 3 install methods)
  - CLI reference (all commands with examples)
  - API documentation (gRPC proto + example client code)
  - Troubleshooting guide (40+ common issues)
  - FAQ & video tutorials

- [ ] MAMP PRO migration tool
  - Import vhosts from MAMP SQLite database
  - Convert to DevForge TOML format
  - Database backup utilities

- [ ] Community feedback integration
  - GitHub Issues triage
  - Discord/Slack community setup
  - Public beta (v0.9.0-beta)

- [ ] v1.0.0 GA release
  - Final bug fixes
  - Performance optimization (target: < 3s startup, < 250MB idle)
  - Security audit (code review, fuzzing)

**Effort:** 2 developers, ~10-14 days

### Phase 7 — Advanced Features (Post-v1.0)

- [ ] Team management (multi-user setup, permissions)
- [ ] Cloud sync (DevForge config backup to user's cloud storage)
- [ ] Advanced SSL (wildcard certs, custom CAs, ACME integration)
- [ ] Container integration (Traefik reverse proxy, Docker CLI shim)
- [ ] IDE plugins (VS Code, JetBrains IDEs)

### Estimated Effort Summary

| Phase | Duration | Team | Deliverable |
|-------|----------|------|-------------|
| 1 — Foundation | 3 weeks | 1 dev | Core daemon + gRPC |
| 2 — Core Services | 3 weeks | 2 devs | Service mgmt + config pipeline |
| 3 — Site Management | 3 weeks | 2 devs | VHost CRUD + SSL + CLI |
| 4 — GUI | 3 weeks | 2-3 devs | Avalonia UI, all screens |
| 5 — Polish & Ecosystem | 3 weeks | 2 devs | Plugins, installers, updater |
| 6 — Documentation & Launch | 3 weeks | 2 devs | Docs, migration tool, GA |
| **Total (MVP to v1.0)** | **18 weeks** | **2-3 devs** | **Production-ready product** |

**Notes:**
- Timeline assumes parallel work on Windows and Unix implementations (CI/CD coverage)
- Phase 1-3 can shift to Phase 4 as soon as gRPC stubs are ready (CLI testing begins mid-Phase 3)
- Each phase includes testing (xUnit + integration tests) and CI/CD pipeline setup
- Burn-down tracking via GitHub Project board + weekly demos

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
