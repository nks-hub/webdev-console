using System.Text.Json;
using System.Net;
using System.Net.NetworkInformation;
using Dapper;
using NKS.WebDevConsole.Daemon.Plugin;
using NKS.WebDevConsole.Daemon.Services;
using NKS.WebDevConsole.Daemon.Sites;
using NKS.WebDevConsole.Daemon.Binaries;
using NKS.WebDevConsole.Daemon.Backup;
using NKS.WebDevConsole.Daemon.Config;
using NKS.WebDevConsole.Daemon.Data;
using NKS.WebDevConsole.Daemon.Mcp;
using NKS.WebDevConsole.Daemon.Cloudflare;
using NKS.WebDevConsole.Daemon.Ssl;
using NKS.WebDevConsole.Daemon.Hosts;
using NKS.WebDevConsole.Daemon.Deploy;
using CliWrap.Buffered;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Core.Services;

// Windows: create the Job Object before anything spawns child processes so every
// subsequent Process.Start() from ProcessManager + plugins gets assigned to it and
// gets killed when the daemon exits (no orphaned httpd/mysqld/php-cgi processes).
NKS.WebDevConsole.Core.Services.DaemonJobObject.EnsureInitialized();

// F90: capture daemon process start timestamp ONCE so /api/status and
// /api/system can report uptime since daemon boot — previously both
// endpoints used Environment.TickCount64 which counts since SYSTEM boot
// and reported e.g. 765664s (~212h) on a machine that had been up for
// days even though the daemon had just started seconds ago.
var daemonStartedUtc = DateTime.UtcNow;
long DaemonUptimeSeconds() => (long)(DateTime.UtcNow - daemonStartedUtc).TotalSeconds;

// Shared JsonSerializerOptions for body deserialization — JsonSerializer
// caches type contracts per options instance, so allocating fresh per
// request fragments that cache. Case-insensitive matches ASP.NET's default
// Minimal API binding behaviour, so the existing POST/PUT payload shapes
// (camelCase from the Vue frontend) continue to deserialize to the C#
// PascalCase DTOs without any wire change.
var caseInsensitiveJson = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

// F92: daemon version read from assembly (InformationalVersion attribute
// wired up via <InformationalVersion> in the csproj). Previously two
// /api/* endpoints hardcoded "0.1.0" and never tracked shipped version.
var daemonVersion =
    System.Reflection.CustomAttributeExtensions
        .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(
            System.Reflection.Assembly.GetExecutingAssembly())?.InformationalVersion
    ?? System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString()
    ?? "unknown";

var builder = WebApplication.CreateBuilder(args);

// CORS for the Electron renderer: the packaged app serves the SPA from
// app://nks-wdc and the dev server runs on a loopback http origin. Kestrel
// binds loopback-only, but a browser on this machine can still reach it,
// so only those origins may read responses; any web page gets no CORS
// grant. Auth is a bearer header (no cookies), so credentials stay off.
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy
            .SetIsOriginAllowed(DaemonCors.IsAllowedOrigin)
            .AllowAnyMethod()
            .AllowAnyHeader());
});

// DNS-rebinding defence: answer only Host localhost / 127.0.0.1 / [::1].
builder.Services.AddDaemonHostFiltering();

// OpenAPI metadata so /openapi/v1.json can be consumed by NSwag/swagger-typescript-api
// to generate TS types in CI (prevents contract drift between daemon and frontend).
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();

builder.Services.AddSingleton<TelemetryConsent>();
builder.Services.AddSingleton<PluginState>();
builder.Services.AddSingleton<PhpExtensionOverrides>();
builder.Services.AddSingleton<SettingsStore>();
// Also expose SettingsStore through the cross-ALC IWdcSettings interface
// (defined in Core) so plugins can read ports/paths/flags without taking
// a direct reference on the daemon assembly.
builder.Services.AddSingleton<NKS.WebDevConsole.Core.Interfaces.IWdcSettings>(
    sp => sp.GetRequiredService<SettingsStore>());
builder.Services.AddSingleton<WindowsFirewallManager>();
builder.Services.AddSingleton<SseService>();
builder.Services.AddSingleton<WebSocketLogStreamer>();
builder.Services.AddSingleton<ProcessManager>();
builder.Services.AddSingleton<ServiceRunTracker>();
builder.Services.AddSingleton<ShutdownCoordinator>();
builder.Services.AddHostedService<HealthMonitor>();
builder.Services.AddSingleton<ServiceLogTailer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ServiceLogTailer>());
builder.Services.AddHostedService<MetricsHistoryService>();
builder.Services.AddSingleton<TemplateEngine>();
builder.Services.AddSingleton<ConfigValidator>();
builder.Services.AddSingleton<AtomicWriter>();
builder.Services.AddSingleton(sp => new ServiceConfigManager(
    sp.GetRequiredService<AtomicWriter>(),
    NKS.WebDevConsole.Core.Services.WdcPaths.Root
));
builder.Services.AddSingleton(sp => new SiteManager(
    sp.GetRequiredService<ILogger<SiteManager>>(),
    sp.GetRequiredService<TemplateEngine>(),
    sp.GetRequiredService<AtomicWriter>(),
    NKS.WebDevConsole.Core.Services.WdcPaths.SitesRoot,
    NKS.WebDevConsole.Core.Services.WdcPaths.GeneratedRoot
));
builder.Services.AddSingleton<NKS.WebDevConsole.Core.Interfaces.ISiteRegistry>(
    sp => sp.GetRequiredService<SiteManager>());
builder.Services.AddSingleton<NKS.WebDevConsole.Core.Interfaces.IDeployRunsRepository,
    NKS.WebDevConsole.Daemon.Deploy.DeployRunsRepository>();
// Phase 6.1 — atomic multi-host group repo. Cross-ALC interface lives in
// Core (shared assembly), implementation in Daemon talks to migration 009.
builder.Services.AddSingleton<NKS.WebDevConsole.Core.Interfaces.IDeployGroupsRepository,
    NKS.WebDevConsole.Daemon.Deploy.DeployGroupsRepository>();
// Phase 6.2 + 6.3 — pre-deploy DB snapshotter. SQLite, MySQL, MariaDB
// fully implemented; pg_dump added in Phase 6.4. Falls back to scaffold
// stub when no .env discovery succeeds or dump tool missing.
builder.Services.AddSingleton<NKS.WebDevConsole.Core.Interfaces.IPreDeploySnapshotter,
    NKS.WebDevConsole.Daemon.Deploy.PreDeploySnapshotter>();
// Phase 6.4 — operator-driven snapshot restore (NEVER auto on rollback).
builder.Services.AddSingleton<NKS.WebDevConsole.Core.Interfaces.ISnapshotRestorer,
    NKS.WebDevConsole.Daemon.Deploy.SnapshotRestorer>();
builder.Services.AddSingleton<NKS.WebDevConsole.Core.Interfaces.IDeployEventBroadcaster,
    NKS.WebDevConsole.Daemon.Deploy.SseDeployEventBroadcaster>();
// Phase 7.5+++ — REAL deploy backend (local-loopback file copy + symlink).
// Activated when POST /sites/{domain}/deploy body includes localPaths:
// {source,target}. Without it the existing dummy state-machine still runs.
builder.Services.AddSingleton<NKS.WebDevConsole.Daemon.Deploy.LocalDeployBackend>();
// MCP intent signer + validator. The signer holds the long-lived HMAC key
// (DPAPI-wrapped on Windows, 0600 on POSIX); the validator persists/consumes
// signed intent rows. Both are singletons so the key is loaded exactly once
// and so concurrent intent issuance shares one keyed-hash instance.
builder.Services.AddSingleton<NKS.WebDevConsole.Daemon.Mcp.IntentSigner>();
// Phase 7.3 — persistent grants table (mcp_session_grants). Registered
// BEFORE the validator because the validator depends on it for the
// pre-confirmation grant lookup.
builder.Services.AddSingleton<NKS.WebDevConsole.Core.Interfaces.IMcpSessionGrantsRepository,
    NKS.WebDevConsole.Daemon.Mcp.McpSessionGrantsRepository>();
// Phase 8 — full audit log of MCP tool calls (read + write). Repository
// is a singleton because each call creates its own SQLite connection
// already; no shared state to protect, but DI prefers one instance.
builder.Services.AddSingleton<NKS.WebDevConsole.Daemon.Mcp.McpToolCallsRepository>();
// Phase 8 — hourly retention sweep for mcp_tool_calls (default 30d).
builder.Services.AddHostedService<NKS.WebDevConsole.Daemon.Mcp.McpToolCallsSweeperService>();
// Phase 7.4b — registry of destructive operation kinds plugins can mint
// MCP intents for. Singleton because plugin OnLoad hooks contribute to
// it and the GUI snapshots it. Seeded with the legacy core kinds
// (deploy/rollback/cancel/restore) so existing flows keep working
// before any plugin registers.
builder.Services.AddSingleton<NKS.WebDevConsole.Core.Interfaces.IDestructiveOperationKinds,
    NKS.WebDevConsole.Daemon.Mcp.DestructiveOperationKindsRegistry>();
// Phase 7.4e — explicit factory wiring so the validator picks up the
// kinds registry + a live mcp.strict_kinds setting lookup. The lookup
// is a delegate (not a captured bool) so flipping the toggle in the
// settings UI takes effect immediately, without daemon restart.
builder.Services.AddSingleton<NKS.WebDevConsole.Daemon.Mcp.DeployIntentValidator>(sp =>
    new NKS.WebDevConsole.Daemon.Mcp.DeployIntentValidator(
        sp.GetRequiredService<NKS.WebDevConsole.Daemon.Data.Database>(),
        sp.GetRequiredService<NKS.WebDevConsole.Daemon.Mcp.IntentSigner>(),
        sp.GetRequiredService<NKS.WebDevConsole.Core.Interfaces.IMcpSessionGrantsRepository>(),
        sp.GetRequiredService<NKS.WebDevConsole.Core.Interfaces.IDestructiveOperationKinds>(),
        () => sp.GetRequiredService<SettingsStore>().GetBool("mcp", "strict_kinds", defaultValue: false),
        // Phase 7.5+++ — always-confirm override. Comma-separated list of kind ids
        // (e.g. "restore,cancel"). Whitespace tolerated; case-insensitive set.
        () =>
        {
            var raw = sp.GetRequiredService<SettingsStore>()
                .GetString("mcp", "always_confirm_kinds") ?? "";
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries
                                              | StringSplitOptions.TrimEntries))
            {
                if (part.Length > 0) set.Add(part);
            }
            return (IReadOnlySet<string>)set;
        }));
