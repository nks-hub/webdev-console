# FlyEnv Deep Analysis — Inspirace pro NKS WebDev Console

**Date:** 2026-04-09
**Source:** https://github.com/xpf0000/FlyEnv (BSD-3-Clause, 2.7k stars)
**Stack:** Electron 33 + Vue 3 + TypeScript + Element Plus

---

## 1. FlyEnv Architecture Summary

### Tech Stack
- Frontend: Vue 3 + Element Plus (component library)
- Backend: Electron main process + utility process (fork workers)
- Build: esbuild + Vite
- Languages: TypeScript 51%, Vue 41%, Go 1.4% (helper binary)

### Module Registry
- NO manifest file — filesystem IS the registry
- `import.meta.glob('@/components/*/Module.ts')` auto-discovers modules
- Each module exports `AppModuleItem` with `typeFlag`, `moduleType`, `isService`
- Complete enum: 48+ modules (caddy, nginx, php, mysql, mariadb, apache, memcached, redis, mongodb, postgresql, tomcat, java, node, dns, hosts, golang, rabbitmq, python, mailpit, erlang, ruby, elasticsearch, ollama, minio, rust, meilisearch, deno, bun, perl, consul, qdrant, cloudflare-tunnel, n8n, etc.)

### Version API
- Proprietary CDN: `POST https://api.one-env.com/api/version/fetch`
- Body: `{ app: "mysql", os: "win", arch: "x86" }`
- Response: `[{ url, version, mVersion }]`
- Pre-built binaries hosted by FlyEnv, NOT official vendor sources

### Directory Structure
```
FlyEnv-Data/
  app/           <- downloaded + extracted binaries (mysql-8.0.33/, php-8.2.0/)
  server/        <- runtime data (mysql/my-8.0.cnf, data-8.0/, redis/redis-7.conf)
  cache/         <- download staging (.zip files)
  pid/           <- PID files per service
```

### Process Spawning (Windows)
- PowerShell wrapper: `static/sh/Windows/flyenv-async-exec.ps1`
- Substitutes `#BIN#`, `#ARGS#`, `#CWD#`, `#OUTLOG#`, `#ERRLOG#`
- Uses `Start-Process -PassThru` to get PID
- PID communicated back via `##FlyEnv-Process-ID<pid>FlyEnv-Process-ID##`

### Health Checking
- PID file polling only (20 × 500ms = 10s timeout)
- NO port probing, NO HTTP health check
- NO process supervision, NO auto-restart

### Config Templates
- `static/tmpl/Windows/` — redis.conf, my.cnf, nginx.vhost
- Simple `string.replace("#PLACEHOLDER#", value)` — no template engine
- MySQL config is INLINE in source code (not a file template)

### IPC Architecture
- Electron IPC (ipcRenderer.invoke → main process → ForkManager → utility process)
- ForkManager: worker pool, auto-destroy after 10s idle
- BaseManager: lazy-loads modules via dynamic import
- Message format: `[ipcKey, moduleId, functionName, ...args]`

### Port Management
- NONE — ports hardcoded in config templates
- No conflict detection
- User edits config file manually to change ports

### Custom Modules
- Stored as JSON in user settings (not filesystem discovery)
- `ModuleExecItem`: { id, name, command, commandType, pidPath, env }
- No sandboxing, full system access

---

## 2. What FlyEnv Does WELL (to adapt)

### 2.1 Per-Module Version Switcher
Dropdown showing installed versions → one-click switch → service restarts.
**NKS WebDev Console:** Same pattern + config validation step between switch and restart.

### 2.2 Service Status Dashboard
Cards with colored dots (green/red), version, port, start/stop toggle.
All visible without scrolling for 5-8 services.
**NKS WebDev Console:** Keep + add CPU/RAM inline metrics per card.

### 2.3 Log Viewer Per Service
Tails log file in real-time with ANSI colors. Search/filter box. Auto-scroll toggle.
**NKS WebDev Console:** Implement with gRPC StreamLogs + log level filtering.

### 2.4 One-Click SSL
Single toggle: SSL on/off. Auto-generates cert, configures web server.
**NKS WebDev Console:** Copy exactly. Add "Details" expandable for power users.

### 2.5 Module Download Manager
Browse available → click Install → progress bar → ready.
Initial install stays small, download only what needed.
**NKS WebDev Console:** Core services bundled, optional modules downloaded on demand.

### 2.6 Visual Version Grid
All installed vs available versions displayed in a grid UI.
Most demo-ed feature in FlyEnv marketing.
**NKS WebDev Console:** Must match this polish — critical for first impressions.

---

## 3. What FlyEnv Does POORLY (to fix in NKS WebDev Console)

### 3.1 No Config Validation
Writes Apache/Nginx configs without httpd -t/nginx -t.
Syntax errors crash web server with no recovery.
**NKS WebDev Console killer feature:** 3-stage pipeline (parse → render → dry-run → atomic write).

