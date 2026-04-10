# DevForge — Implementation Specification

**Version:** 1.0.0
**Date:** 2026-04-09
**Status:** Definitive specification for implementation
**Author:** Solo developer (Lukas) + Claude Code

---

## Table of Contents

1. [Project Overview](#1-project-overview)
2. [Technology Stack](#2-technology-stack)
3. [Solution Structure](#3-solution-structure)
4. [Architecture](#4-architecture)
5. [Core Modules](#5-core-modules)
6. [Database Schema](#6-database-schema)
7. [gRPC API](#7-grpc-api)
8. [Configuration Pipeline](#8-configuration-pipeline)
9. [Service Management](#9-service-management)
10. [PHP Version Manager](#10-php-version-manager)
11. [Virtual Host Manager](#11-virtual-host-manager)
12. [DNS / Hosts Manager](#12-dns--hosts-manager)
13. [SSL Certificate Manager](#13-ssl-certificate-manager)
14. [Database Manager](#14-database-manager)
15. [CLI Interface](#15-cli-interface)
16. [GUI Specification](#16-gui-specification)
17. [Plugin System](#17-plugin-system)
18. [Security Model](#18-security-model)
19. [Performance Targets](#19-performance-targets)
20. [Testing Strategy](#20-testing-strategy)
21. [Deployment and Packaging](#21-deployment-and-packaging)
22. [Implementation Phases](#22-implementation-phases)
23. [File Layout on Disk](#23-file-layout-on-disk)

---

## 1. Project Overview

### What Is DevForge

DevForge is a local development server manager. It replaces MAMP PRO / XAMPP / WampServer with a modern, cross-platform, extensible tool. It manages Apache, Nginx, MySQL, MariaDB, PHP-FPM (multiple simultaneous versions), Redis, and Mailpit as local processes — no Docker.

### Why It Exists — Specific MAMP PRO Pain Points

Every feature in this spec exists to fix a concrete bug or missing feature in MAMP PRO:

| MAMP PRO Problem | DevForge Solution |
|---|---|
| Config corruption: SQLite stores empty values → Apache syntax error | Config validation pipeline: render → `httpd -t` → atomic write |
| No CLI | Full CLI: `devforge new myapp.loc --php=8.2 --db --ssl` |
| SSL / OpenSSL EC key failure on Windows | mkcert with bundled OpenSSL 3.x |
| Alias bug: changing one vhost breaks all others | Independent per-site config files |
| Restart overwrites manually fixed config | Atomic writes + config versioning (5 generations) |
| No MySQL management | Full DB manager: create, backup, import/export |
| PHP version confusion, no CLI aliases | Clear global default + per-site override + `php82`, `php84` aliases |
| No php.ini for CLI | Managed php.ini per version |
| Two httpd.conf files, unclear which is canonical | Single generated config, templates are source of truth |

### Target User

Solo developer, Lukas:
- 15–30 active local websites
- Primarily Nette PHP, also Node.js and Python projects
- Uses `.loc` TLD: `nks-web.loc`, `chatujme.loc`
- 2x Windows 10/11 PCs + 1x macOS (identical experience required)
- Currently on MAMP PRO, frustrated with above bugs

### Positioning

"The Laragon experience, everywhere, with config validation and a plugin ecosystem."

Competitive advantages over the closest competitor (FlyEnv):
1. Config validation before applying (FlyEnv has none)
2. Full CLI interface (FlyEnv is GUI-only)
3. Process supervision with auto-restart (FlyEnv doesn't restart crashed services)
4. Lighter: .NET ~50–90 MB vs Electron 150 MB+
5. Lower RAM: ~40–80 MB vs 150–300 MB
6. Per-site config files (FlyEnv uses electron-store JSON, no validation)

---

## 2. Technology Stack

### Final Decisions — Do Not Change

| Component | Choice | Reason |
|---|---|---|
| Language | C# / .NET 9 | Single language for daemon + GUI; developer knows C#; no AV false positive |
| GUI framework | Avalonia UI 12.x | MIT, TrayIcon built-in, FluentTheme dark/light, Skia rendering (no WebView), $3M Devolutions backing |
| Daemon pattern | .NET Worker Service (IHostedService) | First-party .NET pattern, fits service lifecycle |
| IPC | gRPC (Grpc.AspNetCore + Grpc.Net.Client) | Typed, streaming, first-party Microsoft + Google support |
| Process management | System.Diagnostics.Process | .NET built-in |
| Charts | LiveCharts2 for Avalonia | Native Avalonia integration |
| System tray | Avalonia TrayIcon (built-in) | No third-party dependency |
| Plugin loading | AssemblyLoadContext + IServiceModule interface | .NET built-in, standard plugin pattern |
| Storage | SQLite via Microsoft.Data.Sqlite + Dapper | Lightweight, no server needed |
| Config templates | Scriban | Liquid-like syntax, safe, well-maintained |
| Theme | Avalonia FluentTheme | Built-in dark/light |
| CLI parser | System.CommandLine | First-party Microsoft, shell completions. Use `Spectre.Console` for OUTPUT ONLY (tables, progress bars, colors). Do NOT use `Spectre.Console.Cli` for command parsing. |
| MVVM | CommunityToolkit.Mvvm | Lightweight, source generator–based |
| Testing | xUnit + Moq | Standard .NET stack |
| Installer (Windows) | Inno Setup (portable) + WiX MSI (signed) | Standard |
| CI/CD | GitHub Actions | Standard |

### Why C# / Avalonia (AV False Positive Decision)

This is the most important architectural decision and must not be revisited without very good reason.

- Go binaries are flagged by Windows Defender (microsoft/go#1255, OPEN as of 2026, no fix planned by Microsoft)
- Flutter desktop binaries are flagged (flutter#46696, closed "not planned")
- .NET Framework-Dependent Executable (FDE) is NOT flagged by Defender
- Developer has $0 budget for EV code signing
- C# / Avalonia is the only option that is: cross-platform + native GUI + no WebView + no AV false positive + known to the developer

### Publish Command

```bash
dotnet publish --runtime win-x64 --self-contained true
# DO NOT add /p:PublishTrimmed=true — trimming triggers Defender heuristics
```

Bundle the .NET runtime in the installer. This is the standard AppHost.exe pattern and is not flagged.

---

## 3. Solution Structure

```
DevForge.sln
src/
├── DevForge.Core/              # Shared types, interfaces, config models
│   ├── Models/                 # Site, Service, PhpVersion, Certificate, Database
│   ├── Interfaces/             # IServiceModule, IConfigProvider, IHostsManager
│   ├── Configuration/          # AppConfig, SiteConfig, TOML loading
│   └── Proto/                  # .proto files for gRPC (shared between daemon and clients)
│
├── DevForge.Daemon/            # Background service — owns all child processes
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
├── DevForge.Gui/               # Avalonia desktop application
│   ├── App.axaml               # Application entry, theme registration
│   ├── ViewModels/             # MVVM ViewModels (CommunityToolkit.Mvvm)
│   ├── Views/                  # .axaml views per screen
│   ├── Controls/               # Reusable controls: ServiceCard, SiteCard, PhpVersionBadge
│   └── Services/               # GrpcClientService, ThemeService, NotificationService
│
├── DevForge.Cli/               # CLI client (System.CommandLine)
│   ├── Program.cs
│   └── Commands/               # SiteCommand, ServiceCommand, PhpCommand, DbCommand, SslCommand
│
└── DevForge.Tests/
    ├── Core.Tests/
    ├── Daemon.Tests/
    ├── Cli.Tests/
    └── Gui.Tests/              # Avalonia Headless testing
```

### Project Dependencies

```
DevForge.Core       (no dependencies on other DevForge projects)
DevForge.Daemon     → DevForge.Core
DevForge.Gui        → DevForge.Core
DevForge.Cli        → DevForge.Core
DevForge.Tests.*    → all above
```

### Key NuGet Packages

**DevForge.Core:**
- `Google.Protobuf`
- `Grpc.Tools`
- `Tomlyn` (TOML parsing)

**DevForge.Daemon:**
- `Grpc.AspNetCore` 2.76.0+
- `Microsoft.Data.Sqlite` 9.0+
- `Dapper` 2.1+
- `Scriban` 7.1.0+ (template engine for Apache/Nginx config generation)
- `CliWrap` 3.10.1+ (one-shot subprocess: httpd -t, mysqladmin, mkcert)
- `Microsoft.Extensions.Hosting`
- `Microsoft.Extensions.Logging`
- `Serilog` 4.3+ + `Serilog.Sinks.File` 7.0+ (structured logging)
- `dbup-sqlite` 6.0+ (schema migrations)

**DevForge.Gui:**
- `Avalonia` (12.x)
- `Avalonia.Desktop`
- `Avalonia.Themes.Fluent`
- `Avalonia.Controls.DataGrid`
- `LiveChartsCore.SkiaSharpView.Avalonia` (**VERIFY Avalonia 12 compat on day 1** — stable 2.0.0 declares `>= Avalonia 11.0.0`, no 12.x confirmed. If fails, try ScottPlot.Avalonia — OxyPlot.Avalonia is also broken on 12.x)
- `Grpc.Net.Client`
- `CommunityToolkit.Mvvm`
- `HotAvalonia` (Debug only — XAML hot reload)

**DevForge.Cli:**
- `System.CommandLine` 2.0.5+ (command parsing — now STABLE as of 2026-03-12)
- `Grpc.Net.Client` 2.76.0+
- `Spectre.Console` 0.55.0 (output formatting ONLY — tables, progress bars, colors. Do NOT use Spectre.Console.Cli for parsing)
- `CliWrap` 3.10.1+ (one-shot subprocess calls: mkcert, httpd -t, mysqladmin)

**DevForge.Tests:**
- `xunit`
- `Moq`
- `Avalonia.Headless.XUnit`
- `Microsoft.Data.Sqlite` (in-memory for DB tests)

---

## 4. Architecture

### Core Principle: Daemon Is Source of Truth

Neither the GUI nor CLI ever modify config files, spawn services, or touch the hosts file directly. Every mutation goes through the daemon's gRPC API. The daemon is the single source of truth.

### Component Diagram

```
┌──────────────────────────────┐   ┌────────────────────────────────┐
│  DevForge.Gui (Avalonia)     │   │  DevForge.Cli (System.CommandLine)
│  - Main window               │   │  - devforge start apache       │
│  - System tray               │   │  - devforge new myapp.loc      │
│  - LiveCharts2 metrics       │   │  - devforge db:import mydb ... │
└──────────┬───────────────────┘   └────────────┬───────────────────┘
           │ gRPC over named pipe               │ gRPC over named pipe
           │ (Windows: \\.\pipe\devforge)       │ (macOS/Linux: unix socket)
           └──────────────┬────────────────────┘
                          │
           ┌──────────────▼──────────────────────────────────────┐
           │  DevForge.Daemon (Worker Service)                    │
           │                                                      │
           │  ProcessManager   HealthMonitor   MetricsCollector  │
           │  ┌────────────┐  ┌────────────┐  ┌───────────────┐ │
           │  │ Apache     │  │ Nginx      │  │ MySQL         │ │
           │  │ Module     │  │ Module     │  │ Module        │ │
           │  ├────────────┤  ├────────────┤  ├───────────────┤ │
           │  │ PHP-FPM    │  │ Redis      │  │ Mailpit       │ │
           │  │ Module     │  │ Module     │  │ Module        │ │
           │  └────────────┘  └────────────┘  └───────────────┘ │
           │                                                      │
           │  ConfigEngine     SslManager    HostsFileManager    │
           │  PluginLoader     DbManager     DnsFlush            │
           │                                                      │
           │  SQLite (state.db)                                   │
           └──────────────────────────────────────────────────────┘
```

### IPC Transport

| Platform | Transport |
|---|---|
| Windows | Named pipe `\\.\pipe\devforge-daemon` |
| macOS / Linux | Unix domain socket `~/.devforge/daemon.sock` |

gRPC runs over these transports using `GrpcDotNetNamedPipes` on Windows and the built-in Unix socket support on macOS/Linux.

### Daemon Lifecycle

1. Check for existing PID lock (`~/.devforge/daemon.pid`). If stale, clean up.
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

## 5. Core Modules

### 5.1 ProcessManager

Manages the lifecycle of each service process. Each service is a `ServiceUnit` running in its own supervised Task.

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

**State machine:**

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

**Restart policy:**

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

### 5.2 HealthMonitor

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

### 5.3 Supporting Types

```csharp
public enum ServiceType { WebServer, Database, PhpRuntime, Cache, Mail, Proxy, Custom }

public enum ServiceState { Stopped, Starting, Running, Stopping, Crashed, Disabled }

public record ServiceStatus(
    ServiceState State,
    int? Pid,
    TimeSpan Uptime,
    int RestartCount,
    double CpuPercent,
    long MemoryBytes);

public record ValidationResult(bool IsValid, IReadOnlyList<string> Errors);

public record CliCommandDefinition(
    string Name,
    string Description,
    Func<string[], Task<int>> Handler);
```

### 5.4 IServiceModule Interface

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
    IReadOnlyList<CliCommandDefinition> GetCliCommands() => Array.Empty<CliCommandDefinition>();
}
```

### 5.4 Parallel Startup Sequence (target: <3 seconds)

```
T=0ms    Daemon starts, reads config
T=0ms    [PARALLEL] MySQL start + PHP-FPM version pools start
T=800ms  MySQL socket accepting connections
T=1200ms PHP-FPM pools ready (Unix sockets listening)
T=1200ms [PARALLEL] Apache/Nginx start (depends on PHP-FPM)
T=1600ms Web servers ready, all vhosts responding
T=2000ms Health checks pass → "All services ready" event
```

Key: Direct process launch (NOT Windows Services, saves 400–800ms). Pre-generate all configs at site creation, not at startup. PHP-FPM starts in parallel with MySQL (no dependency).

---

## 6. Database Schema

### Location

`~/.devforge/data/state.db` (or `%APPDATA%\DevForge\data\state.db` on Windows)

### PRAGMA Settings

```sql
PRAGMA foreign_keys = ON;
PRAGMA journal_mode = WAL;
PRAGMA busy_timeout = 5000;
PRAGMA synchronous = NORMAL;
```

### Tables

The schema is already prototyped and tested (49 tests passing). Located at `prototype/database/`. Use it as the canonical DDL source. The application load order is:

1. `migrations/001_initial.sql` — all tables with constraints
2. `triggers.sql` — audit trail triggers, `updated_at` auto-update, business rules
3. `views.sql` — dashboard views
4. `indexes.sql` — performance indexes
5. `seed.sql` — default settings and services (first run only)

### Table Summary

| Table | Purpose |
|---|---|
| `schema_migrations` | Migration version tracking |
| `settings` | Key/value global config, grouped by category |
| `php_versions` | Installed PHP versions with extensions, ini overrides |
| `services` | Managed services: Apache, MySQL, etc. with status |
| `sites` | Virtual hosts: domain, docroot, PHP version, SSL |
| `certificates` | SSL certificates: paths, validity, auto-renew |
| `databases` | MySQL/MariaDB databases per site |
| `plugins` | Installed plugins with permissions |
| `config_history` | Full audit trail for all configuration changes |

### Key Constraints (enforce at both DB and application layer)

- `settings`: Unique `(category, key)`. JSON values validated with `json_valid()`.
- `php_versions`: Only one `is_default=1` (enforced by trigger). Cannot delete default version.
- `services`: Port range 1–65535. Port and ssl_port must differ.
- `sites`: SSL enabled requires `certificate_id NOT NULL`. Domain unique. Webserver in (`apache`, `nginx`).
- `certificates`: `valid_from < valid_until`. Issuer in (`self-signed`, `mkcert`, `letsencrypt`, `acme`, `custom`).
- `databases`: Name matches `[a-zA-Z0-9_]`. Unique `(name, service_id)`.
- `plugins`: Slug matches `[a-z0-9-]`.
- `config_history`: Entity type and operation are enums. JSON columns validated.

### Migration System

C# migration runner in `DevForge.Daemon`:
- Reads `schema_migrations` table to find current version
- Applies pending `.sql` files from `migrations/` directory in version order
- Backs up database before applying migrations
- Records version, name, checksum, execution_ms in `schema_migrations`
- Supports `_down.sql` files for rollback

### Views Available

| View | Purpose |
|---|---|
| `v_active_sites` | Active sites with resolved PHP version and cert info |
| `v_service_dashboard` | Services with status colors, listen addresses, site counts |
| `v_expiring_certs` | Certificates expiring within 30 days with urgency level |
| `v_recent_changes` | Last 50 config changes with human-readable summary |
| `v_site_summary` | Site counts grouped by status and webserver |

---

## 7. gRPC API

### Proto File Location

`src/DevForge.Core/Proto/devforge.proto`

This file is compiled by both `DevForge.Daemon` (server) and `DevForge.Gui` / `DevForge.Cli` (clients). Place it in `DevForge.Core` and reference it from all projects.

### Service Definition

```protobuf
syntax = "proto3";
package devforge.v1;

import "google/protobuf/empty.proto";
import "google/protobuf/timestamp.proto";

service DevForgeService {
  // Daemon health
  rpc GetStatus(google.protobuf.Empty) returns (DaemonStatus);

  // Service management
  rpc StartService(ServiceRequest) returns (ServiceResponse);
  rpc StopService(ServiceRequest) returns (ServiceResponse);
  rpc RestartService(ServiceRequest) returns (ServiceResponse);
  rpc GetServiceStatus(ServiceRequest) returns (ServiceStatusResponse);
  rpc ListServices(google.protobuf.Empty) returns (ServiceListResponse);

  // Site management
  rpc CreateSite(CreateSiteRequest) returns (SiteResponse);
  rpc UpdateSite(UpdateSiteRequest) returns (SiteResponse);
  rpc DeleteSite(DeleteSiteRequest) returns (google.protobuf.Empty);
  rpc GetSite(SiteRequest) returns (SiteResponse);
  rpc ListSites(google.protobuf.Empty) returns (SiteListResponse);

  // PHP version management
  rpc ListPhpVersions(google.protobuf.Empty) returns (PhpVersionListResponse);
  rpc SetDefaultPhpVersion(SetDefaultPhpRequest) returns (PhpVersionResponse);
  rpc InstallPhpVersion(PhpInstallRequest) returns (stream ProgressUpdate);

  // Database management
  rpc ListDatabases(google.protobuf.Empty) returns (DatabaseListResponse);
  rpc CreateDatabase(CreateDatabaseRequest) returns (DatabaseResponse);
  rpc DropDatabase(DropDatabaseRequest) returns (google.protobuf.Empty);
  rpc ImportDatabase(stream ImportChunk) returns (ImportResult);
  rpc ExportDatabase(ExportRequest) returns (stream ExportChunk);
  rpc CreateDbUser(CreateDbUserRequest) returns (DbUserResponse);
  rpc GrantDbAccess(GrantDbAccessRequest) returns (google.protobuf.Empty);

  // SSL management
  rpc GenerateCert(GenerateCertRequest) returns (CertResponse);
  rpc InstallCa(google.protobuf.Empty) returns (CaInstallResponse);
  rpc ListCerts(google.protobuf.Empty) returns (CertListResponse);

  // DNS / hosts management
  rpc GetHostsStatus(google.protobuf.Empty) returns (HostsStatusResponse);
  rpc FlushDns(google.protobuf.Empty) returns (google.protobuf.Empty);

  // Log streaming (server-side streaming)
  rpc StreamLogs(LogRequest) returns (stream LogEntry);

  // Metrics streaming (server-side streaming)
  rpc StreamMetrics(MetricsRequest) returns (stream ServiceMetrics);

  // Plugin management
  rpc ListPlugins(google.protobuf.Empty) returns (PluginListResponse);
  rpc InstallPlugin(PluginInstallRequest) returns (stream ProgressUpdate);
  rpc RemovePlugin(PluginRequest) returns (google.protobuf.Empty);
  rpc EnablePlugin(PluginRequest) returns (PluginResponse);
  rpc DisablePlugin(PluginRequest) returns (PluginResponse);
}
```

### Key Message Types

```protobuf
message DaemonStatus {
  string version = 1;
  bool running = 2;
  int32 uptime_seconds = 3;
  repeated ServiceStatusResponse services = 4;
}

message CreateSiteRequest {
  string domain = 1;
  string document_root = 2;
  string php_version = 3;       // e.g. "8.2"
  string webserver = 4;         // "apache" or "nginx"
  bool ssl_enabled = 5;
  bool create_database = 6;
  string database_name = 7;     // optional
  string framework = 8;         // "nette", "laravel", "wordpress", "generic"
  repeated string aliases = 9;  // *.myapp.loc wildcard aliases
  string custom_directives = 10;
}

message SiteResponse {
  int64 id = 1;
  string domain = 2;
  string document_root = 3;
  string php_version = 4;
  string webserver = 5;
  bool ssl_enabled = 6;
  string status = 7;
  repeated string aliases = 8;
  string config_path = 9;
  google.protobuf.Timestamp created_at = 10;
}

message ProgressUpdate {
  int32 percent = 1;
  string message = 2;
  bool done = 3;
  string error = 4;  // non-empty if failed
}

message LogEntry {
  google.protobuf.Timestamp timestamp = 1;
  string service_id = 2;
  string level = 3;   // "info", "warn", "error"
  string message = 4;
}

message ServiceMetrics {
  string service_id = 1;
  float cpu_percent = 2;
  int64 memory_bytes = 3;
  int64 uptime_seconds = 4;
  google.protobuf.Timestamp timestamp = 5;
}
```

### Error Handling

Use standard gRPC status codes with application-specific error details:

| Scenario | gRPC Status | Detail |
|---|---|---|
| Domain already exists | `AlreadyExists` | domain name |
| Domain not found | `NotFound` | domain name |
| Service not running | `FailedPrecondition` | service id |
| PHP version not installed | `FailedPrecondition` | php version |
| Config validation failed | `InvalidArgument` | validation error output |
| Permission denied (hosts file) | `PermissionDenied` | "elevation required" |
| Port conflict | `ResourceExhausted` | port number + process info |

---

## 8. Configuration Pipeline

This is the architectural centerpiece of DevForge. It eliminates the config corruption problem that makes MAMP PRO unreliable.

### Pipeline Stages

```
Site model (from SQLite)
       ↓
  Scriban template
  (templates/apache-vhost.conf)
       ↓
  Rendered .conf text
       ↓
  Write to temp file (.conf.tmp)
       ↓
  Validate: httpd -t / nginx -t
       ↓
  ┌── PASS ──────────────────────────────────────────────┐
  │   Archive current → generated/history/{n}.conf        │
  │   Atomic rename: .conf.tmp → .conf                    │
  │   Signal: graceful reload (not full restart)          │
  └──────────────────────────────────────────────────────┘
  ┌── FAIL ────────────────────────────────┐
  │   Delete .conf.tmp                     │
  │   Return error to caller               │
  │   No change applied                    │
  └────────────────────────────────────────┘
```

### Atomic Write Implementation

```csharp
public async Task<Result> ApplyVhostConfigAsync(SiteConfig site, CancellationToken ct)
{
    var rendered = await _templateEngine.RenderAsync("apache-vhost.conf", site, ct);

    var tmpPath = site.ConfigPath + ".tmp";
    await File.WriteAllTextAsync(tmpPath, rendered, ct);

    var validationResult = await ValidateConfigAsync(site.Server, tmpPath, ct);
    if (!validationResult.IsValid)
    {
        File.Delete(tmpPath);
        return Result.Fail(validationResult.Error);
    }

    ArchiveCurrentConfig(site.ConfigPath);   // keep last 5 versions
    File.Move(tmpPath, site.ConfigPath, overwrite: true);  // atomic on same filesystem
    await ReloadServiceAsync(site.Server, ct);
    return Result.Ok();
}
```

### Apache Validation

```bash
httpd -t -f /path/to/generated/myapp.loc.conf
```

Exit code 0 = valid. Non-zero or stderr containing "Syntax Error" = invalid. Capture stderr as error message.

### Config Versioning

Keep last 5 versions of each config file:
```
generated/
  myapp.loc.conf              ← current
  history/
    myapp.loc.conf.1          ← previous
    myapp.loc.conf.2
    myapp.loc.conf.3
    myapp.loc.conf.4
    myapp.loc.conf.5
```

On rollback: copy `.conf.N` → `.conf`, validate, apply.

### Data Ownership (TOML vs SQLite)

**TOML files** are the source of truth for **site configuration** (what gets templated into Apache/Nginx configs):
- `~/.devforge/sites/{domain}.toml` — human-editable, diffable, git-friendly
- Daemon reads TOML → renders templates → generates server configs

**SQLite database** is the source of truth for **runtime state and relationships**:
- Service PIDs, health status, restart counts
- PHP version install paths, extension states
- Certificate IDs, expiry dates, fingerprints
- Config change audit trail

**Sync direction:** TOML → SQLite. On daemon start, all TOML files are scanned and SQLite `sites` table is reconciled. After a TOML write (via API), the corresponding SQLite row is updated in the same transaction. SQLite is NEVER written back to TOML — it is a derived/cached view.

**Conflict resolution:** If TOML and SQLite disagree, TOML wins. The daemon logs a warning and re-syncs SQLite from TOML.

### Site TOML Format (per-site source of truth)

```toml
[site]
hostname = "myapp.loc"
aliases = ["www.myapp.loc", "*.myapp.loc"]
document_root = "C:\\work\\sites\\myapp\\www"
framework = "nette"

[php]
version = "8.2"
extensions = ["xdebug", "intl", "gd"]

[php.ini_overrides]
memory_limit = "512M"
display_errors = "On"

[ssl]
enabled = true

[server]
type = "apache"
custom_directives = """
<Directory "${document_root}">
    AllowOverride All
    Options -Indexes
</Directory>
"""
```

Store TOML files at: `~/.devforge/sites/{domain}.toml`

### Config Rebuild

The `generated/` directory is always fully reconstructible. Running `devforge config:rebuild` deletes `generated/` and rebuilds everything from TOML files. This is run automatically after daemon upgrade.

### Scriban Template Example (Apache vhost)

```
# Generated by DevForge — DO NOT EDIT MANUALLY
# Source: {{ site.hostname }}.toml  Generated: {{ now }}

<VirtualHost *:80>
    ServerName {{ site.hostname }}
    {{ for alias in site.aliases }}
    ServerAlias {{ alias }}
    {{ end }}
    DocumentRoot "{{ site.document_root }}"
    {{ if site.php.version }}
    <FilesMatch "\.php$">
        SetHandler "proxy:unix:{{ php_fpm_socket }}|fcgi://localhost"
    </FilesMatch>
    {{ end }}
    {{ site.custom_directives }}
</VirtualHost>

{{ if site.ssl.enabled }}
<VirtualHost *:443>
    ServerName {{ site.hostname }}
    {{ for alias in site.aliases }}
    ServerAlias {{ alias }}
    {{ end }}
    DocumentRoot "{{ site.document_root }}"
    SSLEngine on
    SSLCertificateFile "{{ site.ssl.cert_path }}"
    SSLCertificateKeyFile "{{ site.ssl.key_path }}"
    {{ if site.php.version }}
    <FilesMatch "\.php$">
        SetHandler "proxy:unix:{{ php_fpm_socket }}|fcgi://localhost"
    </FilesMatch>
    {{ end }}
    {{ site.custom_directives }}
</VirtualHost>
{{ end }}
```

---

## 9. Service Management

### Bundled Services (MVP)

| Service | Binary | Default Port | Notes |
|---|---|---|---|
| Apache 2.4.x | httpd | 80, 443 | mod_proxy_fcgi, NOT mod_php |
| MySQL 8.0.x | mysqld | 3306 | Random root password on first init |
| PHP-FPM 8.2 | php-fpm | 9082 (Unix socket preferred) | Per-version pools |
| Redis 7.x | redis-server | 6379 | Optional, via plugin |
| Mailpit | mailpit | 1025 (SMTP), 8025 (UI) | Optional, via plugin |

### PHP-FPM Port Allocation

Each PHP version gets a dedicated port (fallback if Unix sockets unavailable):

| PHP Version | Port |
|---|---|
| 5.6.x | 9056 |
| 7.0.x | 9070 |
| 7.4.x | 9074 |
| 8.0.x | 9080 |
| 8.1.x | 9081 |
| 8.2.x | 9082 |
| 8.3.x | 9083 |
| 8.4.x | 9084 |

On macOS/Linux, use Unix sockets: `~/.devforge/run/php-fpm-8.2.sock`

### Apache Integration

Apache is proxied to PHP-FPM via `mod_proxy_fcgi`. Never use `mod_php`. This allows multiple PHP versions simultaneously, one per site.

Per-site generated config uses:
```apache
<FilesMatch "\.php$">
    SetHandler "proxy:unix:/path/to/php-fpm-8.2.sock|fcgi://localhost"
</FilesMatch>
```

Apache global config (`httpd.conf`) includes the `generated/` directory:
```apache
IncludeOptional /path/to/devforge/generated/*.conf
```

This keeps site configs independent — adding, removing, or changing one site cannot corrupt another.

### MySQL Init

On first launch (no data directory):
1. Run `mysqld --initialize-insecure --datadir=...`
2. Start MySQL
3. Generate random root password (32 chars, alphanumeric)
4. `ALTER USER 'root'@'localhost' IDENTIFIED BY '...'`
5. Store password in platform secrets manager (Windows DPAPI, macOS Keychain, Linux libsecret)
6. Display password once in GUI with "Copy to clipboard" button

### Port Conflict Detection

Before binding any port:
1. Check port availability
2. If in use: identify owner process name and PID
3. Report to caller with actionable options:
   - Stop conflicting process (if DevForge manages it)
   - Use fallback port (80→8080, 443→8443, 3306→3307)
   - Show error and let user resolve

---

## 10. PHP Version Manager

### Architecture

Each PHP version is self-contained under `~/.devforge/bin/php/X.Y.Z/`.

```
~/.devforge/bin/php/
├── 7.4.33/
│   ├── bin/php[.exe]
│   ├── bin/php-fpm[.exe]  (not on Windows — see note)
│   ├── etc/php.ini
│   └── ext/
├── 8.2.21/
│   ├── bin/php[.exe]
│   ├── bin/php-fpm[.exe]
│   ├── etc/php.ini
│   └── ext/
```

Note on Windows: PHP-FPM is not available as a native Windows binary. On Windows, use `php-cgi.exe` with a CGI wrapper or use Windows Subsystem for Linux PHP-FPM. Recommend Apache mod_fcgid on Windows with php-cgi.exe.

### Per-Site PHP Version

In SQLite: `sites.php_version_id → php_versions.id`

In TOML: `[php] version = "8.2"`

In Apache config: SetHandler points to the correct FPM socket for that version.

### Global Default

Only one PHP version has `is_default = 1`. Enforced by trigger: setting a new default clears the previous one. Sites without explicit `php_version_id` inherit the global default.

### CLI Aliases

DevForge manages a directory `~/.devforge/shims/` that is added to PATH. Create shim scripts for each installed version:

On macOS/Linux (shell script):
```bash
#!/bin/sh
exec ~/.devforge/bin/php/8.2.21/bin/php "$@"
```

On Windows (CMD wrapper `.cmd` file):
```cmd
@echo off
"C:\Users\Username\.devforge\bin\php\8.2.21\bin\php.exe" %*
```

Shim names: `php82`, `php83`, `php84`, `php74`, `php56`, etc.
Also manage `php` → points to default version.

### php.ini Management

Each PHP version has its own `php.ini` managed by DevForge. The ini file is generated from:
1. Base template (sensible development defaults)
2. Per-version ini overrides from SQLite `php_versions.ini_overrides`

Development defaults in generated php.ini:
```ini
memory_limit = 256M
display_errors = On
display_startup_errors = On
error_reporting = E_ALL
max_execution_time = 300
upload_max_filesize = 64M
post_max_size = 64M
date.timezone = UTC
```

### PHP-FPM Pool Config (Development Optimized)

```ini
[myapp_loc]
user = <current_user>
group = <current_group>
listen = /path/to/php-fpm-8.2.sock
pm = ondemand
pm.max_children = 3
pm.process_idle_timeout = 30s
pm.max_requests = 500
php_admin_value[error_log] = /path/to/devforge/log/php-fpm-myapp.log
php_admin_flag[log_errors] = on
```

`pm = ondemand` is critical — it prevents spawning idle worker processes for all 15–30 sites, saving 400–600 MB of RAM.

---

## 11. Virtual Host Manager

### Create Site Flow

```
CLI: devforge new myapp.loc --php=8.2 --db --ssl --nette
  or
GUI: New Site wizard

  ↓
1. Validate domain: RFC 952 regex, check for conflicts in SQLite
2. Detect framework if --nette/--laravel/--wordpress not specified
   (presence of composer.json + nette/application → Nette)
   (presence of artisan → Laravel)
   (presence of wp-config.php → WordPress)
3. Set document root based on framework:
   - Nette: {project_root}/www
   - Laravel: {project_root}/public
   - WordPress: {project_root}/
   - Generic: as specified
4. Write sites/{domain}.toml
5. Insert into SQLite (sites table)
6. Run Config Pipeline → generates and validates Apache/Nginx config
7. If --ssl: generate mkcert certificate
8. Add hosts file entry (127.0.0.1 domain + aliases)
9. If --db: create MySQL database
10. Graceful reload of Apache/Nginx
11. Return site summary
```

### Framework Auto-Detection

```csharp
public static Framework DetectFramework(string documentRoot)
{
    var projectRoot = Path.GetDirectoryName(documentRoot) ?? documentRoot;

    if (File.Exists(Path.Combine(projectRoot, "artisan")))
        return Framework.Laravel;

    if (File.Exists(Path.Combine(projectRoot, "wp-config.php")) ||
        File.Exists(Path.Combine(projectRoot, "wp-config-sample.php")))
        return Framework.WordPress;

    if (File.Exists(Path.Combine(projectRoot, "composer.json")))
    {
        var composer = File.ReadAllText(Path.Combine(projectRoot, "composer.json"));
        if (composer.Contains("nette/application"))
            return Framework.Nette;
        if (composer.Contains("symfony/symfony") || composer.Contains("symfony/framework-bundle"))
            return Framework.Symfony;
    }

    return Framework.Generic;
}
```

### Domain Validation

Strict regex at both application and database level:
```
^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)*$
```

Explicitly reject: spaces, newlines, null bytes, path traversal (`../`), semicolons, angle brackets.

### Config Versioning and Rollback

The `devforge site:rollback myapp.loc` command:
1. Lists available config history versions (from `generated/history/`)
2. Copies selected version to current
3. Validates it
4. Applies atomically
5. Records rollback in `config_history`

---

## 12. DNS / Hosts Manager

### Hosts File Format

DevForge writes a clearly-marked managed block:

```
# >>> DevForge Managed — DO NOT EDIT <<<
127.0.0.1    myapp.loc
127.0.0.1    www.myapp.loc
127.0.0.1    chatujme.loc
127.0.0.1    www.chatujme.loc
# <<< DevForge Managed >>>
```

Everything outside the markers is never touched.

### Hosts File Location

| Platform | Path |
|---|---|
| Windows | `C:\Windows\System32\drivers\etc\hosts` |
| macOS | `/etc/hosts` |
| Linux | `/etc/hosts` |

### Windows UAC Elevation

The daemon runs unprivileged. Hosts file writes require elevation on Windows. Use a minimal elevation helper:

1. `devforge-elevate.exe` — a separate minimal Windows executable
2. Registered as a scheduled task at install time with highest privileges and `RunOnlyIfLoggedOn = false`
3. The daemon calls the scheduled task with the operation payload (add/remove entries)
4. The helper validates the payload (only allows add/remove within the managed block)
5. Writes the hosts file
6. Returns exit code

Alternative: UAC prompt via `ProcessStartInfo { Verb = "runas" }` for one-time operations.

### Wildcard Entries

For wildcard `*.myapp.loc` to work with the hosts file (which does not support wildcards natively), add explicit entries for all used subdomains. For the `www` subdomain specifically, always add it automatically.

For true wildcard support on macOS: create `/etc/resolver/loc` with `nameserver 127.0.0.1` and run a local dnsmasq bound to a high port with `address=/.loc/127.0.0.1`.

### DNS Cache Flush

After hosts file changes:

| Platform | Command |
|---|---|
| Windows | `ipconfig /flushdns` |
| macOS | `sudo dscacheutil -flushcache && sudo killall -HUP mDNSResponder` |
| Linux | `sudo systemd-resolve --flush-caches` or `sudo nscd -i hosts` |

Run the appropriate command after every hosts file write.

---

## 13. SSL Certificate Manager

### mkcert Integration

mkcert is a third-party tool. Bundle the mkcert binary with DevForge at `~/.devforge/bin/mkcert[.exe]`.

**One-time CA installation** (requires elevation):

```bash
mkcert -install
```

This installs the local CA into:
- Windows: Trusted Root Certification Authorities (via certutil)
- macOS: Keychain
- Linux: NSS database + system CA bundle

Store CA files at `~/.devforge/ssl/ca/`:
- `rootCA.pem` — CA certificate (read-only, 444)
- `rootCA-key.pem` — CA private key (600, owner-only)

**Per-site certificate generation:**

```bash
mkcert \
  -cert-file ~/.devforge/ssl/sites/myapp.loc/cert.pem \
  -key-file ~/.devforge/ssl/sites/myapp.loc/key.pem \
  myapp.loc "*.myapp.loc" www.myapp.loc
```

### Certificate Tracking

Record in SQLite `certificates` table:
- domain, cert_path, key_path
- valid_from, valid_until
- is_wildcard, fingerprint
- auto_renew (for future auto-renewal)

### Certificate Expiry Handling

The HealthMonitor checks certificate validity daily. If a cert expires within 7 days:
- Show warning in GUI (yellow badge on SSL indicator)
- Send system notification
- Offer one-click regeneration

### OpenSSL Bundling

On Windows, MAMP PRO has a known bug: EC key generation fails because it bundles an old OpenSSL. DevForge bundles OpenSSL 3.x per platform and sets environment variables when invoking mkcert:

```
OPENSSL_CONF = ~/.devforge/ssl/openssl.cnf
OPENSSL_MODULES = ~/.devforge/bin/ossl-modules/
```

---

## 14. Database Manager

### MySQL Configuration (Development Optimized)

Generated `my.cnf`:
```ini
[mysqld]
innodb_buffer_pool_size = 128M
max_connections = 20
performance_schema = OFF
skip_log_bin = ON
innodb_flush_log_at_trx_commit = 2
```

### Database Operations

Via gRPC API:

- `CreateDatabase`: `CREATE DATABASE {name} CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci`
- `DropDatabase`: `DROP DATABASE IF EXISTS {name}`
- `CreateUser`: `CREATE USER '{user}'@'localhost' IDENTIFIED BY '{password}'`
- `GrantAccess`: `GRANT ALL PRIVILEGES ON {db}.* TO '{user}'@'localhost'`
- `ListDatabases`: `SELECT schema_name FROM information_schema.schemata WHERE schema_name NOT IN ('mysql','performance_schema','sys','information_schema')`
- Import: stream SQL file via gRPC, pipe to `mysql` binary
- Export: run `mysqldump` binary, stream output back

### Database Connection String

Construct from the stored root password (retrieved from platform secrets manager) at runtime. Never store the plaintext password in SQLite.

### phpMyAdmin / Adminer

DevForge does not bundle phpMyAdmin or Adminer. Instead, it provides:
- A button "Open phpMyAdmin" that opens the user's locally installed phpMyAdmin URL
- A button "Open Adminer" that downloads `adminer.php` to a temp site and opens it
- Connection pre-fill via URL parameters (hostname, username — NOT password)

### Backup Scheduling

Store backup schedule in `settings` table:
- `category = "backup"`, `key = "schedule"`, `value = "daily|weekly|disabled"`
- `category = "backup"`, `key = "retention_days"`, `value = "7"`
- `category = "backup"`, `key = "output_dir"`, `value = "/path/to/backups"`

Run backup as a background task in the daemon using `IHostedService` periodic timer.

---

## 15. CLI Interface

### Binary Name

`devforge[.exe]` — single binary for all CLI commands. Communicates with the daemon via gRPC.

If the daemon is not running when a CLI command is issued:
- For read commands (`status`, `site:list`): start daemon automatically, wait up to 5s, execute command
- For write commands: auto-start daemon

### Full Command Tree

```
devforge daemon start|stop|status|restart

devforge status                          # All services overview
devforge start [service]                 # Start all or named service
devforge stop [service]                  # Stop all or named service
devforge restart [service]               # Restart all or named service

devforge new <domain>                    # Create new site (wizard or flags)
  --php=8.2                              # PHP version
  --server=apache|nginx                  # Web server
  --docroot=PATH                         # Document root (auto-detected if omitted)
  --ssl                                  # Enable SSL
  --db                                   # Create database
  --db-name=NAME                         # Database name (default: domain slugified)
  --nette|--laravel|--wordpress          # Framework hint

devforge site:list [--json]
devforge site:info <domain> [--json]
devforge site:delete <domain> [--yes]
devforge site:open <domain>              # Open in default browser
devforge site:rollback <domain>          # Roll back config to previous version

devforge php:list [--json]
devforge php:install <version>           # e.g. devforge php:install 8.4
devforge php:remove <version>
devforge php:default <version>
devforge php:use <version> [--site=<domain>]

devforge db:list [--json]
devforge db:create <name> [--service=mysql]
devforge db:drop <name> [--yes]
devforge db:import <name> <file.sql>
devforge db:export <name> [output.sql]
devforge db:backup [--all]

devforge ssl:install-ca                  # Install mkcert CA (requires elevation)
devforge ssl:create <domain>             # Generate cert for domain
devforge ssl:list [--json]
devforge ssl:status                      # Show CA trust status

devforge dns:status                      # Show hosts file entries
devforge dns:flush                       # Flush DNS cache
devforge dns:add <domain> <ip>           # Manual entry
devforge dns:remove <domain>

devforge config:get <key>
devforge config:set <key> <value>
devforge config:list [--json]
devforge config:rebuild                  # Rebuild all generated configs from TOML

devforge plugin:list [--json]
devforge plugin:install <name>           # From marketplace or local path
devforge plugin:remove <name>
devforge plugin:enable <name>
devforge plugin:disable <name>
```

### Global Flags

| Flag | Description |
|---|---|
| `--json` | Machine-readable JSON output |
| `--no-color` | Disable ANSI colors |
| `--quiet`, `-q` | Suppress progress, errors only |
| `--verbose`, `-v` | Debug output including gRPC calls |
| `--help`, `-h` | Show help for command |

### JSON Output Mode

When `--json` is set, all output is a JSON object. Errors use:
```json
{"error": true, "code": "DOMAIN_NOT_FOUND", "message": "Domain myapp.loc not found"}
```

Success:
```json
{"success": true, "data": {...}}
```

### Shell Completions

`devforge completion bash|zsh|fish|powershell` — outputs completion script.

Generated via `System.CommandLine` built-in completion support. Installation instructions output automatically.

### Output Formatting

Use `Spectre.Console` for:
- Tables (site:list, php:list)
- Progress bars (php:install, db:import)
- Color status indicators (running=green, stopped=red, error=yellow)
- Panels (site:info detail view)

---

## 16. GUI Specification

### Technology

Avalonia UI 12.x with FluentTheme. MVVM pattern using `CommunityToolkit.Mvvm`. gRPC client communicates with daemon via the same transport as the CLI.

### Window Layout

```
┌─────────────────────────────────────────────────────────────────────┐
│  [Logo] DevForge          [service status dots]    [─][□][✕]        │
├──────────────┬──────────────────────────────────────────────────────┤
│              │                                                       │
│  SIDEBAR     │  CONTENT AREA                                        │
│  (200px)     │                                                       │
│              │                                                       │
│  Dashboard   │                                                       │
│  Sites       │                                                       │
│  PHP         │                                                       │
│  Database    │                                                       │
│  SSL         │                                                       │
│  Logs        │                                                       │
│  Settings    │                                                       │
│              │                                                       │
│  [+New Site] │                                                       │
│              │                                                       │
└──────────────┴──────────────────────────────────────────────────────┘
```

Sidebar collapses to icon-only (56px) via toggle button. Minimum window size: 900×600px.

### Screens

#### Dashboard

- Service status cards (one per service: Apache, MySQL, PHP-FPM)
  - Card shows: name, state (colored badge), CPU%, RAM, uptime
  - Buttons: Start / Stop / Restart
  - LiveCharts2 sparkline for CPU last 60 seconds
- Recent activity timeline (last 10 events from `config_history`)
- Quick actions: "New Site", "Open phpMyAdmin", "Start All", "Stop All"
- System health summary: total sites, active sites, certs expiring soon

#### Sites Manager

- Table view: domain, PHP version badge, SSL indicator, status, actions
- Click row → detail panel slides in from right:
  - Edit: PHP version dropdown, docroot, aliases
  - SSL toggle with cert status
  - Danger zone: delete, rollback config
  - Open in browser button
  - View logs button
- "New Site" button → opens Create Site Wizard

#### Create Site Wizard

Multi-step modal:
1. Domain name input + auto-validate
2. Document root picker + framework auto-detection result
3. PHP version selector (shows installed versions, default highlighted)
4. Options: SSL (mkcert), create database, web server choice
5. Review summary → Create button
6. Progress indicator during creation

#### PHP Version Manager

- List of installed PHP versions with default badge
- Each row: version, path, extensions count, active sites count
- Actions: Set as default, Edit extensions/ini, Remove
- "Install new version" → version picker with download progress bar

#### Database Manager

- List of MySQL databases with size, site association
- Actions: Create, Drop, Import, Export, Backup
- Import: file picker → streaming progress bar
- Export: save file dialog

#### SSL Manager

- CA status: installed/not installed with "Install CA" button
- List of site certificates: domain, expiry date, status badge
- Expiring soon highlighted in yellow/red
- "Generate Certificate" button per site
- Bulk actions: regenerate all expiring

#### Log Viewer

- Service selector dropdown (Apache, MySQL, PHP-FPM, etc.)
- Real-time log streaming via gRPC `StreamLogs`
- Filter by level (info/warn/error)
- Search/filter text box
- Auto-scroll with pause-on-select behavior
- Copy to clipboard button

#### Settings

- Port configuration (HTTP, HTTPS, MySQL, PHP-FPM base port)
- DNS settings (hosts file path, DNS cache flush button)
- Default PHP version
- Theme: Dark / Light / System
- Startup: run on system start, auto-start services
- Plugins: list with enable/disable toggles
- About: version, links

### System Tray

Avalonia `TrayIcon` (built-in). Always visible while daemon is running.

Tray icon:
- Green: all auto-start services running
- Yellow: some services running
- Red: daemon not running or critical error

Left-click: show/hide main window.
Right-click context menu:
```
DevForge 1.0.0
─────────────────
● Apache     Running  [Stop]
● MySQL      Running  [Stop]
● PHP-FPM    Running  [Stop]
─────────────────
Recent Sites:
  myapp.loc          →
  chatujme.loc       →
─────────────────
Start All Services
Stop All Services
─────────────────
Open Dashboard
─────────────────
Quit DevForge
```

### Dark Mode Theme (Default)

| Token | Value | Usage |
|---|---|---|
| Background | `#0F1117` | App background |
| Surface | `#1A1D27` | Cards, panels, sidebar |
| Elevated | `#242736` | Hover states |
| Text Primary | `#E8EAF0` | Primary text |
| Text Secondary | `#8B90A7` | Metadata, labels |
| Accent Blue | `#4F87FF` | Primary action buttons |
| Success | `#22C55E` | Running, valid |
| Error | `#EF4444` | Stopped, error |
| Warning | `#F59E0B` | Starting, expiring |
| Info | `#38BDF8` | Info badges |

Light mode: FluentTheme built-in light palette with accent `#4F87FF`.

### Keyboard Shortcuts

| Shortcut | Action |
|---|---|
| `Ctrl+K` | Command palette |
| `Ctrl+N` | New site |
| `Ctrl+1` through `Ctrl+7` | Switch sidebar sections |
| `F5` | Refresh status |
| `Space` | Toggle selected service (start/stop) |
| `Esc` | Close modal / detail panel |

### ViewModels (CommunityToolkit.Mvvm)

Each screen has a dedicated ViewModel with `[ObservableProperty]` for state and `[RelayCommand]` for actions. All gRPC calls are async. Loading states shown via `IsBusy` properties.

```csharp
[ObservableProperty]
private ObservableCollection<SiteViewModel> _sites = new();

[RelayCommand]
private async Task CreateSiteAsync(CreateSiteModel model)
{
    IsBusy = true;
    var result = await _grpcClient.CreateSiteAsync(model.ToRequest());
    if (result.Success)
        Sites.Add(new SiteViewModel(result.Site));
    IsBusy = false;
}
```

---

## 17. Plugin System

### IServiceModule Interface (for service plugins)

```csharp
namespace DevForge.Core.Interfaces;

public interface IServiceModule
{
    string ServiceId { get; }
    string DisplayName { get; }
    ServiceType Type { get; }
    string DefaultConfigTemplate { get; }

    Task<ValidationResult> ValidateConfigAsync(CancellationToken ct);
    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
    Task ReloadAsync(CancellationToken ct);
    Task<ServiceStatus> GetStatusAsync(CancellationToken ct);
    Task<IReadOnlyList<string>> GetLogsAsync(int lines, CancellationToken ct);

    // Optional: GUI extension
    Type? DashboardPanelType => null;
    IReadOnlyList<CliCommandDefinition> CliCommands => Array.Empty<CliCommandDefinition>();
}
```

### Plugin Manifest (`plugin.json`)

```json
{
  "id": "devforge-redis",
  "name": "Redis",
  "version": "1.2.0",
  "type": "service",
  "minDevForgeVersion": "1.0.0",
  "entryAssembly": "DevForge.Plugin.Redis.dll",
  "entryType": "DevForge.Plugin.Redis.RedisModule",
  "permissions": {
    "network": true,
    "filesystem": ["${DEVFORGE_HOME}/plugins/redis/"],
    "process": true,
    "gui": false
  }
}
```

### Plugin Loading (AssemblyLoadContext)

```csharp
public class PluginLoader
{
    private readonly Dictionary<string, PluginLoadContext> _contexts = new();

    public IServiceModule LoadPlugin(string pluginDirectory)
    {
        var manifest = LoadManifest(pluginDirectory);
        var context = new PluginLoadContext(pluginDirectory);
        _contexts[manifest.Id] = context;

        var assembly = context.LoadFromAssemblyPath(
            Path.Combine(pluginDirectory, manifest.EntryAssembly));

        var type = assembly.GetType(manifest.EntryType)
            ?? throw new InvalidOperationException($"Type {manifest.EntryType} not found");

        return (IServiceModule)Activator.CreateInstance(type)!;
    }

    public void UnloadPlugin(string pluginId)
    {
        if (_contexts.TryGetValue(pluginId, out var context))
        {
            context.Unload();
            _contexts.Remove(pluginId);
        }
    }
}
```

### Plugin Directory

`~/.devforge/plugins/{plugin-id}/`

On startup: scan this directory and attempt to load all enabled plugins from SQLite.

### Built-in Plugins (Ship with DevForge)

- `devforge-apache` — Apache 2.4 module (builtin = true)
- `devforge-nginx` — Nginx module (builtin = true)
- `devforge-mysql` — MySQL 8.x module (builtin = true)
- `devforge-php-fpm` — PHP-FPM multi-version module (builtin = true)

### Optional Plugins (V1 or V2)

- `devforge-redis` — Redis 7.x
- `devforge-mariadb` — MariaDB 11.x (alternative to MySQL)
- `devforge-mailpit` — Mailpit email testing
- `devforge-nette` — Nette framework driver (auto-detection, site creation defaults)
- `devforge-laravel` — Laravel framework driver
- `devforge-wordpress` — WordPress framework driver

---

## 18. Security Model

### Privilege Architecture

The daemon runs as the **current user** (unprivileged). The only operations requiring elevation:
1. Writing to the hosts file (Windows requires admin; macOS/Linux require sudo)
2. Binding to ports < 1024 (if the user wants to use port 80/443 directly)
3. Installing the mkcert CA certificate

For each of these, DevForge uses a minimal elevation helper that is installed once at setup time.

### Windows Elevation Helper

`devforge-elevate.exe` — separate minimal executable registered as a Windows Scheduled Task with `RunLevel = Highest`. The daemon calls it via:

```csharp
var task = TaskScheduler.FindTask("DevForge Elevation Helper");
task.Run(JsonSerializer.Serialize(new ElevationRequest { Operation = "write-hosts", Payload = ... }));
```

The helper validates:
- Operation is in the allowed list (`write-hosts`, `flush-dns`, `install-ca`)
- Payload matches expected schema
- Target paths are the system hosts file (not arbitrary paths)
- Hosts file entries only add/modify the managed block markers

### Domain Validation (Injection Prevention)

Reject any domain containing:
- Whitespace (space, tab, newline, carriage return)
- Null bytes (`\0`)
- Path traversal sequences (`../`, `..\`)
- Shell metacharacters (`;`, `|`, `&`, `$`, `` ` ``, `>`, `<`)
- Angle brackets
- Quotes (single or double)

This prevents config injection attacks through the domain field, which is a known vector in tools that embed domain names directly into Apache config.

### MySQL Root Password

Random 32-character alphanumeric password generated at first init. Stored in:
- Windows: DPAPI + `CredentialManager`
- macOS: Keychain via `SecItemAdd`
- Linux: libsecret (GNOME Keyring) or `~/.devforge/secrets/` with `chmod 600` as fallback

Never stored in SQLite or TOML files.

### Network Binding

All services bind to `127.0.0.1` by default. Binding to `0.0.0.0` requires explicit user action and shows a warning dialog in the GUI.

### Config File Permissions

| File | Permissions |
|---|---|
| `~/.devforge/ssl/ca/rootCA-key.pem` | `600` (owner read/write only) |
| `~/.devforge/secrets/` (Linux fallback) | `700` directory, `600` files |
| All other config files | `644` |

---

## 19. Performance Targets

| Metric | Target |
|---|---|
| Cold start, all services | < 3 seconds |
| Site creation (no SSL) | < 1 second |
| Site creation (with SSL) | < 2 seconds |
| PHP version switch | < 2 seconds |
| Config reload (Apache graceful) | < 500ms |
| GUI startup to responsive | < 2 seconds |
| gRPC request round-trip | < 50ms (local) |
| Memory: daemon only | < 50 MB |
| Memory: daemon + Apache + MySQL + 5 PHP-FPM pools | < 300 MB |
| Memory: daemon + all services + 15 sites | < 500 MB |

### Key Optimizations Required

1. `pm = ondemand` for all PHP-FPM pools — saves 400–600 MB for 15+ sites
2. `performance_schema = OFF` in MySQL — saves 100–200 MB
3. Parallel service startup (MySQL and PHP-FPM start simultaneously)
4. Pre-generate all Apache/Nginx configs at site creation, not at startup
5. Keep last 5 config versions in memory for instant rollback

---

## 20. Testing Strategy

### Coverage Targets

| Project | Line Coverage | Branch Coverage |
|---|---|---|
| DevForge.Core | 90% | 90% |
| DevForge.Daemon | 80% | 80% |
| DevForge.Cli | 85% | 85% |
| DevForge.Gui | 70% | 70% |

### Test Projects

**DevForge.Tests/Core.Tests:**
- Config template rendering: all templates produce valid output for all framework types
- Domain validation: valid domains pass, injection attempts are rejected
- TOML parsing: read/write round-trip fidelity
- Database migration: 001_initial.sql applies cleanly on empty DB (reuse prototype/database/schema_test.py logic in C#)

**DevForge.Tests/Daemon.Tests:**
- ProcessManager: start/stop/crash/restart state machine transitions
- ConfigPipeline: `httpd -t` validation integration test (requires Apache binary)
- AtomicWriter: simulated filesystem failures
- HostsFileManager: managed block add/remove/update logic
- SslManager: mkcert invocation and certificate tracking
- Database migration runner: version sequencing, checksum validation

**DevForge.Tests/Cli.Tests:**
- All commands parse correctly from argument strings
- `--json` flag produces valid JSON for all commands
- Commands fail gracefully when daemon is not running
- Shell completion output is valid for bash/zsh/PowerShell

**DevForge.Tests/Gui.Tests:**
- Avalonia Headless: all ViewModels initialize without exceptions
- Create Site Wizard: step validation logic
- ServiceCard: renders in all ServiceState values
- Theme switching: no layout exceptions

### Critical Integration Tests (require real binaries)

Run in CI on all three platforms (Windows, macOS, Ubuntu):

1. **Config injection guard** — attempt to create site with domain containing newline → must be rejected at API level
2. **Full site creation** — `CreateSite` → verify Apache config exists, passes `httpd -t`, hosts entry added
3. **SSL full flow** — create site with SSL → verify cert exists, has correct CN, is trusted by system store
4. **PHP version switch** — assign PHP 8.2 to site → assign PHP 8.4 → verify FPM socket path updated in config
5. **Startup benchmark** — measure cold start time across 5 runs, assert < 3000ms mean
6. **Crash recovery** — kill Apache process → verify daemon restarts it within 5 seconds

### CI Matrix (GitHub Actions)

```yaml
strategy:
  matrix:
    os: [windows-2022, macos-14, ubuntu-24.04]
    dotnet: ['9.0']
```

Run on all PRs and main branch merges.

---

## 21. Deployment and Packaging

### Publish Configuration

```xml
<PropertyGroup>
  <RuntimeIdentifier>win-x64</RuntimeIdentifier>
  <SelfContained>true</SelfContained>
  <!-- DO NOT enable PublishTrimmed — triggers Defender heuristics -->
  <PublishReadyToRun>true</PublishReadyToRun>
  <PublishSingleFile>false</PublishSingleFile>
</PropertyGroup>
```

For macOS: `osx-x64` and `osx-arm64`, produce universal binary with `lipo`.
For Linux: `linux-x64`.

### Portable Directory Structure (on disk after install/extract)

```
~/.devforge/ (or %APPDATA%\DevForge\ on Windows)
├── bin/
│   ├── php/
│   │   ├── 8.2.21/
│   │   └── 8.4.1/
│   ├── apache/2.4.x/
│   ├── mysql/8.0.x/
│   ├── mkcert[.exe]
│   └── openssl/3.x/
├── sites/              ← TOML per site
│   ├── myapp.loc.toml
│   └── chatujme.loc.toml
├── generated/          ← Ephemeral, reconstructible
│   ├── myapp.loc.conf
│   └── history/
├── data/
│   └── state.db
├── ssl/
│   ├── ca/
│   └── sites/
├── log/
├── plugins/
└── templates/          ← Scriban templates for Apache/Nginx configs
```

The DevForge executables themselves are installed to:
- Windows: `C:\Program Files\DevForge\`
- macOS: `/Applications/DevForge.app/`
- Linux: `/opt/devforge/`

### Installers

| Platform | Format | Notes |
|---|---|---|
| Windows | WiX MSI (signed) | MSI raises fewer AV false positives than NSIS |
| Windows | Portable .zip | Extract and run, no install needed |
| macOS | DMG with .app | Code signed with Apple Developer ID |
| Linux | AppImage | Self-contained, no root needed |
| Linux | .deb and .rpm | For distro package managers |

### Auto-Updater

Use `Velopack` (formerly Squirrel.Windows). It handles:
- Delta updates
- Background download
- Silent install and restart
- Update channels: `stable`, `beta`

Update manifest published to CDN. Check interval: 24 hours.

### Windows Antivirus Mitigation

Steps taken at every release:
1. Build with `--self-contained true` and `PublishReadyToRun = true` (NOT `PublishSingleFile`)
2. Do NOT strip symbols (`DebugType=None` in .NET) — this triggers Defender heuristics
3. Sign with OV code signing certificate (EV when budget allows)
4. Submit to Microsoft Defender submission portal: https://www.microsoft.com/wdsi/filesubmission
5. Pre-publish VirusTotal scan in CI

---

## 22. Implementation Phases

### Phase 0: Day-1 Verification (BEFORE any implementation)

**Must verify before writing any code:**
- [ ] `dotnet new install Avalonia.Templates` — install Avalonia project templates
- [ ] Create minimal Avalonia 12 project: verify `FluentTheme` dark mode works
- [ ] Add `LiveChartsCore.SkiaSharpView.Avalonia 2.0.0` — verify it renders a chart in Avalonia 12 window
- [ ] If LiveCharts2 fails on Avalonia 12: test `ScottPlot.Avalonia` as fallback (OxyPlot is also broken)
- [ ] Verify `Grpc.AspNetCore` 2.76.0 works with Kestrel named pipe on Windows
- [ ] Verify `HotAvalonia` works with Avalonia 12 for XAML hot reload
- [ ] Run `dotnet publish --self-contained -r win-x64` and scan result with Windows Defender (confirm no false positive)

**If any verification fails:** update SPEC.md with the workaround before proceeding to Phase 1.

### Phase 1: Foundation (Week 1–2)

Deliverables:
- [ ] .NET 9 solution with 5 projects: Core, Daemon, Gui, Cli, Tests
- [ ] Copy prototype database schema (`prototype/database/`) and create C# migration runner
- [ ] gRPC service definition (`devforge.proto`) with all methods defined
- [ ] Basic daemon: Worker Service host, gRPC server on named pipe, SQLite open
- [ ] Basic Avalonia window: FluentTheme dark, sidebar layout, empty screens
- [ ] Basic CLI: `devforge status` command, gRPC client connecting to daemon

Acceptance criteria:
- `dotnet build` succeeds on all three platforms
- Daemon starts, writes PID file, opens SQLite, exposes gRPC
- CLI connects to daemon and returns status
- Avalonia window opens with sidebar navigation

### Phase 2: Core Services (Week 3–4)

Deliverables:
- [ ] Apache module: template rendering (Scriban), `httpd -t` validation, start/stop/reload
- [ ] MySQL module: init data directory, start/stop, connection test
- [ ] PHP-FPM module: multi-version, per-site pool config generation
- [ ] ProcessManager: full state machine, restart policy, Windows Job Objects
- [ ] HealthMonitor: periodic checks, state transitions

Acceptance criteria:
- Can start Apache, MySQL, and PHP-FPM via gRPC `StartService`
- `httpd -t` validation blocks invalid configs from being applied
- Service crash is detected and auto-restart fires within 5 seconds

### Phase 3: Sites + DNS + SSL (Week 5–6)

Deliverables:
- [ ] Virtual host CRUD via gRPC (create, list, delete, update)
- [ ] Config pipeline: TOML → Scriban → validate → atomic write → version history
- [ ] Hosts file manager: managed block, add/remove entries, UAC helper on Windows
- [ ] mkcert integration: CA install, per-site cert generation
- [ ] Framework auto-detection: Nette, Laravel, WordPress, generic
- [ ] CLI: all `site:*`, `ssl:*`, `dns:*` commands

Acceptance criteria:
- `devforge new myapp.loc --php=8.2 --ssl --nette` creates a working site end-to-end
- Site accessible at https://myapp.loc in browser without certificate warning
- Hosts file has correct entry, DNS cache flushed

### Phase 4: GUI + Logging (Week 7–8)

Deliverables:
- [ ] Dashboard screen with LiveCharts2 service status and metrics
- [ ] Sites Manager screen with table, detail panel, and create wizard
- [ ] PHP Manager screen
- [ ] SSL Manager screen
- [ ] Log viewer with gRPC streaming (`StreamLogs`)
- [ ] System tray with context menu and service status indicators
- [ ] Real-time metrics streaming (`StreamMetrics`)

Acceptance criteria:
- All screens render without exceptions
- Create Site Wizard creates a working site with real-time progress
- Log viewer shows live Apache access logs
- System tray reflects service states correctly

### Phase 5: CLI Completeness + Polish (Week 9–10)

Deliverables:
- [ ] All CLI commands implemented and tested
- [ ] Shell completions: bash, zsh, fish, PowerShell
- [ ] `--json` output for all commands
- [ ] Database manager: create, drop, import, export, backup scheduling
- [ ] Settings screen in GUI
- [ ] Dark/light theme toggle
- [ ] Keyboard shortcuts

Acceptance criteria:
- All CLI commands pass unit and integration tests
- `devforge db:import mydb dump.sql` completes with progress bar
- Shell completions work in bash and PowerShell

### Phase 6: Plugins + Packaging (Week 11–12)

Deliverables:
- [ ] Plugin system: AssemblyLoadContext loader, IServiceModule interface, manifest parsing
- [ ] Built-in plugins: Apache, Nginx, MySQL, PHP-FPM as proper IServiceModule implementations
- [ ] Optional plugin: Redis
- [ ] Optional plugin: Mailpit
- [ ] Installer: WiX MSI (Windows), DMG (macOS), AppImage (Linux)
- [ ] Auto-updater (Velopack)
- [ ] MAMP PRO migration import tool (reads MAMP's SQLite, creates DevForge sites)

Acceptance criteria:
- Redis plugin loads, starts Redis, shows in Dashboard
- MAMP PRO import tool creates correct site configs from MAMP database
- WiX MSI installs cleanly on Windows 10/11

---

## 23. File Layout on Disk

### Source Tree Reference Files

The `prototype/` directory contains working Go prototype code. Use it as implementation reference only — the final implementation is C#. Key reference materials:

| Prototype File | Reference For |
|---|---|
| `prototype/database/migrations/001_initial.sql` | Canonical SQLite DDL — use this schema verbatim |
| `prototype/database/triggers.sql` | All triggers — port to C# as seeded SQL |
| `prototype/database/views.sql` | All views — port to C# as seeded SQL |
| `prototype/database/indexes.sql` | All indexes |
| `prototype/database/seed.sql` | Default data for first run |
| `prototype/ssl/ssl_manager.go` | mkcert invocation patterns |
| `prototype/dns/hosts_manager.go` | Hosts file managed block logic |
| `prototype/daemon/internal/` | Service lifecycle patterns (translate from Go to C#) |
| `prototype/cli/` | CLI command structure reference |

### Important: Do Not Port Prototype Code

The Go prototype (`prototype/daemon/`, `prototype/ssl/`, `prototype/dns/`) exists for **architectural reference only**. Do not port Go code to C# — translate the patterns and algorithms, not the syntax.

The GUI prototype (`prototype/gui/`) uses **Tauri + Svelte** — this is **superseded by Avalonia**. Do not reference it for implementation.

**Exception:** Use the SQLite schema verbatim — `prototype/database/migrations/001_initial.sql` is already correct and tested (49/49 tests pass). Copy and adapt for dbup-sqlite.

### Documentation

Existing documentation that is useful context but should not drive implementation decisions differently from this SPEC:

- `docs/plans/2026-04-09-devforge-implementation-plan.md` — Earlier plan compiled from 15 agents; some decisions were later revised (e.g., Go → C#, Tauri → Avalonia). Use as supplemental reference.
- `docs/plans/interview-results.md` — Original user requirements gathering.

**This SPEC.md is the definitive document.** When this SPEC conflicts with any other document, follow this SPEC.

---

*End of specification. Total tables: 9. Total gRPC methods: 30. Total CLI commands: 35+.*
*Technology decisions are final. Begin with Phase 1.*