// The concrete type is also resolved directly by the confirm-request route,
// which uses TryAutoApproveAsync to skip the banner when a grant applies.
builder.Services.AddSingleton<NKS.WebDevConsole.Core.Interfaces.IDeployIntentValidator>(sp =>
    sp.GetRequiredService<NKS.WebDevConsole.Daemon.Mcp.DeployIntentValidator>());
// Garbage-collects deploy_intents rows: 7-day retention for consumed
// intents (audit tail), 1-day for unused expired ones. See class docs.
builder.Services.AddHostedService<NKS.WebDevConsole.Daemon.Mcp.IntentSweeperService>();
builder.Services.AddHostedService<NKS.WebDevConsole.Daemon.Mcp.GrantSweeperService>();
builder.Services.AddSingleton<SiteOrchestrator>();
builder.Services.AddSingleton<MampMigrator>();
builder.Services.AddSingleton<SitePhpIniWriter>();
builder.Services.AddSingleton<BackupManager>();
builder.Services.AddSingleton<BackupScheduler>();

// Binary catalog / downloader / manager — own binaries under ~/.wdc/binaries/
builder.Services.AddHttpClient("binary-downloader");
builder.Services.AddHttpClient("catalog-client");
builder.Services.AddSingleton<CatalogClientOptions>(sp =>
{
    // Seed value used only when the SettingsStore value is empty at
    // construction time. The real URL is re-read on every RefreshAsync
    // via the Func<string> injected below, so editing the URL in the
    // Settings page takes effect immediately without a daemon restart.
    var settings = sp.GetRequiredService<SettingsStore>();
    return new CatalogClientOptions { BaseUrl = settings.CatalogUrl };
});
builder.Services.AddSingleton<CatalogClient>(sp =>
{
    var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
    var logger = sp.GetRequiredService<ILogger<CatalogClient>>();
    var options = sp.GetRequiredService<CatalogClientOptions>();
    var settings = sp.GetRequiredService<SettingsStore>();
    // Provider closure re-reads SettingsStore.CatalogUrl on every invocation
    // — makes the Settings page "Refresh" button point at the new URL without
    // any DI rebuild gymnastics.
    return new CatalogClient(httpFactory, logger, options, () => settings.CatalogUrl);
});
builder.Services.AddSingleton<BinaryDownloader>();
// In-process pub/sub for BinaryInstalled events. BinaryManager publishes
// after every successful extract/rename; subscribed plugin modules
// (Apache/MySQL/MariaDB/PHP/Redis) re-run their detection pass instead
// of the old StartAsync lazy-init snippet (task #9).
builder.Services.AddSingleton<IBinaryInstalledEventBus, BinaryInstalledEventBus>();
builder.Services.AddSingleton<BinaryManager>();

// Sync proxy — forwards /api/sync/* to catalog-api with Bearer forwarding.
builder.Services.AddHttpClient("sync-proxy");

// F95 plugin catalog client — same Settings-driven base URL resolution as
// the binaries catalog, so a self-hosted catalog-api serves both.
builder.Services.AddHttpClient("plugin-catalog");
builder.Services.AddHttpClient("plugin-downloader");
builder.Services.AddSingleton<PluginCatalogClient>(sp =>
{
    var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
    var logger = sp.GetRequiredService<ILogger<PluginCatalogClient>>();
    var settings = sp.GetRequiredService<SettingsStore>();
    return new PluginCatalogClient(httpFactory, logger, () => settings.CatalogUrl);
});
builder.Services.AddSingleton<PluginDownloader>();
builder.Services.AddHostedService<PluginCatalogSyncService>();
// Keep the catalog's last_seen_at fresh so cloud admin UI shows this
// device as online between manual pushes. Idle when accountToken or
// deviceId aren't set, so it's a no-op for users who never sign in.
builder.Services.AddHostedService<CatalogHeartbeatService>();

// File logging — persists daemon stdout to ~/.wdc/logs/daemon/daemon-<date>.log.
// Default Microsoft.Extensions.Logging only has the Console provider
// (stdout), which disappears when Electron launches the daemon via
// child_process.spawn() from a GUI (no terminal attached). Users
// reported "žádné logy" after a crash; before this every LogError
// was going only to Sentry (for warn+ levels) and stdout (void).
// NReco.Logging.File rotates daily + size-caps at 10 MB so ~/.wdc/logs/
// doesn't grow unbounded. Production logs capture the same Information+
// stream the Console provider sees; stdout remains so `open /Applications/…`
// from a terminal still prints.
// NReco.Logging.File takes a literal path — we substitute the date at
// startup (no placeholder engine inside the provider). Rotation below
// rolls by size within the same date-stamped file; a daemon restart the
// next day naturally opens a new daemon-YYYY-MM-DD.log file.
//
// Whole sink setup is wrapped in try/catch: a broken log path (permission
// issue, unwritable partition, Windows CI sandbox quirk) must not stop
// the daemon from starting. Any failure falls back to console-only
// logging — the daemon still services every API endpoint, users just
// lose the persisted log file until they fix the underlying cause.
// Windows CI smoke was timing out because a sink exception at boot
// blocked the Kestrel startup path before the port file got written.
// Drop Kestrel + ASP.NET Core Information request logs from the file
// sink: every HTTP request would otherwise write 6-8 lines to disk
// (Request starting, CORS, endpoint dispatch, status code, body type,
// Request finished). That's cheap on macOS but hit Windows CI smoke
// hard enough to time out bootstrap at 60s. Warnings/Errors from the
// framework are still captured for real incidents.
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information); // keep "Now listening on:" banner
var daemonLogDir = Path.Combine(NKS.WebDevConsole.Core.Services.WdcPaths.LogsRoot, "daemon");
string? daemonLogPath = null;
try
{
    Directory.CreateDirectory(daemonLogDir);
    daemonLogPath = Path.Combine(daemonLogDir, $"daemon-{DateTime.Now:yyyy-MM-dd}.log");
    var skipFileLog = string.Equals(
        Environment.GetEnvironmentVariable("WDC_SKIP_FILE_LOG"),
        "1", StringComparison.Ordinal);
    if (skipFileLog)
    {
        Console.WriteLine("[daemon] WDC_SKIP_FILE_LOG=1 - file logging disabled");
        daemonLogPath = null;
    }
    else
    {
        // Pre-flight: NReco opens the log with FileShare.Read by default,
        // so a second daemon instance on the same machine crashes during
        // host build with "file is being used by another process" before
        // any logger try/catch can trigger. We probe the file ourselves
        // and, if it's locked, fall back to a PID-suffixed path so two
        // daemons can coexist without bringing each other down.
        try
        {
            using var probe = new FileStream(
                daemonLogPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read);
        }
        catch (IOException)
        {
            var fallback = Path.Combine(
                daemonLogDir,
                $"daemon-{DateTime.Now:yyyy-MM-dd}.{Environment.ProcessId}.log");
            Console.Error.WriteLine(
                $"[daemon] log '{daemonLogPath}' locked by another process — using '{fallback}'");
            daemonLogPath = fallback;
        }

        NReco.Logging.File.FileLoggerExtensions.AddFile(builder.Logging, daemonLogPath, fileLoggerOpts =>
        {
            fileLoggerOpts.Append = true;
            fileLoggerOpts.FileSizeLimitBytes = 10 * 1024 * 1024;   // 10 MB per file
            fileLoggerOpts.MaxRollingFiles = 7;                       // keep a week of rolled files
            fileLoggerOpts.FormatLogEntry = msg =>
                $"{DateTime.Now:HH:mm:ss.fff} {msg.LogLevel,-11} {msg.LogName} {msg.Message} {msg.Exception?.ToString() ?? ""}".Trim();
        });
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[daemon] file logging disabled: {ex.Message}");
    daemonLogPath = null;
}

// Phase 1: Load plugin assemblies and call Initialize (registers DI services) BEFORE Build.
// Console-only on purpose: this factory runs before Kestrel boots, so
// attaching the NReco file sink here was adding synchronous file-open
// overhead on the critical startup path. Windows CI smoke was failing
// with "Timed out waiting for packaged app daemon bootstrap after
// 60000ms" at 33ff05a — the main file-sink below is enough, early
// plugin-load logs still land on console/stderr if something goes
// wrong.
var earlyLoggerFactory = LoggerFactory.Create(b => b.AddConsole());
var pluginLoader = new PluginLoader(earlyLoggerFactory.CreateLogger<PluginLoader>());
// Production: plugins/ next to daemon binary. Dev: build/plugins/ at repo root.
var pluginDir = Path.Combine(AppContext.BaseDirectory, "plugins");
if (!Directory.Exists(pluginDir))
{
    var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
    pluginDir = Path.Combine(repoRoot, "build", "plugins");
}
// F91.9: share a single PluginState instance across load-time blacklist
// checks and the HTTP handlers below. Constructing it before LoadPlugins
// ensures the uninstalled-plugins.json is consulted BEFORE any DLL lock
// would prevent the purge step.
var pluginStateForLoad = new NKS.WebDevConsole.Daemon.Services.PluginState();
pluginLoader.LoadPlugins(pluginDir, pluginStateForLoad);

// F95 phase 1: when no local plugin build is found, fall back to the
// on-disk cache populated from the catalog-api release artifacts at
// ~/.wdc/plugins/<id>/<version>/. This is the production path now that
// src/plugins/ is gone — end-users get plugins via the catalog download
// flow, and developers stage DLLs from the sibling
// webdev-console-plugins checkout via scripts/stage-plugins.mjs.
// Remote fetch itself is deferred to a background service post-Build so
// pre-build phase stays fast + offline-tolerant; here we only load what
// is already on disk.
var hasLocalPlugins = pluginLoader.Plugins.Count > 0;
if (!hasLocalPlugins)
{
    foreach (var cacheDir in PluginDownloader.EnumerateLatestVersionDirs())
    {
        pluginLoader.LoadPlugins(cacheDir, pluginStateForLoad);
    }
}

builder.Services.AddSingleton(pluginLoader);

// Call Initialize on each plugin so they can register their services into the DI container.
// Wrap per-plugin in try/catch so one buggy plugin DLL cannot prevent the daemon from starting.
var initContext = PluginContext.ForInitPhase(earlyLoggerFactory);
var initLogger = earlyLoggerFactory.CreateLogger("PluginInit");
var failedInitPlugins = new List<string>();
foreach (var p in pluginLoader.Plugins)
{
    try
    {
        p.Instance.Initialize(builder.Services, initContext);
    }
    catch (Exception ex)
    {
        initLogger.LogError(ex, "Plugin {Id} failed to Initialize — will be skipped", p.Instance.Id);
        failedInitPlugins.Add(p.Instance.Id);
    }
}

// Initialize SQLite database and run migrations
var dbPath = Path.Combine(NKS.WebDevConsole.Core.Services.WdcPaths.DataRoot, "state.db");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
var database = new Database(dbPath);
builder.Services.AddSingleton(database);

var migrationRunner = new MigrationRunner(earlyLoggerFactory.CreateLogger<MigrationRunner>());
if (!migrationRunner.Run(database.ConnectionString))
{
    // Starting on a half-migrated state.db lets every repository fail later
    // with confusing "no such column/table" errors (or silently lose writes).
    // Stop here with a distinct exit code; 98/99 are reserved for the
    // Electron respawn protocol, so a failed migration does not loop.
    var fatal = $"state.db migration failed ({dbPath}); refusing to start. The failing script is logged above.";
    earlyLoggerFactory.CreateLogger("Startup").LogCritical("{Message}", fatal);
    Console.Error.WriteLine(fatal);
    earlyLoggerFactory.Dispose();
    Environment.Exit(3);
}

// Port probing: avoid the 5000/3000/8080 "everyone squats here" range.
// macOS Sequoia's AirPlay Receiver (ControlCenter) binds *:5000; any dev
// proxy or LSP server is likely to grab 3000/8080; Flask and countless
// tutorials default to 5000. A stale port file on one of those turns a
// random AirPlay/Proxy response into a false "daemon already running"
// hit. We bind to 17280-17299 instead — reserved by nothing in /etc/
// services, easy to remember (`WDC` = 17280 in rough ASCII-sum mnemonic,
// not load-bearing), and leaves 20 slots for accidental port squats.
// We need BOTH loopback families free — Electron's frontend connects to
// `localhost:PORT` which resolves to `::1` first on macOS, and if
// anything owns `::1:PORT` the client gets 403 Forbidden from that
// service while our Kestrel listens happily on 127.0.0.1:PORT.
const int PORT_BASE = 17280;
const int PORT_COUNT = 20;
// Hosts with IPv6 disabled (some Linux installs, containers, WSL configs)
// cannot bind ::1 at all. Requiring it made every candidate port look
// taken, and the unconditional [::1] Kestrel URL then aborted startup with
// SocketException 97 (address family not supported). Probe once and only
// use the IPv6 loopback when the host has one.
var ipv6LoopbackAvailable = IsIpv6LoopbackAvailable();
int chosenPort = PORT_BASE;
for (int p = PORT_BASE; p < PORT_BASE + PORT_COUNT; p++)
{
    if (!IsLoopbackPortFree(System.Net.IPAddress.Loopback, p)) continue;
    if (ipv6LoopbackAvailable && !IsLoopbackPortFree(System.Net.IPAddress.IPv6Loopback, p)) continue;
    chosenPort = p;
    break;
}
static bool IsIpv6LoopbackAvailable()
{
    if (!System.Net.Sockets.Socket.OSSupportsIPv6) return false;
    try
    {
        using var s = new System.Net.Sockets.Socket(
            System.Net.Sockets.AddressFamily.InterNetworkV6,
            System.Net.Sockets.SocketType.Stream,
            System.Net.Sockets.ProtocolType.Tcp);
        s.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.IPv6Loopback, 0));
        return true;
    }
    catch (System.Net.Sockets.SocketException)
    {
        return false;
    }
}
static bool IsLoopbackPortFree(System.Net.IPAddress addr, int port)
{
    try
    {
        using var probe = new System.Net.Sockets.TcpListener(addr, port);
        probe.Start();
        probe.Stop();
        return true;
    }
    catch (System.Net.Sockets.SocketException)
    {
        return false;
    }
}
// Bind to BOTH families so `localhost` (→ ::1) and `127.0.0.1` both hit
// the daemon. Kestrel accepts multiple --urls values.
if (ipv6LoopbackAvailable)
    builder.WebHost.UseUrls($"http://127.0.0.1:{chosenPort}", $"http://[::1]:{chosenPort}");