### 3.2 Electron Memory Overhead
250-400MB idle RAM. Developers notice in Task Manager.
**NKS WebDev Console:** Avalonia target 40-80MB (3-5x improvement).

### 3.3 No CLI Interface
Everything GUI-only. No scripting, no CI/CD integration.
**NKS WebDev Console:** Full gRPC CLI with --json output.

### 3.4 Shallow Module Integration
Each module is an isolated island. No inter-service awareness.
Creating PHP site doesn't auto-configure database.
**NKS WebDev Console:** Deep integration — site creation auto-creates DB if requested.

### 3.5 No Process Supervision
Crashed service stays "green" in UI. User discovers via "connection refused".
**NKS WebDev Console:** Auto-restart with exponential backoff + state polling.

### 3.6 PID-Only Health Check
No port probing, no HTTP health check.
**NKS WebDev Console:** TCP port + HTTP health + mysqladmin ping + process alive.

### 3.7 No Port Conflict Detection
Ports hardcoded. Service fails to start if port taken.
**NKS WebDev Console:** Automatic detection + suggestion of alternative ports.

---

## 4. Gap Analysis: 52 FlyEnv Modules vs NKS WebDev Console

### v1.0 Must-Have (19 modules):
Apache, Nginx, MySQL, MariaDB, PHP (multi-version), Node.js, Python, Redis,
Memcached, Mailpit, DNS/hosts, SSL (mkcert), Log viewer, Port viewer,
Config editor, Version switcher, PHP extensions, Composer, Xdebug

### v1.1 Should-Have (+12 = 31 total):
Caddy, PostgreSQL, MongoDB, Go, Ollama, n8n, Cloudflare Tunnel,
Meilisearch, MinIO, Bun, Deno, RabbitMQ

### v2.0 Could-Have (+5 = 36 total):
Tomcat, Elasticsearch, Typesense, Ruby, Podman

### Won't Have (16):
Erlang, Zig, Perl, Rust runtime, Consul, etcd, FTP, RustFS, OpenClaw,
Chatbox, PHP Obfuscator, Static HTTP server, Maven, Gradle, Qdrant (maybe v1.2)

---

## 5. C#/.NET Equivalent Architecture

### IServiceModule (extended from FlyEnv's Base class):
```csharp
public interface IServiceModule
{
    string TypeFlag { get; }
    string ModuleType { get; }
    bool IsService { get; }

    Task<List<InstalledVersion>> DetectLocalVersionsAsync();
    Task<List<OnlineVersion>> FetchOnlineVersionsAsync();
    Task DownloadVersionAsync(OnlineVersion version, IProgress<int> progress, CancellationToken ct);
    Task<ServiceStartResult> StartAsync(InstalledVersion version, CancellationToken ct);
    Task StopAsync(InstalledVersion version);
    Task<string> GenerateConfigAsync(InstalledVersion version);
    Task<bool> HealthCheckAsync(CancellationToken ct);
}
```

### ModuleRegistry (replaces import.meta.glob):
```csharp
public class ModuleRegistry
{
    public void DiscoverModules(IEnumerable<Assembly> assemblies)
    {
        foreach (var type in assemblies.SelectMany(a => a.GetTypes())
            .Where(t => typeof(IServiceModule).IsAssignableFrom(t) && !t.IsAbstract))
        {
            var instance = (IServiceModule)Activator.CreateInstance(type)!;
            _modules[instance.TypeFlag] = instance;
        }
    }
}
```

### Binary Layout (mirrors FlyEnv):
```
%APPDATA%\NKS WebDev Console\
  app\        <- downloaded binaries (mysql-8.0.33\, php-8.2.0\)
  server\     <- runtime data (mysql\my-8.0.cnf, data-8.0\)
  cache\      <- download staging
  sites\      <- per-site TOML configs
  ssl\        <- certificates
  data\       <- state.db (SQLite)
  log\        <- logs
  plugins\    <- plugin DLLs
```

### Version API (NKS WebDev Console equivalent):
```
POST https://api.wdc.nks-hub.cz/api/version/fetch
{ "app": "mysql", "os": "win", "arch": "x64" }
→ [{ "version": "8.0.33", "url": "https://cdn.wdc.nks-hub.cz/mysql-8.0.33-win.zip" }]
```

---

## 6. Key UX Insight

> **Config validation must be VISIBLE in the UI.**
> When user changes site config or switches PHP version, show a brief
> "Validating... Passed ✓" step with green checkmark before applying.
> This transforms NKS WebDev Console's invisible backend advantage into a visible
> UX differentiator. FlyEnv cannot match this without fundamental architecture change.

---

*Analysis compiled from 4 parallel agents: Code Explorer, UI/UX Designer, Backend Architect, Data Analyst*