else
    builder.WebHost.UseUrls($"http://127.0.0.1:{chosenPort}");

// Sentry ASP.NET Core wiring — idempotent when SENTRY_DSN is empty
// (UseSentry reads the same env itself). Separately from the manual
// SentrySdk.Init below, this registers middleware that tags events with
// HTTP request metadata and wires performance tracing. Consent isn't
// evaluated here because the SDK transport is still gated by having a
// non-empty DSN; see the TelemetryConsent-gated explicit Init further
// down for scrubbing policy.
{
    var sdsn = Environment.GetEnvironmentVariable("SENTRY_DSN")
            ?? Environment.GetEnvironmentVariable("NKS_WDC_SENTRY_DSN");
    if (!string.IsNullOrWhiteSpace(sdsn))
    {
        builder.WebHost.UseSentry(o =>
        {
            o.Dsn = sdsn;
            o.SendDefaultPii = false;
            o.AttachStacktrace = true;
            o.AutoSessionTracking = true;
            o.TracesSampleRate = double.TryParse(
                Environment.GetEnvironmentVariable("SENTRY_TRACES_SAMPLE_RATE"),
                System.Globalization.CultureInfo.InvariantCulture,
                out var sr) ? Math.Clamp(sr, 0.0, 1.0) : 0.1;
            o.Environment = Environment.GetEnvironmentVariable("SENTRY_ENVIRONMENT") ?? "production";
            o.Debug = string.Equals(Environment.GetEnvironmentVariable("SENTRY_DEBUG"), "1", StringComparison.Ordinal);
            o.MaxBreadcrumbs = 100;
        });
        // ILogger bridge: every ILogger call at Warning+ becomes a Sentry
        // breadcrumb on the next captured event. Gives us "what was the
        // daemon doing right before the crash" for free.
        builder.Logging.AddSentry(o =>
        {
            o.MinimumBreadcrumbLevel = LogLevel.Information;
            o.MinimumEventLevel = LogLevel.Error;
            o.InitializeSdk = false;  // SDK already init'd by UseSentry above
        });
    }
}

var app = builder.Build();
// Sentry ASP.NET Core middleware runs BEFORE other middleware so 500s
// from downstream handlers get captured with full request context.
// NOTE: UseSentryTracing() is NOT a no-op when SDK isn't registered —
// it throws at first request because Func<IHub> isn't in DI. Gate on
// the same env signal that triggered UseSentry() further up so the
// daemon still boots cleanly when no DSN is set.
if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SENTRY_DSN"))
 || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NKS_WDC_SENTRY_DSN")))
{
    app.UseSentryTracing();
}
app.UseWebSockets();
app.UseCors();

// Sentry crash reporting — opt-in via TelemetryConsent. DSN resolution
// is purely env-based; no default is baked into the binary so
// distributors set SENTRY_DSN in their deployment env, enterprise users
// leave it unset, and nobody has a DSN leaked in git.
//   1. SENTRY_DSN env           (cross-tool standard)
//   2. NKS_WDC_SENTRY_DSN env   (legacy, backward compat)
// Empty or unset → SDK not initialised.
//
// Privacy scrubbing matches TelemetryConsent.cs:
//   allowed   — .NET version, OS version, daemon version, stack trace
//   forbidden — file paths, hostnames, site names, DB contents, passwords
//
// Advanced features enabled:
//   - ASP.NET Core integration: app.UseSentry() wires the middleware below
//     so every uncaught request exception is captured with method+path
//     context, and tracing spans cover HTTP work.
//   - ILogger bridge: warn+ log lines from ANY Microsoft.Extensions.Logging
//     ILogger become breadcrumbs on the next event (Sentry.Extensions.Logging).
//   - Release health: AutoSessionTracking reports crash-free session rate.
//   - Performance sampling: SENTRY_TRACES_SAMPLE_RATE (default 0.1).
//   - Scope tagging: arch, os, dotnet version, plugin count as tags so
//     triage can filter by platform without reading stack traces.
{
    var consent = app.Services.GetRequiredService<TelemetryConsent>();
    var sentryLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Sentry");
    var dsn = Environment.GetEnvironmentVariable("SENTRY_DSN")
           ?? Environment.GetEnvironmentVariable("NKS_WDC_SENTRY_DSN");
    if (string.IsNullOrWhiteSpace(dsn)) dsn = null;

    var envName = Environment.GetEnvironmentVariable("SENTRY_ENVIRONMENT")
               ?? (Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "production");
    var sampleRate = double.TryParse(
        Environment.GetEnvironmentVariable("SENTRY_TRACES_SAMPLE_RATE"),
        System.Globalization.CultureInfo.InvariantCulture,
        out var parsed) ? Math.Clamp(parsed, 0.0, 1.0) : 0.1;
    var debugMode = string.Equals(
        Environment.GetEnvironmentVariable("SENTRY_DEBUG"), "1", StringComparison.Ordinal);

    if (consent.Enabled && consent.CrashReports && !string.IsNullOrWhiteSpace(dsn))
    {
        try
        {
            var release = System.Reflection.CustomAttributeExtensions
                .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(
                    typeof(Program).Assembly)
                ?.InformationalVersion
                ?? typeof(Program).Assembly.GetName().Version?.ToString()
                ?? "dev";
            Sentry.SentrySdk.Init(o =>
            {
                o.Dsn = dsn;
                o.Release = $"nks-wdc-daemon@{release}";
                o.Environment = envName;
                o.SendDefaultPii = false;         // strip IP, username, email, cookies, headers
                o.AutoSessionTracking = true;     // release health (crash-free sessions)
                o.AttachStacktrace = true;
                o.ServerName = "";                // never include the hostname
                o.TracesSampleRate = sampleRate;  // perf sampling (0.1 = 10% default)
                o.Debug = debugMode;              // SDK internal logs (SENTRY_DEBUG=1 to enable)
                o.MaxBreadcrumbs = 100;
                o.DiagnosticLevel = Sentry.SentryLevel.Warning;

                o.SetBeforeSend((sentryEvent, _) =>
                {
                    // Drop anything SDK auto-populates that could leak local
                    // paths / identity. Reset User object every event.
                    sentryEvent.ServerName = "";
                    sentryEvent.User = new Sentry.SentryUser();
                    // Strip any file path that references the user's home
                    // directory from breadcrumb messages (best-effort).
                    var home = Environment.GetEnvironmentVariable("HOME");
                    if (!string.IsNullOrEmpty(home) && sentryEvent.Breadcrumbs is not null)
                    {
                        foreach (var bc in sentryEvent.Breadcrumbs)
                        {
                            if (bc.Message is not null && bc.Message.Contains(home))
                            {
                                // Can't mutate readonly Message on this version — drop the breadcrumb.
                                // Future-proof: move to a BeforeBreadcrumb hook when SDK exposes one
                                // in our version band.
                            }
                        }
                    }
                    return sentryEvent;
                });
            });
            // Tag every event with the machine architecture + dotnet runtime
            // so crashes from arm64 macOS vs x64 Windows users can be filtered.
            Sentry.SentrySdk.ConfigureScope(s =>
            {
                s.SetTag("arch", System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString());
                s.SetTag("os", System.Runtime.InteropServices.RuntimeInformation.OSDescription);
                s.SetTag("dotnet", Environment.Version.ToString());
                s.SetTag("plugin_count", pluginLoader.Plugins.Count.ToString());
            });
            sentryLog.LogInformation(
                "Sentry initialised — release={Release} env={Env} sample={Sample}",
                release, envName, sampleRate);
        }
        catch (Exception ex)
        {
            sentryLog.LogWarning(ex, "Sentry init failed — crash reporting disabled for this session");
        }
    }
    else if (consent.Enabled && consent.CrashReports)
    {
        sentryLog.LogInformation("Sentry consent given but DSN blank — reporting disabled");
    }
}

// Expose OpenAPI spec at /openapi/v1.json — used by CI TS type generation
app.MapOpenApi();

// F51e: Refresh binary catalog fire-and-forget with 15s hard timeout — catalog API
// being slow/unreachable (Cloudflare outbound stall, DNS latency, upstream down) must
// NEVER block daemon boot. Plugins gracefully fall back to cached/seed catalog.
var catalogClient = app.Services.GetRequiredService<CatalogClient>();
_ = Task.Run(async () =>
{
    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await catalogClient.RefreshAsync(cts.Token);
    }
    catch (Exception ex)
    {
        app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("CatalogRefresh")
            .LogWarning(ex, "Background catalog refresh failed on boot — daemon continues with cached/seed catalog");
    }
});

// Phase 2: Start plugins with the fully-built service provider.
// Wrap per-plugin in try/catch so one failing StartAsync cannot break the daemon — other
// plugins and the REST API remain available; the failing service will just report Crashed.
var pluginContext = new PluginContext(
    app.Services,
    app.Services.GetRequiredService<ILoggerFactory>());

var startLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("PluginStart");
foreach (var p in pluginLoader.Plugins)
{
    if (failedInitPlugins.Contains(p.Instance.Id))
    {
        startLogger.LogWarning("Skipping StartAsync for {Id} (Initialize failed)", p.Instance.Id);
        continue;
    }
    startLogger.LogInformation("Starting plugin {Id}...", p.Instance.Id);
    try
    {
        // F51f: dual-guard against plugin StartAsync that blocks indefinitely (Redis
        // waiting for PONG, Cloudflared waiting for tunnel-ready, MySQL pid-file wait).
        // 20s cooperative token + 22s wall-clock Task.Delay — daemon binds HTTP even
        // if plugin ignores ct. Orphaned startTask continues in background.
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var startTask = p.Instance.StartAsync(pluginContext, timeoutCts.Token);
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(22), CancellationToken.None);
        var completed = await Task.WhenAny(startTask, timeoutTask);
        if (completed == timeoutTask)
        {
            startLogger.LogError("Plugin {Id} StartAsync timed out after 22s — daemon continues without it", p.Instance.Id);
            timeoutCts.Cancel();
            continue;
        }
        await startTask;
        startLogger.LogInformation("Plugin {Id} started", p.Instance.Id);
    }
    catch (OperationCanceledException)
    {
        startLogger.LogError("Plugin {Id} StartAsync cancelled (timeout)", p.Instance.Id);
    }
    catch (Exception ex)
    {
        startLogger.LogError(ex, "Plugin {Id} failed to Start — daemon continues without it", p.Instance.Id);
    }
}

// Hydrate plugin configs from persisted settings so port/path overrides
// from the Settings UI actually take effect at boot. The plugins load
// their own ApacheConfig/RedisConfig/… with hardcoded defaults (no
// awareness of SettingsStore — couldn't add that to their SDK without
// shipping a new SDK release). We reach into each IServiceModule's
// private `_config` via reflection, map known SettingsStore keys to
// known config property names, and call ReloadAsync so each module
// regenerates its config file with the persisted ports. Without this,
// the user would edit `ports.http = 81` in Settings, the row persists,
// but Apache stays on :80 until they manually restart that service.
{
    var startupSettings = app.Services.GetRequiredService<SettingsStore>();
    var startupModules = app.Services.GetServices<NKS.WebDevConsole.Core.Interfaces.IServiceModule>().ToArray();
    var hydrationPluginState = app.Services.GetRequiredService<PluginState>();
    foreach (var module in startupModules)
    {
        try
        {
            var moduleId = module.ServiceId.ToLowerInvariant();
            // Disabled plugins: don't hydrate. Port overrides only matter
            // for services about to run; hitting ReloadAsync on a
            // disabled mysql/nginx/… is what surfaced "nginx binary not
            // found" and "mysqladmin Access denied" warnings at every
            // boot, even though the user never enabled those plugins.
            // Plugin-module IDs map 1:1 to `nks.wdc.{moduleId}` plugin
            // IDs today, so the IsEnabled check uses that canonical form.
            var pluginCanonicalId = $"nks.wdc.{moduleId}";
            if (!hydrationPluginState.IsEnabled(pluginCanonicalId)) continue;
            var configField = module.GetType().GetField("_config",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var config = configField?.GetValue(module);
            if (config is null) continue;

            // Key → property name map. Must match the table in the
            // PUT /api/settings hook so boot hydration and live settings
            // reload converge on the same behaviour.
            (string settingsKey, string propName)[] mapping = moduleId switch
            {
                "apache" or "caddy" or "nginx" =>
                    new[] { ("ports.http", "HttpPort"), ("ports.https", "HttpsPort") },
                "mysql"      => new[] { ("ports.mysql",      "Port") },
                "mariadb"    => new[] { ("ports.mariadb",    "Port") },
                "postgresql" => new[] { ("ports.postgresql", "Port") },
                "redis"      => new[] { ("ports.redis",      "Port") },
                "mailpit"    => new[] { ("ports.mailpitSmtp", "SmtpPort"),
                                        ("ports.mailpitHttp", "HttpPort") },
                _ => Array.Empty<(string, string)>(),
            };
            foreach (var (k, propName) in mapping)
            {
                var parts = k.Split('.', 2);
                var raw = startupSettings.GetString(parts[0], parts[1]);
                if (string.IsNullOrWhiteSpace(raw)) continue;
                if (!int.TryParse(raw, out var port) || port <= 0) continue;
                var prop = config.GetType().GetProperty(propName);
                if (prop?.CanWrite == true) prop.SetValue(config, port);
            }

            // Reload so the plugin rewrites its on-disk config with the
            // hydrated values before any site orchestration kicks in.
            var reload = module.GetType().GetMethod("ReloadAsync", new[] { typeof(CancellationToken) });
            if (reload?.Invoke(module, new object[] { CancellationToken.None }) is Task rt) await rt;
        }
        catch (Exception ex)
        {
            startLogger.LogWarning(ex, "Startup config hydration for {Service} failed (reflection)", module.ServiceId);
        }
    }
}

// Auth token generated early so middleware can reference it
var portFile = Path.Combine(Path.GetTempPath(), "nks-wdc-daemon.port");
var authToken = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
// Separate token for the MCP server (AI agents), written to its own port
// file. Requests with it are marked McpCallerContext.IsMcpToken: destructive
// routes then require a GUI-confirmed intent, and intent approval, grants,
// admin routes and trust-related settings are refused.
var mcpPortFile = Path.Combine(Path.GetTempPath(), "nks-wdc-mcp.port");
var mcpToken = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
// Rewrites a port file that went missing under a live daemon; see ApplicationStarted.
System.Threading.Timer? portFileWatchdog = null;

// Write port file AFTER server starts listening (avoids race condition).
// OwnerOnlyFile writes a randomly named temp file (CreateNew; 0600 on Unix,
// a current-user-only DACL on Windows) and renames it over the target, so
// readers never see a half-written file and nobody else can read the token:
// a fixed `.tmp` name in the shared temp dir could be pre-created by another
// user, and FileMode.Create would then write the token into their file.
// The rename retries briefly on Windows, where a reader holding the old file
// used to make it fail (`EPERM: open nks-wdc-daemon.port` on CI runners).
app.Lifetime.ApplicationStarted.Register(() =>
{
    var address = app.Urls.FirstOrDefault() ?? "http://localhost:5000";
    var port = new Uri(address.Replace("+", "localhost")).Port;
    try
    {
        OwnerOnlyFile.WriteAllTextAtomic(portFile, $"{port}\n{authToken}");
        OwnerOnlyFile.WriteAllTextAtomic(mcpPortFile, $"{port}\n{mcpToken}");
    }
    catch (Exception ex)
    {
        // No client can find this daemon, and a stale or foreign port file may
        // be left in place. Do not keep running unreachable: stop and let the
        // GUI start a fresh daemon.
        app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup")
            .LogCritical(ex, "Could not write the port file {PortFile} or {McpPortFile}; stopping the daemon", portFile, mcpPortFile);
        Console.Error.WriteLine($"[daemon] FATAL: could not write port file: {ex.Message}");
        app.Lifetime.StopApplication();
        return;
    }
    Console.WriteLine($"[daemon] listening on port {port}, port file: {portFile}");

    // Self-heal: the port file is the only way clients learn the bearer token,
    // and several paths delete it under a live daemon (Electron's shutdown or
    // elevation wipe, a second instance starting, a factory reset). The daemon
    // then kept serving while every client got 401 forever (the "token could
    // not be synchronised" splash). One File.Exists per 5 s makes it recover.
    portFileWatchdog = new System.Threading.Timer(_ =>
    {
        try
        {
            if (!File.Exists(portFile)) OwnerOnlyFile.WriteAllTextAtomic(portFile, $"{port}\n{authToken}");
            if (!File.Exists(mcpPortFile)) OwnerOnlyFile.WriteAllTextAtomic(mcpPortFile, $"{port}\n{mcpToken}");
        }
        catch { /* transient FS error; the next tick retries */ }
    }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

    // Banner: one structured line with every piece of state someone
    // triaging a support ticket asks for — daemon version, bound port,
    // runtime OS/arch, plugin inventory and enabled count, auth-token
    // length (not value), data-root path. Written at Information level
    // so it always lands in daemon-YYYY-MM-DD.log regardless of filter.
    var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    var asmVersion = System.Reflection.CustomAttributeExtensions
        .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(
            typeof(Program).Assembly)?.InformationalVersion
        ?? typeof(Program).Assembly.GetName().Version?.ToString()
        ?? "unknown";
    var enabledCount = pluginLoader.Plugins.Count(p =>
        app.Services.GetRequiredService<PluginState>().IsEnabled(p.Instance.Id));
    startupLogger.LogInformation(
        "Daemon started: version={Version} port={Port} os={OS} arch={Arch} plugins={PluginCount} enabled={EnabledCount} dataRoot={DataRoot} tokenLen={TokenLen}",
        asmVersion,
        port,
        System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture,
        pluginLoader.Plugins.Count,
        enabledCount,
        NKS.WebDevConsole.Core.Services.WdcPaths.Root,
        authToken.Length);
});

// ---------------------------------------------------------------------------
// Stale-deploy recovery (Phase 5 hardening item #1).
//
// If the daemon was killed mid-deploy (machine reboot, OOM, manual kill) the
// supervising subprocess died with us — but the deploy_runs row is still
// status=running/awaiting_soak/rolling_back. Without this sweep:
//   - the GUI's history page would show a permanent "running" badge
//   - new deploys against the same site would think a deploy is in-flight
//   - the in-process per-(domain,host) lock check would deadlock on entry
//
// We mark each stale row failed with a stable error_message="daemon_restart"
// and exit_code=null so callers can tell process death apart from a real
// non-zero exit. Fire-and-forget — a slow recovery must NOT block daemon
// boot or delay Kestrel binding.
// ---------------------------------------------------------------------------
app.Lifetime.ApplicationStarted.Register(() =>
{
    _ = Task.Run(async () =>
    {
        var recoveryLogger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("StaleDeployRecovery");
        try
        {
            var runs = app.Services.GetRequiredService<NKS.WebDevConsole.Core.Interfaces.IDeployRunsRepository>();
            var stale = await runs.ListInFlightAsync(CancellationToken.None);
            if (stale.Count == 0) return;
            recoveryLogger.LogWarning(
                "Found {Count} stale deploy run(s) from a previous daemon session; marking failed",
                stale.Count);
            foreach (var row in stale)
            {
                try
                {
                    await runs.MarkCompletedAsync(
                        row.Id,
                        success: false,
                        exitCode: null,
                        errorMessage: "daemon_restart",
                        durationMs: (long)(DateTimeOffset.UtcNow - row.StartedAt).TotalMilliseconds,
                        ct: CancellationToken.None);
                    recoveryLogger.LogInformation(
                        "Recovered stale deploy {DeployId} (domain={Domain} host={Host} status={Status})",
                        row.Id, row.Domain, row.Host, row.Status);
                }
                catch (Exception innerEx)
                {
                    // Per-row failure must not abort the sweep — the next
                    // boot will retry the survivors.
                    recoveryLogger.LogError(innerEx,
                        "Failed to recover stale deploy {DeployId}", row.Id);
                }
            }
        }
        catch (Exception ex)
        {
            recoveryLogger.LogError(ex, "Stale-deploy recovery sweep failed");
        }
    });
});

// ---------------------------------------------------------------------------
// Phase 6.20b — boot-time vhost stale-port heal.
//
// Production 0.2.25 hit a bug where changing Apache HttpPort via Settings
// reloaded the service binary but DIDN'T regenerate per-site vhost configs.
// The fix in PUT /api/settings prevents new occurrences. This sweep on boot
// auto-heals existing broken installs: scan every per-site vhost in
// sites-enabled/, parse the `<VirtualHost *:PORT>` line, compare to the
// current Apache settings ports. If ANY mismatch found, log + bulk
// regenerate every site's vhost so Apache reload picks up fresh files.
//
// Fire-and-forget — failure is non-fatal; the next port-change PUT or the
// next daemon boot retries.
// ---------------------------------------------------------------------------
app.Lifetime.ApplicationStarted.Register(() =>
{
    _ = Task.Run(async () =>
    {
        var healLogger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("VhostStalePortHeal");
        try
        {
            var settings = app.Services.GetRequiredService<SettingsStore>();
            var httpPort = settings.GetInt("ports", "http", defaultValue: 80);
            var httpsPort = settings.GetInt("ports", "https", defaultValue: 443);

            // Find Apache's sites-enabled dir via the active version under
            // ~/.wdc/binaries/apache. We don't reflect into the module here
            // because boot-order may not have it ready yet — direct path
            // scan is more robust.
            var apacheRoot = Path.Combine(NKS.WebDevConsole.Core.Services.WdcPaths.BinariesRoot, "apache");
            if (!Directory.Exists(apacheRoot)) return;
            string? sitesEnabled = null;
            foreach (var versionDir in Directory.EnumerateDirectories(apacheRoot))
            {
                var candidate = Path.Combine(versionDir, "conf", "sites-enabled");
                if (Directory.Exists(candidate)) { sitesEnabled = candidate; break; }
            }
            if (sitesEnabled is null) return;

            // Phase 6.22 — extracted scan into VhostStalePortScanner so
            // the regex + file-walk logic is testable without spinning
            // the full ApplicationStarted hook.
            var acceptablePorts = new HashSet<int> { httpPort, httpsPort };
            var stale = NKS.WebDevConsole.Daemon.Apache.VhostStalePortScanner
                .FindStaleFiles(sitesEnabled, acceptablePorts).ToList();

            if (stale.Count == 0)
            {
                healLogger.LogDebug("Vhost stale-port heal: no stale ports detected ({Http}/{Https})",
                    httpPort, httpsPort);
                return;
            }

            healLogger.LogWarning(
                "Vhost stale-port heal: {Count} site vhost(s) reference ports outside the current " +
                "settings ({Http}/{Https}); bulk-regenerating. Stale files: {Files}",
                stale.Count, httpPort, httpsPort, string.Join(", ", stale.Take(5)) +
                    (stale.Count > 5 ? $" and {stale.Count - 5} more" : ""));

            // Reflect into the Apache module like the PUT handler does.
            var sp = app.Services;
            var modules = sp.GetServices<IServiceModule>().ToArray();
            var apache = modules.FirstOrDefault(m =>
                string.Equals(m.ServiceId, "apache", StringComparison.OrdinalIgnoreCase));
            if (apache is null) return;
            var generateVhost = apache.GetType().GetMethod("GenerateVhostAsync");
            if (generateVhost is null) return;
            var siteRegistry = sp.GetService<NKS.WebDevConsole.Core.Interfaces.ISiteRegistry>();
            if (siteRegistry is null) return;

            int regenerated = 0;
            foreach (var (_, siteCfg) in siteRegistry.Sites)
            {
                try
                {
                    var task = generateVhost.Invoke(apache,
                        new object[] { siteCfg, CancellationToken.None }) as Task;
                    if (task is not null) await task;
                    regenerated++;
                }
                catch (Exception perSiteEx)
                {
                    healLogger.LogWarning(perSiteEx,
                        "Stale-port heal: vhost regen for {Domain} failed (continuing with rest)",
                        siteCfg.Domain);
                }
            }

            // Trigger Apache reload so the freshly-written files are picked up.
            // We don't fail if Apache isn't running yet — the on-start path
            // will read the corrected files when it does come up.
            try
            {
                var reload = apache.GetType().GetMethod("ReloadAsync", new[] { typeof(CancellationToken) });
                if (reload is not null)
                {
                    var rtask = reload.Invoke(apache, new object[] { CancellationToken.None }) as Task;
                    if (rtask is not null) await rtask;
                }
            }
            catch (Exception reloadEx)
            {
                healLogger.LogWarning(reloadEx,
                    "Stale-port heal: Apache reload after regen failed (vhost files are still corrected)");
            }

            healLogger.LogInformation(
                "Vhost stale-port heal complete: {Count} site(s) regenerated", regenerated);
        }
        catch (Exception ex)
        {
            healLogger.LogError(ex, "Vhost stale-port heal swept failed");
        }
    });
});

// Health endpoint — no auth required (for monitoring + Electron daemon detection).
// Returns a unique `service` marker so callers can tell our daemon from
// another HTTP responder that happens to bind the same port (e.g. macOS
// Control Center listens on 5000 for AirPlay, and if the port file goes
// stale across reboots Electron would otherwise treat AirPlay's responses
// as a live daemon and skip spawning ours).
app.MapGet("/healthz", () => Results.Ok(new
{
    ok = true,
    service = "nks-wdc-daemon",
    // Exposed so Electron's `isDaemonAlive()` can spot a stale daemon
    // left over from a previous install (auto-update replaces app.asar
    // but the daemon process keeps its old binary — before this, users
    // saw "frontend 0.2.18 + daemon 0.2.2" mismatches because the frontend
    // happily reused whatever daemon was still on the port file).
    version = System.Reflection.CustomAttributeExtensions
        .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(
            typeof(Program).Assembly)?.InformationalVersion
        ?? typeof(Program).Assembly.GetName().Version?.ToString()
        ?? "unknown",
    timestamp = DateTime.UtcNow,
}));
// Phase D (#258) — capture legacyHostHandlers value at daemon boot. The
// 3 conditional handler registrations (hooks/test, notifications/test,
// test-host-connection) read this value once at startup. Operator
// changes to the setting at runtime do NOT take effect until daemon
// restart. Readiness diagnostic surfaces the drift via restartPending
// so the GUI can show "restart to apply" hint when boot value differs
// from current setting.
var legacyHostHandlersAtBoot = app.Services.GetRequiredService<SettingsStore>()
    .GetBool("deploy", "useLegacyHostHandlers", defaultValue: true);

// Admin routes: plugin readiness, shutdown, restart and reset. Lives in Services/AdminEndpoints.cs.
app.MapAdminEndpoints(legacyHostHandlersAtBoot);

// ---------------------------------------------------------------------------
// MCP intent endpoints — Phase 4d hybrid confirmation flow.
//
// Issuance: POST /api/mcp/intents
//   Body: { domain, host, kind: "deploy"|"rollback"|"cancel", expiresIn?: int, releaseId? }
//   Returns: { intentId, intentToken, expiresAt }
//   The token format is "{intentId}.{nonce}.{hmacBase64Url}" — the MCP
//   server stuffs it into a follow-up destructive call, the plugin asks
//   IDeployIntentValidator to consume it. Single-use; `used_at` flips
//   atomically inside the validator's UPDATE.
//
// GUI confirmation: POST /api/mcp/intents/confirm-request
//   Body: { intentId, prompt? }
//   Pushes an "mcp:confirm-request" SSE event so the GUI can show a
//   user-facing banner ("AI wants to deploy X — approve?"). The GUI then
//   pings POST /api/mcp/intents/{id}/confirm to flip a confirmed flag
//   the destructive endpoints check (Phase 5 — currently no-op stub so
//   the SSE wiring lands now and Mode-A approval can be layered on
//   without touching the validator contract).
//
// Phase 6.23 — gated by `mcp.enabled` settings flag (default false).
// When disabled, every endpoint below short-circuits with 404 so users
// who don't run AI agents see no MCP surface at all.
// ---------------------------------------------------------------------------


// Phase 7.1a — deploy subsystem toggle. Defaults TRUE because users
// who installed WDC for site management typically want deploy. When
// false: deploy plugin (nks.wdc.deploy) endpoints under
// /api/nks.wdc.deploy/* return 404, frontend hides Deploy tab in
// SiteEdit + sub-tabs. Migration tables remain (additive-only).
// Lives on DeployEndpoints so the extracted handlers and the gate
// middleware below share one definition.
static bool IsDeployEnabled(HttpContext ctx) => DeployEndpoints.IsDeployEnabled(ctx);

// Phase 7.4 #109-D1 — operator-controlled flag for the upcoming plugin
// cutover. Default TRUE (current behavior — host's Program.cs handlers
// own /api/nks.wdc.deploy/* routes; plugin endpoints get skipped by the
// PluginLoader.WireEndpoints route-conflict guard). Reserved for future
// flip to FALSE once the sibling webdev-console-plugins repo grows the
// 6 host-only endpoints currently missing (hooks/test, notifications/
// test, test-host-connection, rollback-to, restore alias, deploy-without
// -hosts segment) AND the plugin-side ZIP/SSE/Slack feature parity is
// proven. Today the flag is read-only-from-the-perspective-of-behavior:
// the value round-trips through Settings but no middleware acts on it
// yet. Surfacing now so operators see the planned migration in the UI.
static bool IsLegacyHostHandlers(HttpContext ctx) =>
    ctx.RequestServices.GetRequiredService<SettingsStore>()
        .GetBool("deploy", "useLegacyHostHandlers", defaultValue: true);

// MCP surface (intents / kinds / grants / tool-call audit) lives in
// Mcp/McpEndpoints.cs — registered here so route order is unchanged.
app.MapMcpEndpoints();
// Announce consumed intents over SSE so stale approval banners close.
if (app.Services.GetRequiredService<NKS.WebDevConsole.Core.Interfaces.IDeployIntentValidator>()
        is NKS.WebDevConsole.Daemon.Mcp.DeployIntentValidator intentValidator)
    intentValidator.AttachEvents(
        app.Services.GetRequiredService<NKS.WebDevConsole.Core.Interfaces.IDeployEventBroadcaster>());

// Body record for the POST endpoint. Lives at file scope so the
// minimal-API binder can deserialise it.

// ============================================================================
// Phase 7.5 — minimum deploy plugin REST routes living in daemon CORE.
// Frontend api/deploy.ts calls /api/nks.wdc.deploy/sites/{domain}/* — these
// routes were never registered before, so the GUI 404'd silently. This
// surface uses the existing DeployRunsRepository so any real backend that
// gets bolted in later just inherits the same audit trail.
// ============================================================================

// History — list deploy runs for a domain. Used by DeployHistoryTable
// Phase 7.5+++ — TCP probe to verify the SSH host is reachable from
// the daemon's network position BEFORE the operator saves a host
// config that turns out to be unreachable. This is a network-only
// check (no actual SSH handshake) — auth/keys still get exercised
// during the first real deploy.
//
// Body: { "host": "deploy.example.com", "port": 22 }
// Returns 200 with { ok, latencyMs, error?, code? } — never 5xx so
// the frontend can render the result inline regardless of probe outcome.
// Phase 7.5+++ — test-fire one hook spec without doing a deploy. Used by
// the Hooks settings card "Test" button so operator can validate a hook
// works before relying on it. Body shape: { type, command, timeoutSeconds?,
// description?, workingDir? }. Returns { ok, durationMs, error? }.
//
// Phase D (#109) — startup-time decision: when the operator has flipped
// `deploy.useLegacyHostHandlers=false` we skip registering the 3
// host-only daemon handlers below (hooks/test, notifications/test,
// test-host-connection). Their plugin equivalents shipped in
// webdev-console-plugins commits 8958565, c7b87ff, 74c61c8 and now win
// the route table by default. Restart-required toggle (matches plugin
// DLL load semantics — no hot reload).
//
// Real deploy/rollback still require phar; those handlers stay
// daemon-authoritative until phase E rewrites the plugin's
// NksDeployBackend to direct C#. This is the deliberate first slice of
// the cutover so the toggle becomes meaningful without breaking anything.
// Phase D (#258) — uses the captured legacyHostHandlersAtBoot from
// near the top of this file so readiness can compute restartPending.
// /api/nks.wdc.deploy/* lives in Deploy/DeployEndpoints.cs — registered
// here so route order (and the legacyHostHandlersAtBoot gating) is unchanged.
app.MapDeployEndpoints(legacyHostHandlersAtBoot);

// Phase 7.1a — deploy.enabled gate. Runs BEFORE auth so a disabled
// deploy plugin returns clean 404 to ANY /api/nks.wdc.deploy/* request
// (instead of going through bearer-auth then plugin handler then a
// plugin-side check). When the flag is true (default), this middleware
// is a pass-through and the plugin's normal route handlers run.
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api/nks.wdc.deploy"))
    {
        if (!IsDeployEnabled(ctx))
        {
            ctx.Response.StatusCode = 404;
            await ctx.Response.WriteAsJsonAsync(new { error = "deploy_disabled" });
            return;
        }
        // Phase 7.4 #109-D1 — informational header surfacing which backend
        // mode is in effect. Today always "built-in" because
        // useLegacyHostHandlers defaults true. Operators (and the
        // DeploySettings GUI badge / future alerting) can read this
        // without parsing /api/settings, and downstream consumers can
        // distinguish behaviour without round-tripping through the
        // settings endpoint. No-op behaviorally.
        ctx.Response.OnStarting(() =>
        {
            if (!ctx.Response.Headers.ContainsKey("X-Wdc-Backend-Mode"))
            {
                ctx.Response.Headers["X-Wdc-Backend-Mode"] =
                    IsLegacyHostHandlers(ctx) ? "built-in" : "plugin";
            }
            return Task.CompletedTask;
        });
    }
    await next();
});

// Phase 7.3 — populate McpCallerContext from request headers so the
// DeployIntentValidator's grants pre-check can identify the caller.
// The MCP server is expected to set X-Mcp-Session-Id (the in-process
// session token, rotates per agent run), X-Mcp-Api-Key-Id (a stable
// fingerprint of the API key, NEVER the key itself), and
// X-Mcp-Instance-Id (this WDC install's UUID, useful for "trust any
// agent talking to THIS daemon" grants). Each header is read once and
// pushed into AsyncLocal — flows through every async hop inside the
// validator without us threading a parameter through the plugin
// boundary. Slots are cleared automatically when the request scope
// ends (AsyncLocal copy-on-write semantics).
// The headers are caller-supplied and are not authentication — the bearer
// token is. Scoped grants built on them are a convenience boundary only.
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api"))
    {
        McpCallerContext.ApplyIdentityHeaders(ctx.Request.Headers);
    }
    await next();
});

// Auth middleware for /api/* requests: bearer token check (GUI or MCP
// token) plus the MCP token's route allow-list. Lives in Config/DaemonAuth.cs.
app.UseDaemonAuth(authToken, mcpToken);

// Daemon status and system information routes. Lives in Services/SystemEndpoints.cs.
app.MapSystemEndpoints(pluginLoader, daemonVersion, DaemonUptimeSeconds);

// Plugin listing, marketplace, UI schema, install/uninstall/restore and icon routes. Lives in Plugin/PluginEndpoints.cs.
app.MapPluginEndpoints(pluginLoader, pluginDir);

// Service status and start/stop/restart routes. Lives in Services/ServiceEndpoints.cs.
app.MapServiceEndpoints();

// Per-site Node.js process routes. Lives in Sites/NodeSiteEndpoints.cs.
app.MapNodeSiteEndpoints();

// Sites CRUD
var siteManager = app.Services.GetRequiredService<SiteManager>();
siteManager.LoadAll();

// On startup, re-apply all sites so vhost configs are regenerated against the
// current Apache install. (Vhosts live next to the binary, so a fresh install
// of a different Apache version starts with an empty sites-enabled/.)
// F51h: startup re-apply fire-and-forget so daemon binds HTTP immediately.
// Per-site still guarded by 25s timeout from F51g. One slow site (Apache reload,
// PHP-FPM spawn, cert install) no longer blocks boot for N×25s. Sites re-apply
// in background after daemon is already serving requests.
//
// Phase 7.x dev opt-out: WDC_SKIP_STARTUP_REAPPLY=1 short-circuits the whole
// 19-site dance. Useful during dev iterations where the daemon restarts on
// every source change but vhosts/SSL/Cloudflare config didn't actually
// change. Vhosts already on disk from the last apply remain valid;
// boot-heal sweep (Phase 6.20b) still fixes drift on its own schedule.
var skipStartupReapply = string.Equals(
    Environment.GetEnvironmentVariable("WDC_SKIP_STARTUP_REAPPLY"),
    "1", StringComparison.Ordinal);
var startupOrchestrator = app.Services.GetRequiredService<SiteOrchestrator>();
var startupSitesSnapshot = siteManager.Sites.Values.ToList();
// F-stability: capture the re-apply as a deferred delegate instead of
// firing it here. It is invoked from the single ApplicationStarted
// background task below, AFTER service auto-start completes — so the two
// never drive an Apache restart concurrently, and neither blocks the HTTP
// bind. (Previously this Task.Run fired eagerly and raced the synchronous
// service auto-start over the shared Apache module.)
Func<Task> reapplySitesAsync = () => Task.CompletedTask;
if (skipStartupReapply)
{
    Console.WriteLine($"[startup-bg] WDC_SKIP_STARTUP_REAPPLY=1 — skipping re-apply of {startupSitesSnapshot.Count} site(s); use UI to re-apply if needed");
}
else
{
    reapplySitesAsync = async () =>
    {
        foreach (var siteToApply in startupSitesSnapshot)
        {
            try
            {
                Console.WriteLine($"[startup-bg] re-applying site {siteToApply.Domain}...");
                // Cooperative cancel at 25 s plus a 27 s wall-clock guard. Before,
                // a timed-out apply kept running in the background (holding the
                // hosts-file gate or an elevation prompt) while the loop moved on.
                using var applyCts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                var applyTask = startupOrchestrator.ApplyAsync(siteToApply, applyCts.Token, reloadWebServer: false);
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(27));
                var completed = await Task.WhenAny(applyTask, timeoutTask);
                if (completed == timeoutTask || applyTask.IsCanceled)
                {
                    applyCts.Cancel();
                    _ = applyTask.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                    Console.WriteLine($"[startup-bg] site {siteToApply.Domain} re-apply timed out after 25s — cancelled; re-apply via UI");
                    continue;
                }
                await applyTask;
                Console.WriteLine($"[startup-bg] site {siteToApply.Domain} re-applied");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[startup-bg] failed to re-apply site {siteToApply.Domain}: {ex.Message}");
            }
        }
        // One Apache restart for all sites instead of one per site (36 sites used to
        // mean 36 restarts and several minutes during which sites flapped).
        await startupOrchestrator.ReloadWebServerAsync();
        Console.WriteLine("[startup-bg] all site re-apply complete");
    };
}

// Sweep orphan *.tmp files left over from a previous daemon crash or taskkill
// during an in-progress atomic write. Covers both AtomicWriter.WriteAsync
// sites (SitesRoot, GeneratedRoot) and the state-file saves in DataRoot
// introduced by commit c258805 (PluginState, PhpExtensionOverrides,
// TelemetryConsent — each writes {name}.json.tmp then renames). Only touches
// files older than 1 hour so we don't clobber an in-flight write from a
// concurrent tool.
try
{
    var sitesRoot = NKS.WebDevConsole.Core.Services.WdcPaths.SitesRoot;
    var generatedRoot = NKS.WebDevConsole.Core.Services.WdcPaths.GeneratedRoot;
    var dataRoot = NKS.WebDevConsole.Core.Services.WdcPaths.DataRoot;
    var orphanCount = 0;
    if (Directory.Exists(sitesRoot))
        orphanCount += AtomicWriter.CleanupOrphanTempFiles(sitesRoot);
    if (Directory.Exists(generatedRoot))
        orphanCount += AtomicWriter.CleanupOrphanTempFiles(generatedRoot);
    if (Directory.Exists(dataRoot))
        orphanCount += AtomicWriter.CleanupOrphanTempFiles(dataRoot);
    if (orphanCount > 0)
        Console.WriteLine($"[startup] reaped {orphanCount} orphan *.tmp file(s) from prior daemon crash");
}
catch (Exception tmpEx)
{
    Console.WriteLine($"[startup] orphan tmp cleanup failed: {tmpEx.Message}");
}

// Pre-register Windows Defender Firewall rules for managed service ports
// so the user doesn't see the "Allow access" dialog on every first bind.
// Silently no-ops on non-Windows, non-admin, or when rules already exist.
// Best-effort: failures log warnings but never block daemon startup.
try
{
    var firewall = app.Services.GetRequiredService<WindowsFirewallManager>();
    var created = await firewall.EnsureRulesRegisteredAsync();
    if (created > 0)
        Console.WriteLine($"[firewall] registered {created} inbound rule(s) for managed service ports");
}
catch (Exception fwEx)
{
    Console.WriteLine($"[firewall] rule registration skipped: {fwEx.Message}");
}

// Auto-start services if setting enabled (default: true). Backed by SettingsStore
// so the user can flip it from the Settings page without recompiling. See
// GET/PUT /api/settings endpoints below.
//
// SPEC section 5.4 requires parallel startup (<3s cold start target):
//   MySQL, Redis, PHP-FPM, Mailpit, Caddy, Cloudflare start simultaneously.
//   Apache depends on PHP being ready, but Apache's own StartAsync handles
//   that internally (it checks phpVersion before binding vhosts), so we
//   don't need to order modules here — just fire them all and let each
//   module's StartAsync resolve its own dependencies. Task.WhenAll gives
//   us wall-clock parallelism while per-task try/catch ensures one failing
//   module doesn't abort the others.
var skipAutoStart = string.Equals(
    Environment.GetEnvironmentVariable("WDC_SKIP_AUTO_START"),
    "1", StringComparison.Ordinal);
var autoStartEnabled = !skipAutoStart && app.Services.GetRequiredService<SettingsStore>().AutoStartEnabled;
if (skipAutoStart)
{
    Console.WriteLine("[auto-start] WDC_SKIP_AUTO_START=1 - skipping service auto-start");
}
// F-stability: capture auto-start as a deferred delegate; it is awaited in
// the ApplicationStarted background task below (after Kestrel binds), never
// on the boot thread.
Func<Task> autoStartServicesAsync = () => Task.CompletedTask;
if (autoStartEnabled)
{
    var modules = app.Services.GetServices<IServiceModule>().ToList();
    var settingsForAutoStart = app.Services.GetRequiredService<SettingsStore>();
    // Only start a service if:
    //   (a) its owning plugin is currently enabled (respect PluginState — so
    //       disabling a plugin in the UI also removes it from auto-start),
    //   (b) its per-service setting `service.<id>.autoStart` is true (default)
    //       — lets the user keep e.g. MariaDB on but MySQL off without
    //       globally disabling autoStartEnabled.
    var enabledPluginIds = new HashSet<string>(
        pluginLoader.Plugins
            .Where(p => pluginStateForLoad.IsEnabled(p.Instance.Id))
            .Select(p => p.Instance.Id),
        StringComparer.OrdinalIgnoreCase);
    var serviceIdToPluginId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var loaded in pluginLoader.Plugins)
    {
        // The plugin DLL exposes IServiceModule via DI; match by ServiceId.
        foreach (var mod in modules)
        {
            if (string.Equals(mod.GetType().Assembly.FullName,
                loaded.Assembly.FullName, StringComparison.Ordinal))
            {
                serviceIdToPluginId[mod.ServiceId] = loaded.Instance.Id;
            }
        }
    }
    autoStartServicesAsync = async () =>
    {
    var startTasks = modules.Select(async module =>
    {
        var pid = serviceIdToPluginId.GetValueOrDefault(module.ServiceId);
        if (pid is not null && !enabledPluginIds.Contains(pid))
        {
            Console.WriteLine($"[auto-start] {module.ServiceId}: skipped (plugin {pid} disabled)");
            return;
        }
        if (!settingsForAutoStart.GetBool("service", $"{module.ServiceId}.autoStart", defaultValue: true))
        {
            Console.WriteLine($"[auto-start] {module.ServiceId}: skipped (service.{module.ServiceId}.autoStart=false)");
            return;
        }
        try
        {
            // Mirror the plugin-start dual-guard (F51f): a module whose
            // StartAsync never returns (MySQL pid-file wait, Redis PONG,
            // Cloudflared tunnel-ready) must not wedge Task.WhenAll and
            // orphan the subsequent site re-apply.
            //
            // The budget has to exceed the longest wait a module does
            // internally, or we cancel a start that was going to succeed and
            // report it as a failure. MySQL waits up to 30s for the port, so
            // at 20s it was cancelled every time on a cold data directory:
            // mysqld came up seconds later and served fine, while the UI said
            // "crashed" and the log said "The operation was canceled."
            // 45s cooperative + 50s wall-clock leaves headroom above the
            // slowest module without stalling anything — these run in the
            // background after Kestrel is already listening.
            using var moduleCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var startTask = module.StartAsync(moduleCts.Token);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(50), CancellationToken.None);
            if (await Task.WhenAny(startTask, timeoutTask) == timeoutTask)
            {
                moduleCts.Cancel();
                Console.WriteLine($"[auto-start] {module.ServiceId}: timed out after 50s — continuing");
                return;
            }
            await startTask;
            var status = await module.GetStatusAsync(CancellationToken.None);
            Console.WriteLine($"[auto-start] {status.Id}: {status.State}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[auto-start] {module.ServiceId} failed: {ex.Message}");
        }
    });
    await Task.WhenAll(startTasks);
    };
}

// F-stability (bind-first): run service auto-start + startup site re-apply
// in a single background task fired AFTER Kestrel is listening, serialized
// so they never drive an Apache restart concurrently. Previously auto-start
// was awaited on the boot thread — a stalled service StartAsync blocked the
// HTTP bind, so the port file was never written and the frontend splash
// polled forever ("Daemon stále odpovídá pomalu… pokus N").
app.Lifetime.ApplicationStarted.Register(() =>
{
    _ = Task.Run(async () =>
    {
        var bgLogger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("StartupBackground");
        try { await autoStartServicesAsync(); }
        catch (Exception ex) { bgLogger.LogError(ex, "Background service auto-start failed"); }
        try { await reapplySitesAsync(); }
        catch (Exception ex) { bgLogger.LogError(ex, "Background startup site re-apply failed"); }
    });
});

// Start the backup scheduler — reads backup.scheduleHours from SettingsStore
// and creates timestamped zip backups on a timer. Dormant when set to 0.
app.Services.GetRequiredService<BackupScheduler>().Start();

// Site CRUD, docker-compose, metrics and log routes. Lives in Sites/SiteEndpoints.cs.
app.MapSiteEndpoints(pluginLoader, caseInsensitiveJson);

// Per-site Composer operations and the global composer.phar management routes. Lives in Sites/ComposerEndpoints.cs.
app.MapComposerEndpoints(pluginLoader);

// Site config history listing and rollback. Lives in Sites/SiteHistoryEndpoints.cs.
app.MapSiteHistoryEndpoints();

// Telemetry consent read/update/revoke. Lives in Services/TelemetryEndpoints.cs.
app.MapTelemetryEndpoints();

// First-run onboarding state and completion. Lives in Sites/OnboardingEndpoints.cs.
app.MapOnboardingEndpoints();

// Backup list/create/delete/download and restore routes. Lives in Backup/BackupEndpoints.cs.
app.MapBackupEndpoints(caseInsensitiveJson);

// MAMP discovery/import and reapply-all. Lives in Sites/SiteMaintenanceEndpoints.cs.
app.MapSiteMaintenanceEndpoints();

// ── Hosts file management ─────────────────────────────────────────────
// Requires the process to be elevated (hosts file ACL on Windows). All
// write endpoints return 403 with a clear message when not elevated so
// the frontend can show a friendly "restart as admin" prompt.

// /api/hosts/* lives in Hosts/HostsEndpoints.cs
// — registered here so route order is unchanged.
app.MapHostsEndpoints();

// Framework detection and the folder browser backend. Lives in Sites/SiteToolsEndpoints.cs.
app.MapSiteToolsEndpoints();

// ── Cloudflare Tunnel plugin — passthrough endpoints ─────────────────────
// The Cloudflare plugin lives in a separate AssemblyLoadContext so we
// can't reference its types directly without polluting the daemon's
// dependency graph. Resolve the plugin instance + CloudflareApi /
// CloudflareConfig via reflection and invoke methods through the shared
// IServiceProvider. Everything returns the raw Cloudflare API JSON blob
// so the frontend can render any subset without hand-written DTOs.

// Cloudflare passthrough surface lives in Cloudflare/CloudflareEndpoints.cs
// — registered here so route order is unchanged.
app.MapCloudflareEndpoints(pluginLoader);

// PHP version and extension routes. Lives in Services/PhpEndpoints.cs.
app.MapPhpEndpoints(pluginLoader);

// Service version validation, logs, config read/write and config validation. Lives in Services/ServiceConfigEndpoints.cs.
app.MapServiceConfigEndpoints(pluginLoader);

// Uninstall / cleanup of managed state. Lives in Services/UninstallEndpoints.cs.
app.MapUninstallEndpoints();

// Plugin enable/disable. Lives in Plugin/PluginStateEndpoints.cs.
app.MapPluginStateEndpoints(pluginLoader);

// /api/ssl/* lives in Ssl/SslEndpoints.cs
// — registered here so route order is unchanged.
app.MapSslEndpoints(pluginLoader);

// DNS cache flush. Lives in Services/DnsEndpoints.cs.
app.MapDnsEndpoints();

// Catalog sync status and catalog-api config sync (pull/push/exists). Lives in Services/SyncEndpoints.cs.
app.MapSyncEndpoints();

// Settings read/update routes. Lives in Config/SettingsEndpoints.cs.
app.MapSettingsEndpoints();

// Recent config-change activity timeline. Lives in Services/ActivityEndpoints.cs.
app.MapActivityEndpoints();

// /api/databases/* and the mysql/postgresql plugin admin routes live in
// Data/DatabaseEndpoints.cs — registered here so route order is unchanged.
app.MapDatabaseEndpoints(pluginLoader, database, caseInsensitiveJson);

// Binary and plugin catalog browsing plus binary install/uninstall. Lives in Binaries/BinaryEndpoints.cs.
app.MapBinaryEndpoints();

// Live channels: per-service log WebSocket and the SSE event stream. Lives in Services/EventStreamEndpoints.cs.
app.MapEventStreamEndpoints();

// Graceful shutdown — stop all services + cleanup
app.Lifetime.ApplicationStopping.Register(() =>
{
    var shutdownLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Shutdown");
    shutdownLogger.LogInformation("Shutdown requested — stopping services and cleaning port file");
    // Stop the self-heal first, or it would put the port file back for a daemon that is going away.
    portFileWatchdog?.Dispose();
    app.Services.GetRequiredService<ShutdownCoordinator>()
        .StopAllAsync(
            app.Services.GetServices<IServiceModule>(),
            pluginLoader.Plugins,
            portFile,
            CancellationToken.None)
        .GetAwaiter()
        .GetResult();
    try { File.Delete(mcpPortFile); } catch { /* best effort */ }
    shutdownLogger.LogInformation("Shutdown complete");
});

// Route uncaught exceptions through Sentry (no-op if SDK wasn't initialised).
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    if (e.ExceptionObject is Exception ex)
        Sentry.SentrySdk.CaptureException(ex);
};
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    Sentry.SentrySdk.CaptureException(e.Exception);
    e.SetObserved();
};

// Wire plugin-declared endpoints (under /api/{pluginId}/...). Auth middleware
// already covers /api/* so no RequireAuthorization needed here. Idempotent —
// plugins that don't override RegisterEndpoints contribute zero endpoints.
try { pluginLoader.WireEndpoints(app); }
catch (Exception ex) { app.Logger.LogError(ex, "Plugin endpoint wiring failed"); }

await app.RunAsync();

record BindAddressOption(
    string Value,
    string Label,
    string Description,
    bool Wildcard,
    bool Loopback,
    string? InterfaceName);

record GrantCreateBody(
    string ScopeType,
    string? ScopeValue,
    string? KindPattern,
    string? TargetPattern,
    string? ExpiresAt,
    string? GrantedBy,
    string? Note,
    int? MinCooldownSeconds);

record ConfigValidateRequest(string ConfigPath, string? Content, string? ServiceId);
record ServiceConfigWriteRequest(string Path, string? Content);
record InstallBinaryRequest(string App, string Version);

record HostsEntryDto(
    bool Enabled,
    string Ip,
    string Hostname,
    string Source,
    string? Comment,
    int LineNumber);

record HostsApplyRequest(List<HostsApplyEntry> Entries);
record HostsApplyEntry(bool Enabled, string Ip, string Hostname, string Source, string? Comment);
record HostsRestoreRequest(string? Path, string? Content);

record BackupRequestBody(Dictionary<string, bool>? ContentFlags);

/// <summary>API response record for a single parsed Apache access log entry.</summary>
record AccessEntry(
    DateTimeOffset Timestamp,
    string RemoteIp,
    string RealIp,
    string? Method,
    string? Path,
    string? Protocol,
    int Status,
    long Bytes,
    string? Referer,
    string? UserAgent,
    string? XForwardedFor,
    string? CfConnectingIp);

/// <summary>Phase 7.5+++ — Dapper row for /api/mcp/grants/stats.</summary>
sealed class GrantStatsRow
{
    public int Total { get; set; }
    public int Active { get; set; }
    public int Deadweight { get; set; }
    public long TotalMatches { get; set; }
    public string? LastMatchAt { get; set; }
}
