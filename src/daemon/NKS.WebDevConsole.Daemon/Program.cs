using System.Text.Json;
using Dapper;
using NKS.WebDevConsole.Daemon.Plugin;
using NKS.WebDevConsole.Daemon.Services;
using NKS.WebDevConsole.Daemon.Sites;
using NKS.WebDevConsole.Daemon.Binaries;
using NKS.WebDevConsole.Daemon.Backup;
using NKS.WebDevConsole.Daemon.Config;
using NKS.WebDevConsole.Daemon.Data;
using NKS.WebDevConsole.Daemon.Mcp;
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

// CORS for Electron renderer — the packaged app loads the Vue SPA from
// a `file://` URL which gives `Origin: null`, and the dev server runs
// on `http://localhost:5173`. Kestrel binds loopback-only (see the
// daemon listener config below) so the daemon is unreachable from
// anywhere but the same machine anyway — that makes a permissive CORS
// policy safe: anything that can reach the daemon is already running
// on this host with this user's privileges.
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy
            .SetIsOriginAllowed(_ => true)
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials());
});

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
builder.Services.AddSingleton<ShutdownCoordinator>();
builder.Services.AddHostedService<HealthMonitor>();
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
builder.Services.AddSingleton<NKS.WebDevConsole.Core.Interfaces.IDeployIntentValidator>(sp =>
    new NKS.WebDevConsole.Daemon.Mcp.DeployIntentValidator(
        sp.GetRequiredService<NKS.WebDevConsole.Daemon.Data.Database>(),
        sp.GetRequiredService<NKS.WebDevConsole.Daemon.Mcp.IntentSigner>(),
        sp.GetRequiredService<NKS.WebDevConsole.Core.Interfaces.IMcpSessionGrantsRepository>(),
        sp.GetRequiredService<NKS.WebDevConsole.Core.Interfaces.IDestructiveOperationKinds>(),
        () => sp.GetRequiredService<SettingsStore>().GetBool("mcp", "strict_kinds", defaultValue: false)));
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
    NReco.Logging.File.FileLoggerExtensions.AddFile(builder.Logging, daemonLogPath, fileLoggerOpts =>
    {
        fileLoggerOpts.Append = true;
        fileLoggerOpts.FileSizeLimitBytes = 10 * 1024 * 1024;   // 10 MB per file
        fileLoggerOpts.MaxRollingFiles = 7;                       // keep a week of rolled files
        fileLoggerOpts.FormatLogEntry = msg =>
            $"{DateTime.Now:HH:mm:ss.fff} {msg.LogLevel,-11} {msg.LogName} {msg.Message} {msg.Exception?.ToString() ?? ""}".Trim();
    });
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
migrationRunner.Run(database.ConnectionString);

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
int chosenPort = PORT_BASE;
for (int p = PORT_BASE; p < PORT_BASE + PORT_COUNT; p++)
{
    if (!IsLoopbackPortFree(System.Net.IPAddress.Loopback, p)) continue;
    if (!IsLoopbackPortFree(System.Net.IPAddress.IPv6Loopback, p)) continue;
    chosenPort = p;
    break;
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
builder.WebHost.UseUrls($"http://127.0.0.1:{chosenPort}", $"http://[::1]:{chosenPort}");

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
                "mysql"   => new[] { ("ports.mysql",       "Port") },
                "mariadb" => new[] { ("ports.mariadb",     "Port") },
                "redis"   => new[] { ("ports.redis",       "Port") },
                "mailpit" => new[] { ("ports.mailpitSmtp", "SmtpPort"),
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

// Write port file AFTER server starts listening (avoids race condition).
// Uses atomic temp+rename so readers on Windows never see a half-written file
// or hit EPERM/EBUSY against the daemon's open write handle — this matters for
// the packaged-runtime smoke step on GitHub Actions Windows runners, which
// used to flake on `EPERM: open nks-wdc-daemon.port` right after daemon boot.
app.Lifetime.ApplicationStarted.Register(() =>
{
    var address = app.Urls.FirstOrDefault() ?? "http://localhost:5000";
    var port = new Uri(address.Replace("+", "localhost")).Port;
    var tmpFile = portFile + ".tmp";
    var portFileContent = $"{port}\n{authToken}";
    if (OperatingSystem.IsWindows())
    {
        // %TEMP% on Windows resolves to a per-user directory whose ACL
        // already restricts read access to the owning user, so the default
        // File.WriteAllText behaviour is fine here.
        File.WriteAllText(tmpFile, portFileContent);
    }
    else
    {
        // On Linux/macOS the default umask (022) leaves the port file at
        // 0644 inside world-readable /tmp. Any local user could then `cat`
        // the bearer token and impersonate the daemon owner against every
        // /api/* endpoint (sites, hosts, certs, mysql admin). Force 0600
        // explicitly so only the WDC user ever sees the token.
        var opts = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        };
        using var stream = new FileStream(tmpFile, opts);
        using var writer = new StreamWriter(stream);
        writer.Write(portFileContent);
    }
    File.Move(tmpFile, portFile, overwrite: true);
    Console.WriteLine($"[daemon] listening on port {port}, port file: {portFile}");

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
app.MapPost("/api/admin/shutdown", (IHostApplicationLifetime lifetime) =>
{
    _ = Task.Run(() => lifetime.StopApplication());
    return Results.Accepted();
});

// F91.7/F91.8: graceful restart — daemon exits with code 99, which
// Electron's main process treats as "respawn me". Used after uninstall to
// fully unload locked plugin DLLs without forcing the user to kill the app.
// Uses Environment.Exit(99) rather than ExitCode + StopApplication because
// Program.cs does not `return 99` from Main — after graceful shutdown the
// process exits with code 0 and Electron never sees the restart signal.
app.MapPost("/api/admin/restart", () =>
{
    _ = Task.Run(async () =>
    {
        // Delay so the HTTP 202 response flushes + port file unlock path
        // completes before we slam the process. 300ms is empirically safe
        // on both Windows (locked file handles) and Linux.
        await Task.Delay(300);
        // Best-effort port file cleanup so the respawned daemon doesn't
        // reuse a stale port/token combo while it is still booting.
        try
        {
            var pf = Path.Combine(Path.GetTempPath(), "nks-wdc-daemon.port");
            if (File.Exists(pf)) File.Delete(pf);
        }
        catch { /* Electron will just see stale file briefly — harmless */ }
        Environment.Exit(99);
    });
    return Results.Accepted();
});

// POST /api/admin/reset?scope=settings|factory — destructive reset.
//   scope=settings → wipes only the `settings` table (ports, paths, catalog
//     URL, autoStart flags, sync tokens). Sites/databases/binaries are kept.
//   scope=factory  → TRUE factory reset. Stops every service (Apache, MySQL,
//     MariaDB, PHP-FPM, Redis, …), signals Electron that WDC_DATA_DIR and
//     the renderer's Electron userData (localStorage with accountToken,
//     cookies, caches) must be wiped before the next spawn. Daemon exits
//     with special code 98 — Electron's daemon.on('exit') handler
//     recognises 98 as "wipe ~/.wdc/ then respawn". On next boot the
//     app comes up exactly like a fresh install: no sites, no databases,
//     no binaries, no signed-in account. Previous scope=factory kept
//     ~/.wdc/binaries/ (1 GB Apache/PHP/MySQL) + ~/.wdc/data/{mysql,mariadb}/
//     which was a UX footgun — user clicked "Factory reset" and saw their
//     data / credentials / installed PHP versions still there.
app.MapPost("/api/admin/reset", async (HttpContext ctx, Database db, SiteManager siteManager, SiteOrchestrator orchestrator, string? scope) =>
{
    // Scope is mandatory — missing param used to default to "settings" and
    // that was a footgun (blank curl call silently wiped the DB). Force the
    // caller to say what they want.
    if (string.IsNullOrWhiteSpace(scope))
        return Results.BadRequest(new { error = "scope query parameter is required: 'settings' or 'factory'" });
    var effectiveScope = scope.Trim().ToLowerInvariant();
    if (effectiveScope != "settings" && effectiveScope != "factory")
        return Results.BadRequest(new { error = "scope must be 'settings' or 'factory'" });

    var resetLogger = ctx.RequestServices.GetRequiredService<ILogger<Program>>();

    if (effectiveScope == "factory")
    {
        // Delete every site from SiteManager FIRST (wipes TOML files on
        // disk) so the following UpdateHostsBlockAsync call collects an
        // empty domain set and actually clears the /etc/hosts managed
        // block. Before this ordering fix the hosts call collected the
        // still-loaded sites from SiteManager and kept the block intact.
        var domainsToDelete = siteManager.Sites.Keys.ToList();
        foreach (var d in domainsToDelete)
        {
            try { siteManager.Delete(d); }
            catch (Exception ex) { resetLogger.LogWarning(ex, "[factory-reset] site {Domain} delete failed", d); }
        }

        // Strip the managed /etc/hosts block BEFORE stopping services —
        // this needs elevation (osascript admin / pkexec / UAC) and the
        // dialog is easier to reason about while the UI is still live.
        // Without this, a user's `/etc/hosts` keeps `127.0.0.1 myapp.loc`
        // entries pointing at a server that no longer exists after reset.
        try
        {
            await orchestrator.UpdateHostsBlockAsync(Array.Empty<string>(), ctx.RequestAborted);
            resetLogger.LogInformation("[factory-reset] /etc/hosts managed block cleared");
        }
        catch (Exception ex)
        {
            resetLogger.LogWarning(ex, "[factory-reset] hosts block cleanup failed — user may need to manually remove the # BEGIN NKS WebDev Console block");
        }

        // Stop every service module BEFORE we nuke the data dir — otherwise
        // mariadbd / mysqld / httpd / redis-server keep open file handles
        // inside ~/.wdc/data/, causing either undeletable files on macOS/
        // Linux or partial wipe leaving corrupted pid/lock files that poison
        // the next boot. 5 s cooperative cancellation per module — any that
        // hang past that get orphaned and will be SIGKILLed when daemon
        // exits.
        var modules = ctx.RequestServices.GetServices<IServiceModule>().ToArray();
        foreach (var mod in modules)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await mod.StopAsync(cts.Token);
                resetLogger.LogInformation("[factory-reset] {Service} stopped", mod.ServiceId);
            }
            catch (Exception ex)
            {
                resetLogger.LogWarning(ex, "[factory-reset] {Service} stop failed — continuing", mod.ServiceId);
            }
        }
    }

    try
    {
        using var conn = db.CreateConnection();
        conn.Open();
        using var tx = conn.BeginTransaction();
        await conn.ExecuteAsync("DELETE FROM settings", transaction: tx);
        await conn.ExecuteAsync("DELETE FROM config_history WHERE entity_type = 'setting'", transaction: tx);
        tx.Commit();
        conn.Close();  // Release SQLite handle so the file can be unlinked below.
    }
    catch (Exception ex)
    {
        return Results.Problem($"Reset failed: {ex.Message}");
    }

    // For scope=factory we rely on Electron to rm -rf ~/.wdc/ after the
    // daemon exits with code 98. The daemon can't delete its OWN running
    // directory reliably (Mach-O binary mmapped on macOS, OS file locks
    // on Windows), so we signal the parent and let Electron do the nuke
    // between daemon invocations. See src/frontend/electron/main.ts
    // `daemon.on('exit', code => ...)` — code 98 branch.
    var exitCode = effectiveScope == "factory" ? 98 : 99;

    _ = Task.Run(async () =>
    {
        await Task.Delay(300);
        try
        {
            var pf = Path.Combine(Path.GetTempPath(), "nks-wdc-daemon.port");
            if (File.Exists(pf)) File.Delete(pf);
        }
        catch { }
        Environment.Exit(exitCode);
    });
    return Results.Ok(new { scope = effectiveScope, restarting = true, exitCode });
});

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

// Helper: read mcp.enabled from settings on every request. Cheap (in-memory
// dictionary lookup); could be cached but settings rarely change so the
// extra round-trip via SettingsStore is fine.
static bool IsMcpEnabled(HttpContext ctx) =>
    ctx.RequestServices.GetRequiredService<SettingsStore>()
        .GetBool("mcp", "enabled", defaultValue: false);

// Phase 7.1a — deploy subsystem toggle. Defaults TRUE because users
// who installed WDC for site management typically want deploy. When
// false: deploy plugin (nks.wdc.deploy) endpoints under
// /api/nks.wdc.deploy/* return 404, frontend hides Deploy tab in
// SiteEdit + sub-tabs. Migration tables remain (additive-only).
static bool IsDeployEnabled(HttpContext ctx) =>
    ctx.RequestServices.GetRequiredService<SettingsStore>()
        .GetBool("deploy", "enabled", defaultValue: true);

app.MapPost("/api/mcp/intents", async (
    HttpContext ctx,
    NKS.WebDevConsole.Daemon.Data.Database db,
    NKS.WebDevConsole.Daemon.Mcp.IntentSigner signer,
    NKS.WebDevConsole.Core.Interfaces.IDeployEventBroadcaster eventsBus) =>
{
    if (!IsMcpEnabled(ctx)) return Results.NotFound(new { error = "mcp_disabled" });
    using var doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body);
    var root = doc.RootElement;
    string? domain = root.TryGetProperty("domain", out var dEl) ? dEl.GetString() : null;
    string? host = root.TryGetProperty("host", out var hEl) ? hEl.GetString() : null;
    string? kind = root.TryGetProperty("kind", out var kEl) ? kEl.GetString() : null;
    string? releaseId = root.TryGetProperty("releaseId", out var rEl) ? rEl.GetString() : null;
    int expiresInSec = root.TryGetProperty("expiresIn", out var eEl) && eEl.TryGetInt32(out var ei) ? ei : 300;

    if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(host) ||
        string.IsNullOrWhiteSpace(kind))
    {
        return Results.BadRequest(new { error = "domain, host, kind are required" });
    }
    // Phase 7.4 — kind is now an open namespace so plugins can mint
    // intents for their own destructive ops (db:drop_table, site:delete,
    // plugin:reset, …) without requiring a daemon-side migration.
    // Charset/length rule mirrors the schema CHECK in migration 013:
    // 1-64 chars, must start with a letter, [a-z0-9_:] only. Colon
    // is the conventional namespace separator (e.g. "deploy:full").
    if (!System.Text.RegularExpressions.Regex.IsMatch(kind!, "^[a-z][a-z0-9_:]{0,63}$"))
    {
        return Results.BadRequest(new
        {
            error = "kind_invalid",
            detail = "kind must match ^[a-z][a-z0-9_:]{0,63}$ (lowercase letters/digits/_/:; max 64 chars)",
        });
    }
    // Clamp the expiry window. Long-lived signed intents defeat the point
    // of single-use tokens — 1h ceiling matches the MCP server's CCR
    // session length so a single AI turn always has a fresh signature.
    expiresInSec = Math.Clamp(expiresInSec, 30, 3600);

    var intentId = Guid.NewGuid().ToString("D");
    var nonce = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    var expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresInSec);
    var canonical = NKS.WebDevConsole.Daemon.Mcp.IntentSigner.Canonicalize(
        intentId, domain!, host!, kind!, nonce, expiresAt, releaseId);
    var signature = signer.Sign(canonical);

    using var conn = db.CreateConnection();
    await conn.OpenAsync();
    await Dapper.SqlMapper.ExecuteAsync(conn,
        "INSERT INTO deploy_intents (id, domain, host, release_id, kind, nonce, expires_at, hmac_signature) " +
        "VALUES (@Id, @Domain, @Host, @ReleaseId, @Kind, @Nonce, @ExpiresAt, @Signature)",
        new
        {
            Id = intentId,
            Domain = domain,
            Host = host,
            ReleaseId = releaseId,
            Kind = kind,
            Nonce = nonce,
            ExpiresAt = expiresAt.ToString("o"),
            Signature = signature,
        });

    // Phase 7.5+++ — broadcast intent lifecycle so the admin McpIntents
    // table refreshes without F5 when AI/CI mints a new token. Best-effort
    // (no subscribers = no-op); never block the response on SSE I/O.
    try
    {
        await eventsBus.BroadcastAsync("mcp:intent-changed",
            new { change = "created", intentId, domain, host, kind });
    }
    catch { /* SSE failure is non-fatal */ }

    return Results.Ok(new
    {
        intentId,
        intentToken = $"{intentId}.{nonce}.{signature}",
        expiresAt = expiresAt.ToString("o"),
    });
});

app.MapPost("/api/mcp/intents/confirm-request", async (
    HttpContext ctx,
    NKS.WebDevConsole.Core.Interfaces.IDeployEventBroadcaster broadcaster,
    NKS.WebDevConsole.Core.Interfaces.IDestructiveOperationKinds kindsRegistry,
    NKS.WebDevConsole.Daemon.Data.Database db) =>
{
    if (!IsMcpEnabled(ctx)) return Results.NotFound(new { error = "mcp_disabled" });
    using var doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body);
    var root = doc.RootElement;
    string? intentId = root.TryGetProperty("intentId", out var iEl) ? iEl.GetString() : null;
    string? prompt = root.TryGetProperty("prompt", out var pEl) ? pEl.GetString() : null;
    if (string.IsNullOrWhiteSpace(intentId))
    {
        return Results.BadRequest(new { error = "intentId is required" });
    }
    // Phase 6.14b — include expiresAt + kind so the GUI banner can show a
    // live countdown and surface what verb is about to fire. Best-effort
    // lookup; if the row vanished (intent never persisted, race with
    // sweeper), fall back to the minimal payload.
    string? expiresAt = null;
    string? kind = null;
    string? domain = null;
    string? host = null;
    try
    {
        using var conn = db.CreateConnection();
        await conn.OpenAsync();
        var meta = await Dapper.SqlMapper.QuerySingleOrDefaultAsync<dynamic>(conn,
            "SELECT expires_at, kind, domain, host FROM deploy_intents WHERE id = @Id",
            new { Id = intentId });
        if (meta is not null)
        {
            expiresAt = (string?)meta.expires_at;
            kind = (string?)meta.kind;
            domain = (string?)meta.domain;
            host = (string?)meta.host;
        }
    }
    catch { /* best-effort; banner still renders without metadata */ }

    // Phase 7.4c — enrich the SSE event with the human label + danger
    // level the kind was registered with. The banner surfaces these so
    // the operator sees "Restore database backup" instead of bare
    // "restore", and gets visual escalation (red border + typed-host
    // confirm) for kinds tagged Destructive. Falls back to the bare
    // kind id when the registry doesn't know it (post-uninstall race
    // or core-only bootstrap before any plugin contributed).
    string? kindLabel = null;
    string? kindDanger = null;
    string? kindPluginId = null;
    if (!string.IsNullOrEmpty(kind))
    {
        var registered = kindsRegistry.Get(kind);
        if (registered is not null)
        {
            kindLabel = registered.Label;
            kindDanger = registered.Danger.ToString().ToLowerInvariant();
            kindPluginId = registered.PluginId;
        }
    }

    // Best-effort: the SSE bus is the GUI's notification channel. Failure
    // to broadcast (no subscribers, etc.) is not fatal — the AI can still
    // proceed with MCP_DEPLOY_AUTO_APPROVE=true to bypass GUI banner.
    await broadcaster.BroadcastAsync("mcp:confirm-request",
        new { intentId, prompt, expiresAt, kind, kindLabel, kindDanger, kindPluginId, domain, host });
    return Results.Accepted();
});

// GUI calls this when the user clicks Approve on the banner that the
// confirm-request SSE event raised. Single-stamp: only the first POST
// flips `confirmed_at`; subsequent calls return 409 so a confused
// double-click can't be mistaken for a fresh approval.
app.MapPost("/api/mcp/intents/{intentId}/confirm", async (
    string intentId,
    HttpContext ctx,
    NKS.WebDevConsole.Daemon.Data.Database db,
    NKS.WebDevConsole.Core.Interfaces.IDeployEventBroadcaster eventsBus) =>
{
    if (!IsMcpEnabled(ctx)) return Results.NotFound(new { error = "mcp_disabled" });
    if (string.IsNullOrWhiteSpace(intentId))
    {
        return Results.BadRequest(new { error = "intentId is required" });
    }
    using var conn = db.CreateConnection();
    await conn.OpenAsync();
    // Pre-check existence so we can distinguish 404 from 409 cleanly —
    // SQLite's UPDATE rowcount alone collapses both into 0.
    var exists = await Dapper.SqlMapper.QuerySingleOrDefaultAsync<int?>(conn,
        "SELECT 1 FROM deploy_intents WHERE id = @Id",
        new { Id = intentId });
    if (exists is null) return Results.NotFound(new { error = "intent_not_found", intentId });

    var now = DateTimeOffset.UtcNow.ToString("o");
    var rows = await Dapper.SqlMapper.ExecuteAsync(conn,
        "UPDATE deploy_intents SET confirmed_at = @Now WHERE id = @Id AND confirmed_at IS NULL",
        new { Id = intentId, Now = now });
    if (rows == 0)
    {
        return Results.Conflict(new { error = "already_confirmed", intentId });
    }
    try
    {
        await eventsBus.BroadcastAsync("mcp:intent-changed",
            new { change = "confirmed", intentId, confirmedAt = now });
    }
    catch { /* SSE failure is non-fatal */ }
    return Results.Ok(new { intentId, confirmedAt = now });
});

// Phase 6.12b — operator-driven intent revoke. Marks used_at without
// actually consuming, so a leaked or unwanted token can be neutered
// before an AI client tries to fire it. Idempotent — second call
// returns 409 already_used.
app.MapPost("/api/mcp/intents/{intentId}/revoke", async (
    string intentId,
    HttpContext ctx,
    NKS.WebDevConsole.Daemon.Data.Database db,
    NKS.WebDevConsole.Core.Interfaces.IDeployEventBroadcaster eventsBus) =>
{
    if (!IsMcpEnabled(ctx)) return Results.NotFound(new { error = "mcp_disabled" });
    if (string.IsNullOrWhiteSpace(intentId))
    {
        return Results.BadRequest(new { error = "intentId is required" });
    }
    using var conn = db.CreateConnection();
    await conn.OpenAsync();
    var exists = await Dapper.SqlMapper.QuerySingleOrDefaultAsync<int?>(conn,
        "SELECT 1 FROM deploy_intents WHERE id = @Id",
        new { Id = intentId });
    if (exists is null) return Results.NotFound(new { error = "intent_not_found", intentId });

    var now = DateTimeOffset.UtcNow.ToString("o");
    var rows = await Dapper.SqlMapper.ExecuteAsync(conn,
        "UPDATE deploy_intents SET used_at = @Now WHERE id = @Id AND used_at IS NULL",
        new { Id = intentId, Now = now });
    if (rows == 0)
    {
        return Results.Conflict(new { error = "already_used", intentId });
    }
    try
    {
        await eventsBus.BroadcastAsync("mcp:intent-changed",
            new { change = "revoked", intentId, revokedAt = now });
    }
    catch { /* SSE failure is non-fatal */ }
    return Results.Ok(new { intentId, revokedAt = now });
});

// Phase 6.11b — admin inventory of all signed intents. Read-only, no
// destructive side effects — hands back the full deploy_intents row
// list (newest first) so a wdc operator can audit what AI/CI clients
// have minted recently. Bearer-auth on /api/* is sufficient gate.
app.MapGet("/api/mcp/intents", async (
    HttpContext ctx,
    NKS.WebDevConsole.Daemon.Data.Database db,
    NKS.WebDevConsole.Core.Interfaces.IDestructiveOperationKinds kindsRegistry,
    int limit = 100,
    string? matchedGrantId = null) =>
{
    if (!IsMcpEnabled(ctx)) return Results.NotFound(new { error = "mcp_disabled" });
    using var conn = db.CreateConnection();
    await conn.OpenAsync();
    // Phase 7.5+++ — optional matchedGrantId filter for "show all
    // intents this grant approved" drilldown. Server-side WHERE clause
    // is faster than fetching the full inventory and filtering client-
    // side, and lets the limit param scope the response correctly.
    var sql = "SELECT id, domain, host, release_id, kind, expires_at, used_at, " +
              "confirmed_at, created_at, matched_grant_id " +
              "FROM deploy_intents " +
              (string.IsNullOrWhiteSpace(matchedGrantId)
                  ? ""
                  : "WHERE matched_grant_id = @MatchedGrantId ") +
              "ORDER BY created_at DESC LIMIT @Limit";
    var rows = await Dapper.SqlMapper.QueryAsync<dynamic>(conn, sql,
        new { Limit = Math.Clamp(limit, 1, 500), MatchedGrantId = matchedGrantId });
    // Phase 7.5+ — enrich the inventory with the registry-resolved label
    // + danger so the McpIntents page can render "Restore database
    // snapshot (destructive)" instead of bare "restore". Lookup is
    // O(1) per row against the in-memory registry.
    var entries = rows.Select(r =>
    {
        var kindId = (string)r.kind;
        var registered = kindsRegistry.Get(kindId);
        return new
        {
            intentId = (string)r.id,
            domain = (string)r.domain,
            host = (string)r.host,
            releaseId = (string?)r.release_id,
            kind = kindId,
            kindLabel = registered?.Label,
            kindDanger = registered?.Danger.ToString().ToLowerInvariant(),
            kindPluginId = registered?.PluginId,
            expiresAt = (string)r.expires_at,
            usedAt = (string?)r.used_at,
            confirmedAt = (string?)r.confirmed_at,
            createdAt = (string)r.created_at,
            // Phase 7.5+++ — audit trail: which grant auto-confirmed
            // this intent (NULL = manually confirmed via banner OR
            // allowUnconfirmed CI path).
            matchedGrantId = (string?)r.matched_grant_id,
            // Derived state for UI rendering convenience.
            state = ComputeIntentState(
                (string?)r.used_at,
                (string?)r.confirmed_at,
                (string)r.expires_at),
        };
    }).ToList();
    return Results.Ok(new { count = entries.Count, entries });
});

static string ComputeIntentState(string? usedAt, string? confirmedAt, string expiresAtRaw)
{
    if (!string.IsNullOrEmpty(usedAt)) return "consumed";
    if (DateTimeOffset.TryParse(expiresAtRaw, out var exp) && exp < DateTimeOffset.UtcNow)
        return "expired";
    if (string.IsNullOrEmpty(confirmedAt)) return "pending_confirmation";
    return "ready";
}

// Phase 7.4b — discover destructive op kinds plugins have registered.
// MCP clients call this to know what kinds they can include in
// /api/mcp/intents requests; the GUI shows it on the MCP Hub page so
// operators see "what AI can do here". Read-only; bearer auth on /api/*
// is sufficient gate.
app.MapGet("/api/mcp/kinds", async (
    HttpContext ctx,
    NKS.WebDevConsole.Core.Interfaces.IDestructiveOperationKinds kinds,
    NKS.WebDevConsole.Daemon.Data.Database db,
    CancellationToken ct) =>
{
    if (!IsMcpEnabled(ctx)) return Results.NotFound(new { error = "mcp_disabled" });
    // Phase 7.5+++ — usage telemetry per kind. Single GROUP BY query
    // tells operators which destructive ops AI is actually exercising
    // (deploy: 47, restore: 3, rollback: 0). Tolerates missing table
    // for fresh-DB compat.
    var usageByKind = new Dictionary<string, int>(StringComparer.Ordinal);
    try
    {
        using var conn = db.CreateConnection();
        await conn.OpenAsync(ct);
        var rows = await Dapper.SqlMapper.QueryAsync<(string Kind, int Count)>(conn,
            "SELECT kind AS Kind, COUNT(*) AS Count FROM deploy_intents GROUP BY kind");
        foreach (var (kind, count) in rows)
        {
            usageByKind[kind] = count;
        }
    }
    catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 1)
    {
        // Table doesn't exist (fresh DB before migration 006). All counts 0.
    }

    var list = kinds.List().Select(k => new
    {
        id = k.Id,
        label = k.Label,
        pluginId = k.PluginId,
        danger = k.Danger.ToString().ToLowerInvariant(),
        // Phase 7.5+++ — lifetime intent count for this kind. Includes
        // consumed + revoked + expired + still-pending; operators care
        // about historical use, not just live state.
        intentCount = usageByKind.TryGetValue(k.Id, out var c) ? c : 0,
    }).ToList();
    return Results.Ok(new { count = list.Count, entries = list });
});

// ============================================================================
// Phase 7.3 — MCP grants CRUD. The grants table powers persistent trust:
// "approve THIS session for 30 min", "always trust THIS API key", or coarse
// "always trust any AI on THIS daemon". Endpoints are gated by mcp.enabled
// (same as /api/mcp/intents) and the standard bearer auth on /api/*.
// ============================================================================

// List active grants — newest first. Used by GUI grants page + tests.
app.MapGet("/api/mcp/grants", async (
    HttpContext ctx,
    NKS.WebDevConsole.Core.Interfaces.IMcpSessionGrantsRepository grants,
    CancellationToken ct,
    bool? includeRevoked = null,
    int? page = null,
    int? pageSize = null) =>
{
    if (!IsMcpEnabled(ctx)) return Results.NotFound(new { error = "mcp_disabled" });
    // Phase 7.5+++ — opt-in audit view. Nullable + default null so the
    // minimal-API binder treats the param as truly optional (a non-
    // nullable `bool` would 400 when the query string is empty).
    var rows = includeRevoked == true
        ? await grants.ListAllAsync(ct)
        : await grants.ListActiveAsync(ct);
    var total = rows.Count;
    // Phase 7.5+++ — pagination on top of the in-memory list. Defaults
    // (page=1, pageSize=50) keep BC for callers that don't pass params.
    // Page 0 / negative is treated as 1; pageSize clamped to [1, 500]
    // to bound payload size.
    var p = Math.Max(1, page ?? 1);
    var ps = Math.Clamp(pageSize ?? 50, 1, 500);
    var skip = (p - 1) * ps;
    var paged = skip >= total
        ? new List<NKS.WebDevConsole.Core.Interfaces.McpSessionGrantRow>()
        : rows.Skip(skip).Take(ps).ToList();
    return Results.Ok(new
    {
        count = paged.Count,
        total,
        page = p,
        pageSize = ps,
        totalPages = (total + ps - 1) / ps,
        entries = paged,
    });
});

// Create a grant. Body shape:
// {
//   "scopeType":   "session" | "instance" | "api_key" | "always",
//   "scopeValue":  "<id>" | null (must be null when scopeType='always'),
//   "kindPattern": "*" or "deploy" | "rollback" | "cancel" | "restore",
//   "targetPattern":"*" or specific target (e.g. domain),
//   "expiresAt":   ISO-8601 UTC or null (null = permanent),
//   "note":        free-form, optional
// }
app.MapPost("/api/mcp/grants", async (
    HttpContext ctx,
    NKS.WebDevConsole.Core.Interfaces.IMcpSessionGrantsRepository grants,
    NKS.WebDevConsole.Core.Interfaces.IDeployEventBroadcaster eventsBus,
    CancellationToken ct) =>
{
    if (!IsMcpEnabled(ctx)) return Results.NotFound(new { error = "mcp_disabled" });

    GrantCreateBody? body;
    try { body = await ctx.Request.ReadFromJsonAsync<GrantCreateBody>(ct); }
    catch { return Results.BadRequest(new { error = "invalid_json" }); }
    if (body is null) return Results.BadRequest(new { error = "missing_body" });

    var allowedScopes = new[] { "session", "instance", "api_key", "always" };
    if (string.IsNullOrEmpty(body.ScopeType) || !allowedScopes.Contains(body.ScopeType))
        return Results.BadRequest(new { error = "invalid_scope_type", allowed = allowedScopes });
    if (body.ScopeType == "always")
    {
        if (!string.IsNullOrEmpty(body.ScopeValue))
            return Results.BadRequest(new { error = "scope_value_must_be_null_for_always" });
    }
    else if (string.IsNullOrEmpty(body.ScopeValue))
    {
        return Results.BadRequest(new { error = "scope_value_required" });
    }

    var row = new NKS.WebDevConsole.Core.Interfaces.McpSessionGrantRow(
        Id: null,
        ScopeType: body.ScopeType,
        ScopeValue: body.ScopeType == "always" ? null : body.ScopeValue,
        KindPattern: string.IsNullOrEmpty(body.KindPattern) ? "*" : body.KindPattern,
        TargetPattern: string.IsNullOrEmpty(body.TargetPattern) ? "*" : body.TargetPattern,
        GrantedAt: "",
        ExpiresAt: body.ExpiresAt,
        GrantedBy: string.IsNullOrEmpty(body.GrantedBy) ? "gui" : body.GrantedBy,
        RevokedAt: null,
        Note: body.Note,
        // Phase 7.5+++ — optional rate limit. Math.Max in repo clamps negatives.
        MinCooldownSeconds: body.MinCooldownSeconds ?? 0);

    var id = await grants.InsertAsync(row, ct);
    // Phase 7.5+++ — broadcast lifecycle event so any open McpHub Grants
    // tab refreshes its list without operator F5. Best-effort; failure
    // doesn't roll back the grant.
    try
    {
        await eventsBus.BroadcastAsync("mcp:grant-changed", new
        {
            change = "created",
            id,
            scopeType = body.ScopeType,
            kindPattern = row.KindPattern,
            targetPattern = row.TargetPattern,
        });
    }
    catch { /* SSE best-effort */ }
    return Results.Ok(new { id, status = "created" });
});

// Revoke (soft-delete) a grant by id.
app.MapDelete("/api/mcp/grants/{id}", async (
    string id,
    HttpContext ctx,
    NKS.WebDevConsole.Core.Interfaces.IMcpSessionGrantsRepository grants,
    NKS.WebDevConsole.Core.Interfaces.IDeployEventBroadcaster eventsBus,
    CancellationToken ct) =>
{
    if (!IsMcpEnabled(ctx)) return Results.NotFound(new { error = "mcp_disabled" });
    var ok = await grants.RevokeAsync(id, ct);
    if (!ok)
    {
        return Results.NotFound(new { error = "grant_not_found_or_already_revoked", id });
    }
    try { await eventsBus.BroadcastAsync("mcp:grant-changed", new { change = "revoked", id }); }
    catch { /* SSE best-effort */ }
    return Results.Ok(new { id, status = "revoked" });
});

// Phase 7.5+++ — aggregate grant statistics. Single round-trip that
// the McpHub uses to render rich badges + the Settings page can show
// as a snapshot card. Server-side aggregation keeps the GUI fast even
// when the grants table grows beyond the 200-row default page size.
//
// Returned shape:
//   {
//     "total": int,            // all rows (active + revoked, not swept)
//     "active": int,           // revoked_at IS NULL AND not yet expired
//     "deadweight": int,       // active AND match_count=0 AND age >7d
//     "totalMatches": long,    // sum(match_count) across all rows
//     "lastMatchAt": ISO?      // max(last_matched_at), null if never
//   }
app.MapGet("/api/mcp/grants/stats", async (
    HttpContext ctx,
    NKS.WebDevConsole.Daemon.Data.Database db,
    CancellationToken ct) =>
{
    if (!IsMcpEnabled(ctx)) return Results.NotFound(new { error = "mcp_disabled" });
    using var conn = db.CreateConnection();
    await conn.OpenAsync(ct);
    try
    {
        var deadweightCutoff = DateTimeOffset.UtcNow.AddDays(-7).ToString("o");
        var stats = await Dapper.SqlMapper.QuerySingleAsync<GrantStatsRow>(conn,
            "SELECT " +
            "  COUNT(*) AS Total, " +
            "  SUM(CASE WHEN revoked_at IS NULL AND " +
            "           (expires_at IS NULL OR expires_at > strftime('%Y-%m-%dT%H:%M:%fZ','now')) " +
            "           THEN 1 ELSE 0 END) AS Active, " +
            "  SUM(CASE WHEN revoked_at IS NULL AND " +
            "           (expires_at IS NULL OR expires_at > strftime('%Y-%m-%dT%H:%M:%fZ','now')) AND " +
            "           match_count = 0 AND granted_at < @Cutoff " +
            "           THEN 1 ELSE 0 END) AS Deadweight, " +
            "  COALESCE(SUM(match_count), 0) AS TotalMatches, " +
            "  MAX(last_matched_at) AS LastMatchAt " +
            "FROM mcp_session_grants",
            new { Cutoff = deadweightCutoff });
        return Results.Ok(new
        {
            total = stats.Total,
            active = stats.Active,
            deadweight = stats.Deadweight,
            totalMatches = stats.TotalMatches,
            lastMatchAt = stats.LastMatchAt,
        });
    }
    catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 1)
    {
        // Table doesn't exist (fresh DB before migration 012/014). Return
        // zeros so the GUI renders gracefully rather than choking on 500.
        return Results.Ok(new
        {
            total = 0, active = 0, deadweight = 0,
            totalMatches = 0L, lastMatchAt = (string?)null,
        });
    }
});

// Phase 7.5+++ — dry-run grant match. Operator can ask "would a caller
// with this identity tuple firing this kind+target match an existing
// active grant?" WITHOUT actually creating an intent or auto-firing
// anything. Mirrors the validator's pre-check semantics 1:1 by going
// through the same `FindMatchingActiveAsync` path.
//
// Body: { sessionId?: string, instanceId?: string, apiKeyId?: string,
//         kind: string, target: string }
// Returns: { matched: bool, grant?: { id, scopeType, scopeValue,
//            kindPattern, targetPattern, matchCount, lastMatchedAt } }
//
// Use cases: debugging "why isn't my grant matching?" without firing
// destructive ops; pre-flight checks from the MCP CLI; admin auditing.
app.MapPost("/api/mcp/grants/test-match", async (
    HttpContext ctx,
    NKS.WebDevConsole.Core.Interfaces.IMcpSessionGrantsRepository grants,
    CancellationToken ct) =>
{
    if (!IsMcpEnabled(ctx)) return Results.NotFound(new { error = "mcp_disabled" });
    using var doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
    var root = doc.RootElement;
    var sessionId  = root.TryGetProperty("sessionId",  out var sEl) ? sEl.GetString() : null;
    var instanceId = root.TryGetProperty("instanceId", out var iEl) ? iEl.GetString() : null;
    var apiKeyId   = root.TryGetProperty("apiKeyId",   out var aEl) ? aEl.GetString() : null;
    var kind       = root.TryGetProperty("kind",       out var kEl) ? kEl.GetString() : null;
    var target     = root.TryGetProperty("target",     out var tEl) ? tEl.GetString() : null;
    if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(target))
        return Results.BadRequest(new { error = "kind and target are required" });

    var grant = await grants.FindMatchingActiveAsync(
        sessionId, instanceId, apiKeyId, kind!, target!, ct);
    if (grant is null)
    {
        return Results.Ok(new { matched = false });
    }
    // NOTE: this is a dry-run — do NOT call RecordMatchAsync. The
    // telemetry counters reflect REAL auto-confirms, not test queries.
    return Results.Ok(new
    {
        matched = true,
        grant = new
        {
            id            = grant.Id,
            scopeType     = grant.ScopeType,
            scopeValue    = grant.ScopeValue,
            kindPattern   = grant.KindPattern,
            targetPattern = grant.TargetPattern,
            matchCount    = grant.MatchCount,
            lastMatchedAt = grant.LastMatchedAt,
        },
    });
});

// Phase 7.5+++ — manual sweep trigger. Operator can fire the grant
// janitor on demand without waiting for the 15-minute background tick.
// Reuses the same SQL helper the BackgroundService uses; broadcasts
// mcp:grant-changed{change:swept} on success so the GUI table updates.
app.MapPost("/api/mcp/grants/sweep-now", async (
    HttpContext ctx,
    NKS.WebDevConsole.Daemon.Data.Database db,
    NKS.WebDevConsole.Core.Interfaces.IDeployEventBroadcaster eventsBus,
    SettingsStore settings,
    CancellationToken ct) =>
{
    if (!IsMcpEnabled(ctx)) return Results.NotFound(new { error = "mcp_disabled" });
    using var conn = db.CreateConnection();
    await conn.OpenAsync(ct);
    // Read the same operator-tunable retention the background janitor uses
    // so the manual button matches the timer's behaviour exactly.
    var expiredDays = Math.Max(0, settings.GetInt(
        "mcp", "grant_expired_retention_days",
        NKS.WebDevConsole.Daemon.Mcp.GrantSweeperService.DefaultExpiredRetentionDays));
    var revokedDays = Math.Max(0, settings.GetInt(
        "mcp", "grant_revoked_retention_days",
        NKS.WebDevConsole.Daemon.Mcp.GrantSweeperService.DefaultRevokedRetentionDays));
    var deleted = await NKS.WebDevConsole.Daemon.Mcp.GrantSweeperService.SweepAsync(
        conn, DateTimeOffset.UtcNow,
        TimeSpan.FromDays(expiredDays), TimeSpan.FromDays(revokedDays), ct);
    if (deleted > 0)
    {
        try
        {
            await eventsBus.BroadcastAsync("mcp:grant-changed",
                new { change = "swept", count = deleted });
        }
        catch { /* SSE best-effort */ }
    }
    return Results.Ok(new { deleted });
});

// Phase 7.5+++ — partial update of an existing grant. Only mutable
// operator-tunable fields (cooldown, expiresAt, note) — identity and
// telemetry are immutable. Body shape:
//   { "minCooldownSeconds": 60?,
//     "expiresAt": "2026-05-01T00:00:00Z" | null,  ← null = make permanent
//     "note": "updated reason" }
// Any field omitted = leave unchanged. Returns 200 with id, 404 if not
// found, 400 if body has nothing to change.
app.MapPatch("/api/mcp/grants/{id}", async (
    string id,
    HttpContext ctx,
    NKS.WebDevConsole.Core.Interfaces.IMcpSessionGrantsRepository grants,
    NKS.WebDevConsole.Core.Interfaces.IDeployEventBroadcaster eventsBus,
    CancellationToken ct) =>
{
    if (!IsMcpEnabled(ctx)) return Results.NotFound(new { error = "mcp_disabled" });
    System.Text.Json.JsonDocument doc;
    try { doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct); }
    catch { return Results.BadRequest(new { error = "Invalid JSON body" }); }
    using var _ = doc;
    var root = doc.RootElement;

    int? cooldown = null;
    string? expiresAt = null;
    string? note = null;
    if (root.TryGetProperty("minCooldownSeconds", out var cdEl) && cdEl.ValueKind == System.Text.Json.JsonValueKind.Number)
        cooldown = cdEl.GetInt32();
    if (root.TryGetProperty("expiresAt", out var exEl))
    {
        // Distinguish "absent" vs "null" vs "string". Null in JSON → set
        // permanent (sentinel "__null__"); string → use as-is.
        if (exEl.ValueKind == System.Text.Json.JsonValueKind.Null) expiresAt = "__null__";
        else if (exEl.ValueKind == System.Text.Json.JsonValueKind.String) expiresAt = exEl.GetString();
    }
    if (root.TryGetProperty("note", out var noteEl) && noteEl.ValueKind == System.Text.Json.JsonValueKind.String)
        note = noteEl.GetString();

    if (cooldown is null && expiresAt is null && note is null)
        return Results.BadRequest(new { error = "no_mutable_fields", hint = "send minCooldownSeconds, expiresAt, or note" });

    var ok = await grants.UpdateMutableAsync(id, cooldown, expiresAt, note, ct);
    if (!ok) return Results.NotFound(new { error = "grant_not_found", id });

    try
    {
        await eventsBus.BroadcastAsync("mcp:grant-changed", new { change = "updated", id });
    }
    catch { /* SSE best-effort */ }
    return Results.Ok(new { id, status = "updated" });
});

// Phase 7.5+++ — bulk import grants from a previously-exported envelope.
// Payload shape (matches the GUI export):
//   { "formatVersion": 1, "entries": [ { id?, scopeType, scopeValue?,
//     kindPattern, targetPattern, expiresAt?, grantedBy?, note? }, … ] }
//
// Strategy: skip rows whose `id` already exists (idempotent re-import
// of the same backup). Rows without an id get a fresh UUID. Validation
// is delegated to the existing INSERT path's CHECK constraints — bad
// scope_type values blow up per-row and land in the errors[] array
// without aborting the whole batch.
//
// Returns: { imported, skipped, errors: [{index, error}] }
app.MapPost("/api/mcp/grants/import", async (
    HttpContext ctx,
    NKS.WebDevConsole.Core.Interfaces.IMcpSessionGrantsRepository grants,
    NKS.WebDevConsole.Core.Interfaces.IDeployEventBroadcaster eventsBus,
    NKS.WebDevConsole.Daemon.Data.Database db,
    CancellationToken ct) =>
{
    if (!IsMcpEnabled(ctx)) return Results.NotFound(new { error = "mcp_disabled" });
    System.Text.Json.JsonDocument doc;
    try { doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct); }
    catch { return Results.BadRequest(new { error = "Invalid JSON body" }); }
    using var _ = doc;
    var root = doc.RootElement;
    if (!root.TryGetProperty("formatVersion", out var fv) || fv.ValueKind != System.Text.Json.JsonValueKind.Number || fv.GetInt32() != 1)
        return Results.BadRequest(new { error = "formatVersion must be 1" });
    if (!root.TryGetProperty("entries", out var entries) || entries.ValueKind != System.Text.Json.JsonValueKind.Array)
        return Results.BadRequest(new { error = "entries must be an array" });

    // Pre-load existing ids in one shot so dup detection is O(1).
    using var conn = db.CreateConnection();
    await conn.OpenAsync(ct);
    var existing = (await Dapper.SqlMapper.QueryAsync<string>(conn,
        "SELECT id FROM mcp_session_grants")).ToHashSet(StringComparer.Ordinal);

    int imported = 0, skipped = 0;
    var errors = new List<object>();
    int idx = -1;
    foreach (var e in entries.EnumerateArray())
    {
        idx++;
        try
        {
            var id = e.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (!string.IsNullOrEmpty(id) && existing.Contains(id))
            {
                skipped++;
                continue;
            }
            var row = new NKS.WebDevConsole.Core.Interfaces.McpSessionGrantRow(
                Id: id,
                ScopeType: e.GetProperty("scopeType").GetString() ?? "session",
                ScopeValue: e.TryGetProperty("scopeValue", out var sv) ? sv.GetString() : null,
                KindPattern: e.TryGetProperty("kindPattern", out var kp) ? (kp.GetString() ?? "*") : "*",
                TargetPattern: e.TryGetProperty("targetPattern", out var tp) ? (tp.GetString() ?? "*") : "*",
                GrantedAt: e.TryGetProperty("grantedAt", out var ga) ? (ga.GetString() ?? "") : "",
                ExpiresAt: e.TryGetProperty("expiresAt", out var ea) ? ea.GetString() : null,
                GrantedBy: e.TryGetProperty("grantedBy", out var gb) ? (gb.GetString() ?? "import") : "import",
                RevokedAt: null, // imported grants always start active; ignore source revoked_at
                Note: e.TryGetProperty("note", out var note) ? note.GetString() : null);
            await grants.InsertAsync(row, ct);
            imported++;
        }
        catch (Exception ex)
        {
            errors.Add(new { index = idx, error = ex.Message });
        }
    }

    if (imported > 0)
    {
        try
        {
            await eventsBus.BroadcastAsync("mcp:grant-changed",
                new { change = "imported", count = imported });
        }
        catch { /* SSE best-effort */ }
    }

    return Results.Ok(new { imported, skipped, errors });
});

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
app.MapPost("/api/nks.wdc.deploy/test-host-connection", async (
    HttpContext ctx,
    CancellationToken ct) =>
{
    if (!IsDeployEnabled(ctx)) return Results.NotFound(new { error = "deploy_disabled" });
    using var doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
    var root = doc.RootElement;
    var host = root.TryGetProperty("host", out var hEl) ? hEl.GetString() : null;
    var port = root.TryGetProperty("port", out var pEl) && pEl.TryGetInt32(out var p) ? p : 22;

    if (string.IsNullOrWhiteSpace(host))
        return Results.BadRequest(new { error = "host is required" });
    if (port < 1 || port > 65535)
        return Results.BadRequest(new { error = "port must be in [1, 65535]" });

    var sw = System.Diagnostics.Stopwatch.StartNew();
    using var probe = new System.Net.Sockets.TcpClient();
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    cts.CancelAfter(TimeSpan.FromSeconds(5));
    try
    {
        await probe.ConnectAsync(host!, port, cts.Token);
        sw.Stop();
        return Results.Ok(new { ok = true, latencyMs = sw.ElapsedMilliseconds });
    }
    catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
    {
        sw.Stop();
        return Results.Ok(new
        {
            ok = false, code = "timeout",
            error = $"TCP probe to {host}:{port} timed out after 5s",
        });
    }
    catch (System.Net.Sockets.SocketException ex)
    {
        sw.Stop();
        return Results.Ok(new
        {
            ok = false, code = "socket_error",
            error = $"{host}:{port} unreachable: {ex.Message}",
        });
    }
    catch (Exception ex)
    {
        sw.Stop();
        return Results.Ok(new
        {
            ok = false, code = "unexpected",
            error = $"Probe failed: {ex.Message}",
        });
    }
});

// + DeploySiteTab's hasConfig probe (returns 404→empty when zero rows
// would be returned, so frontend keeps showing the wizard CTA).
app.MapGet("/api/nks.wdc.deploy/sites/{domain}/history", async (
    string domain,
    int? limit,
    string? triggeredBy,
    NKS.WebDevConsole.Core.Interfaces.IDeployRunsRepository runs,
    CancellationToken ct) =>
{
    var rows = await runs.ListForDomainAsync(domain, limit ?? 50, ct);
    // Phase 7.5+++ — optional triggeredBy filter (gui|mcp|cli|other).
    // In-memory filter is fine since the rowcount is already capped by
    // the limit param (default 50). Empty string = no filter applied.
    if (!string.IsNullOrWhiteSpace(triggeredBy))
    {
        rows = rows.Where(r => string.Equals(r.TriggeredBy, triggeredBy,
            StringComparison.OrdinalIgnoreCase)).ToList();
    }
    var entries = rows.Select(r => new
    {
        deployId   = r.Id,
        domain     = r.Domain,
        host       = r.Host,
        branch     = r.Branch ?? "",
        finalPhase = MapStatusToPhase(r.Status),
        startedAt  = r.StartedAt.ToString("o"),
        completedAt = r.CompletedAt?.ToString("o"),
        commitSha  = r.CommitSha,
        releaseId  = r.ReleaseId,
        error      = r.ErrorMessage,
        // Phase 7.5+++ — surface trigger source so operators can audit
        // which deploys came from AI/MCP vs GUI vs CI/CLI.
        triggeredBy = r.TriggeredBy,
    }).ToList();
    return Results.Ok(new { domain, count = entries.Count, entries });
});

// Snapshot list — pre-deploy DB snapshots that ran for this site.
// Composed from deploy_runs rows with non-null pre_deploy_backup_path.
// Real backend (when it ships) writes the snapshot path + size via
// IDeployRunsRepository.UpdatePreDeployBackupAsync mid-run; this view
// just projects those rows into the frontend's DeploySnapshotEntry shape.
app.MapGet("/api/nks.wdc.deploy/sites/{domain}/snapshots", async (
    string domain,
    NKS.WebDevConsole.Core.Interfaces.IDeployRunsRepository runs,
    CancellationToken ct) =>
{
    var rows = await runs.ListForDomainAsync(domain, limit: 200, ct);
    var entries = rows
        .Where(r => !string.IsNullOrEmpty(r.PreDeployBackupPath))
        .Select(r => new
        {
            id          = r.Id,
            createdAt   = r.StartedAt.ToString("o"),
            sizeBytes   = r.PreDeployBackupSizeBytes ?? 0,
            path        = r.PreDeployBackupPath!,
        })
        .ToList();
    return Results.Ok(new { domain, count = entries.Count, entries });
});

// Deploy settings persistence — JSON file under
// {WdcPaths.DataRoot}/deploy-settings/{domain}.json. Frontend's
// DeploySettingsPanel writes here when operator clicks Save in any tab.
// Setup wizard's Finish button stores its first-host config here too,
// which transitions the site from "wizard CTA" empty state to the full
// command center on next page load (DeploySiteTab.refreshAll() now
// has a hasConfig truthy signal).
//
// File-per-site keeps the schema dumb: we serialise the body the
// frontend posts verbatim (Phase 7.5 stub). When the real backend ships
// it can validate against a schema before persist.
static string DeploySettingsPath(string domain)
{
    var dir = Path.Combine(NKS.WebDevConsole.Core.Services.WdcPaths.DataRoot, "deploy-settings");
    Directory.CreateDirectory(dir);
    return Path.Combine(dir,
        NKS.WebDevConsole.Daemon.Deploy.DeployRestHelpers.SanitiseDomainForFilename(domain) + ".json");
}

// Phase 7.5+++ — read settings.snapshot.retentionDays for a domain.
// Returns null when settings are absent / malformed so callers can
// fall back to a sensible default. Best-effort — never throws.
static int? ReadSnapshotRetentionDays(string domain)
{
    try
    {
        var sp = DeploySettingsPath(domain);
        if (!File.Exists(sp)) return null;
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(sp));
        if (doc.RootElement.TryGetProperty("snapshot", out var sEl)
            && sEl.TryGetProperty("retentionDays", out var rdEl)
            && rdEl.TryGetInt32(out var rd) && rd > 0)
            return rd;
    }
    catch { /* best-effort */ }
    return null;
}

// Phase 7.5+++ — purge snapshot zips older than retentionDays in the
// given backups subfolder ("manual" or "pre-deploy"). Glob-and-delete
// based on file mtime. Called at snapshot creation moments so no
// separate scheduler is needed; the zip dir stays bounded by the
// operator's own snapshot cadence + retention setting.
static int PurgeOldSnapshots(string subfolder, string domain, int retentionDays)
{
    if (retentionDays <= 0) return 0;
    try
    {
        var dir = Path.Combine(NKS.WebDevConsole.Core.Services.WdcPaths.BackupsRoot, subfolder, domain);
        if (!Directory.Exists(dir)) return 0;
        var cutoff = DateTime.UtcNow - TimeSpan.FromDays(retentionDays);
        var purged = 0;
        foreach (var f in Directory.EnumerateFiles(dir))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(f) < cutoff)
                {
                    File.Delete(f);
                    purged++;
                }
            }
            catch { /* skip locked / permission denied */ }
        }
        return purged;
    }
    catch { return 0; }
}

app.MapGet("/api/nks.wdc.deploy/sites/{domain}/settings", (string domain) =>
{
    var path = DeploySettingsPath(domain);
    if (!File.Exists(path))
    {
        // 404 lets the frontend fall back to defaultDeploySettings() —
        // keeps existing behaviour from when this endpoint didn't exist.
        return Results.NotFound(new { error = "no_settings_yet", domain });
    }
    try
    {
        var json = File.ReadAllText(path);
        // Stream the raw JSON back rather than re-deserialising —
        // frontend's DeploySettings shape is what we wrote, what we
        // read should round-trip byte-equivalent.
        return Results.Content(json, "application/json");
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = "read_failed", message = ex.Message }, statusCode: 500);
    }
});

app.MapPut("/api/nks.wdc.deploy/sites/{domain}/settings", async (
    string domain, HttpContext ctx, CancellationToken ct) =>
{
    // Read and validate body is JSON-shaped — anything past that is the
    // frontend's contract; we don't enforce per-field rules here so a new
    // setting can land without daemon restart.
    string body;
    using (var reader = new StreamReader(ctx.Request.Body))
        body = await reader.ReadToEndAsync(ct);
    try { System.Text.Json.JsonDocument.Parse(body); }
    catch { return Results.BadRequest(new { error = "invalid_json" }); }

    var path = DeploySettingsPath(domain);
    // Atomic write: temp file in same dir + File.Move with overwrite.
    // Avoids leaving a half-written file if the daemon crashes mid-flush.
    var tmp = path + ".tmp";
    await File.WriteAllTextAsync(tmp, body, ct);
    // File.Move on Windows pre-.NET 5 errored on overwrite; current .NET
    // overload accepts overwrite=true safely.
    if (File.Exists(path)) File.Delete(path);
    File.Move(tmp, path);
    return Results.Ok(new { domain, status = "saved", bytes = body.Length });
});

// Single deploy status — used by the drawer's status polling fallback.
app.MapGet("/api/nks.wdc.deploy/sites/{domain}/deploys/{deployId}", async (
    string domain,
    string deployId,
    NKS.WebDevConsole.Core.Interfaces.IDeployRunsRepository runs,
    CancellationToken ct) =>
{
    var row = await runs.GetByIdAsync(deployId, ct);
    if (row is null || !string.Equals(row.Domain, domain, StringComparison.OrdinalIgnoreCase))
        return Results.NotFound(new { error = "deploy_not_found", deployId });
    return Results.Ok(new
    {
        deployId   = row.Id,
        domain     = row.Domain,
        host       = row.Host,
        finalPhase = MapStatusToPhase(row.Status),
        startedAt  = row.StartedAt.ToString("o"),
        completedAt = row.CompletedAt?.ToString("o"),
        commitSha  = row.CommitSha,
        releaseId  = row.ReleaseId,
        error      = row.ErrorMessage,
        success    = row.Status == "completed",
    });
});

// Phase 7.5+ — rollback a deploy. POST /sites/{domain}/deploys/{deployId}/rollback.
// Real local-loopback rollback: when host has localTargetPath configured AND
// {target}/.dep/previous_release exists, atomically swap `current` symlink
// back to the path stored in previous_release. Otherwise the call still
// records a rollback row in the DB so the audit log stays accurate, but
// the filesystem state isn't touched (no localPaths configured).
app.MapPost("/api/nks.wdc.deploy/sites/{domain}/deploys/{deployId}/rollback", async (
    string domain, string deployId, HttpContext rbCtx,
    NKS.WebDevConsole.Core.Interfaces.IDeployRunsRepository runs,
    NKS.WebDevConsole.Core.Interfaces.IDeployIntentValidator intentValidator,
    NKS.WebDevConsole.Core.Interfaces.IDeployEventBroadcaster eventsBus,
    CancellationToken ct) =>
{
    // Phase 7.5+++ — optional MCP intent gate. Validate BEFORE the
    // not-found check so a bogus token can't be probed against arbitrary
    // deployIds to learn which exist (token validity 403 vs deploy
    // existence 404 would otherwise leak that signal to an attacker).
    // When X-Intent-Token header is present, validator enforces
    // kind=rollback + scope match. Without a token the endpoint stays
    // open (back-compat with GUI flows that don't request a token).
    // Host scope is validated as wildcard "*" since we don't yet know
    // the source row's host before the not-found check runs.
    var rbIntentToken = rbCtx.Request.Headers["X-Intent-Token"].FirstOrDefault();
    if (!string.IsNullOrEmpty(rbIntentToken))
    {
        var rbAllowUnconfirmed = string.Equals(
            rbCtx.Request.Headers["X-Allow-Unconfirmed"].FirstOrDefault(), "true",
            StringComparison.OrdinalIgnoreCase);
        var verdict = await intentValidator.ValidateAndConsumeAsync(
            rbIntentToken, "rollback", domain, "*", rbAllowUnconfirmed, ct);
        if (!verdict.Ok)
            return Results.Json(new { error = "intent_rejected", reason = verdict.Reason },
                statusCode: verdict.Reason == "pending_confirmation" ? 425 : 403);
    }

    var source = await runs.GetByIdAsync(deployId, ct);
    if (source is null || !string.Equals(source.Domain, domain, StringComparison.OrdinalIgnoreCase))
        return Results.NotFound(new { error = "deploy_not_found", deployId });

    // Resolve the local target for this host so we can perform a real
    // symlink swap. Mirrors the deploy endpoint's settings lookup.
    string? targetPath = null;
    try
    {
        var settingsPath = DeploySettingsPath(domain);
        if (File.Exists(settingsPath))
        {
            using var sdoc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath, ct));
            if (sdoc.RootElement.TryGetProperty("hosts", out var hostsEl)
                && hostsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var hEl in hostsEl.EnumerateArray())
                {
                    if (!hEl.TryGetProperty("name", out var nEl)) continue;
                    if (!string.Equals(nEl.GetString(), source.Host, StringComparison.OrdinalIgnoreCase)) continue;
                    if (hEl.TryGetProperty("localTargetPath", out var ltEl))
                        targetPath = ltEl.GetString();
                    break;
                }
            }
        }
    }
    catch { /* best-effort */ }

    string? swappedTo = null;
    string? rollbackError = null;
    if (!string.IsNullOrEmpty(targetPath))
    {
        var depPrev = Path.Combine(targetPath, ".dep", "previous_release");
        var currentLink = Path.Combine(targetPath, "current");
        if (File.Exists(depPrev))
        {
            try
            {
                var prevRelease = (await File.ReadAllTextAsync(depPrev, ct)).Trim();
                if (!string.IsNullOrEmpty(prevRelease) && Directory.Exists(prevRelease))
                {
                    // Remove existing current link/dir, then recreate
                    if (Directory.Exists(currentLink))
                    {
                        var fi = new DirectoryInfo(currentLink);
                        if (fi.LinkTarget is not null) Directory.Delete(currentLink);
                        else Directory.Delete(currentLink, recursive: true);
                    }
                    Directory.CreateSymbolicLink(currentLink, prevRelease);
                    swappedTo = prevRelease;

                    // Rotate .dep state — current_release points to prev,
                    // and previous_release becomes the deploy we rolled back FROM
                    // so a subsequent rollback returns to the more-recent release.
                    var depCurrent = Path.Combine(targetPath, ".dep", "current_release");
                    var oldCurrent = File.Exists(depCurrent)
                        ? (await File.ReadAllTextAsync(depCurrent, ct)).Trim()
                        : string.Empty;
                    await File.WriteAllTextAsync(depCurrent, prevRelease, ct);
                    if (!string.IsNullOrEmpty(oldCurrent))
                        await File.WriteAllTextAsync(depPrev, oldCurrent, ct);
                }
                else
                {
                    rollbackError = "previous_release path missing or empty";
                }
            }
            catch (Exception ex) { rollbackError = ex.Message; }
        }
        else
        {
            rollbackError = ".dep/previous_release file not found — nothing to roll back to";
        }
    }

    var rollbackId = Guid.NewGuid().ToString("D");
    var now = DateTimeOffset.UtcNow;
    await runs.InsertAsync(new NKS.WebDevConsole.Core.Interfaces.DeployRunRow(
        Id: rollbackId, Domain: domain, Host: source.Host,
        ReleaseId: now.ToString("yyyyMMdd_HHmmss") + "-rollback-of-" + deployId[..8],
        Branch: source.Branch, CommitSha: source.CommitSha,
        Status: rollbackError is null ? "completed" : "failed",
        IsPastPonr: true,
        StartedAt: now, CompletedAt: now,
        ExitCode: rollbackError is null ? 0 : -1,
        ErrorMessage: rollbackError, DurationMs: 50,
        TriggeredBy: "gui",
        BackendId: swappedTo is not null ? "local-rollback" : "noop-rollback",
        CreatedAt: now, UpdatedAt: now), ct);
    // Mark the source as rolled-back so the UI tag flips.
    await runs.UpdateStatusAsync(deployId, "rolled_back", ct);

    await eventsBus.BroadcastAsync("deploy:complete", new
    {
        deployId = rollbackId,
        success = rollbackError is null,
        sourceDeployId = deployId,
        kind = "rollback",
        swappedTo,
        error = rollbackError,
    });
    return Results.Ok(new
    {
        sourceDeployId = deployId,
        status = rollbackError is null ? "rolled_back" : "rollback_failed",
        swappedTo,
        error = rollbackError,
    });
});

// Phase 7.5+++ — rollback to a SPECIFIC historical release. Useful when
// previous_release is itself broken (operator picks an earlier known-good
// release from the Releases tab). Body: { host, releaseId }. Looks up
// the host's localTargetPath, verifies releases/{releaseId} exists, then
// performs the same atomic symlink swap + .dep rotation as the deploy-id
// rollback path.
app.MapPost("/api/nks.wdc.deploy/sites/{domain}/rollback-to", async (
    string domain, HttpContext ctx,
    NKS.WebDevConsole.Core.Interfaces.IDeployRunsRepository runs,
    NKS.WebDevConsole.Core.Interfaces.IDeployIntentValidator intentValidator,
    NKS.WebDevConsole.Core.Interfaces.IDeployEventBroadcaster eventsBus,
    CancellationToken ct) =>
{
    using var doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
    var root = doc.RootElement;
    var host = root.TryGetProperty("host", out var hEl) ? hEl.GetString() : null;
    var releaseId = root.TryGetProperty("releaseId", out var rEl) ? rEl.GetString() : null;
    if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(releaseId))
        return Results.BadRequest(new { error = "host_and_releaseId_required" });

    // Phase 7.5+++ — optional MCP intent gate. Same shape as the deploy-id
    // rollback endpoint above. Token may also be provided in body for
    // clients that can't set custom headers (older HTTP libs).
    var rtIntentToken = ctx.Request.Headers["X-Intent-Token"].FirstOrDefault();
    if (string.IsNullOrEmpty(rtIntentToken)
        && root.TryGetProperty("intentToken", out var rtTokenEl))
        rtIntentToken = rtTokenEl.GetString();
    if (!string.IsNullOrEmpty(rtIntentToken))
    {
        var rtAllowUnconfirmed = string.Equals(
            ctx.Request.Headers["X-Allow-Unconfirmed"].FirstOrDefault(), "true",
            StringComparison.OrdinalIgnoreCase);
        var verdict = await intentValidator.ValidateAndConsumeAsync(
            rtIntentToken, "rollback", domain, host, rtAllowUnconfirmed, ct);
        if (!verdict.Ok)
            return Results.Json(new { error = "intent_rejected", reason = verdict.Reason },
                statusCode: verdict.Reason == "pending_confirmation" ? 425 : 403);
    }

    // Resolve target path from settings — same lookup as the deploy and
    // rollback endpoints so behaviour stays consistent.
    string? targetPath = null;
    try
    {
        var settingsPath = DeploySettingsPath(domain);
        if (File.Exists(settingsPath))
        {
            using var sdoc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath, ct));
            if (sdoc.RootElement.TryGetProperty("hosts", out var hostsEl)
                && hostsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var hEl2 in hostsEl.EnumerateArray())
                {
                    if (!hEl2.TryGetProperty("name", out var nEl)) continue;
                    if (!string.Equals(nEl.GetString(), host, StringComparison.OrdinalIgnoreCase)) continue;
                    if (hEl2.TryGetProperty("localTargetPath", out var ltEl))
                        targetPath = ltEl.GetString();
                    break;
                }
            }
        }
    }
    catch { /* best-effort */ }

    if (string.IsNullOrEmpty(targetPath))
        return Results.BadRequest(new { error = "no_local_target_configured", host });

    var releaseDir = Path.Combine(targetPath, "releases", releaseId);
    if (!Directory.Exists(releaseDir))
        return Results.NotFound(new { error = "release_not_found", releaseId, host });

    var currentLink = Path.Combine(targetPath, "current");
    var depDir = Path.Combine(targetPath, ".dep");
    Directory.CreateDirectory(depDir);
    var depCurrent = Path.Combine(depDir, "current_release");
    var depPrev = Path.Combine(depDir, "previous_release");

    string? oldCurrent = null;
    string? error = null;
    try
    {
        if (File.Exists(depCurrent))
            oldCurrent = (await File.ReadAllTextAsync(depCurrent, ct)).Trim();

        if (Directory.Exists(currentLink))
        {
            var fi = new DirectoryInfo(currentLink);
            if (fi.LinkTarget is not null) Directory.Delete(currentLink);
            else Directory.Delete(currentLink, recursive: true);
        }
        Directory.CreateSymbolicLink(currentLink, releaseDir);
        await File.WriteAllTextAsync(depCurrent, releaseDir, ct);
        if (!string.IsNullOrEmpty(oldCurrent) && oldCurrent != releaseDir)
            await File.WriteAllTextAsync(depPrev, oldCurrent, ct);
    }
    catch (Exception ex) { error = ex.Message; }

    var rollbackId = Guid.NewGuid().ToString("D");
    var now = DateTimeOffset.UtcNow;
    await runs.InsertAsync(new NKS.WebDevConsole.Core.Interfaces.DeployRunRow(
        Id: rollbackId, Domain: domain, Host: host,
        ReleaseId: now.ToString("yyyyMMdd_HHmmss") + "-rollback-to-" + releaseId,
        Branch: null, CommitSha: null,
        Status: error is null ? "completed" : "failed",
        IsPastPonr: error is null,
        StartedAt: now, CompletedAt: now,
        ExitCode: error is null ? 0 : -1,
        ErrorMessage: error, DurationMs: 50,
        TriggeredBy: "gui", BackendId: "local-rollback-to",
        CreatedAt: now, UpdatedAt: now), ct);

    await eventsBus.BroadcastAsync("deploy:complete", new
    {
        deployId = rollbackId,
        success = error is null,
        kind = "rollback-to",
        host, releaseId, swappedTo = error is null ? releaseDir : null, error,
    });
    return Results.Ok(new
    {
        status = error is null ? "rolled_back" : "rollback_failed",
        host, releaseId, swappedTo = error is null ? releaseDir : null, error,
    });
});

// DELETE /sites/{domain}/deploys/{deployId} — cancel an in-flight deploy.
// Dummy: only allows cancel when status is queued/running and not past PONR.
app.MapDelete("/api/nks.wdc.deploy/sites/{domain}/deploys/{deployId}", async (
    string domain, string deployId,
    NKS.WebDevConsole.Core.Interfaces.IDeployRunsRepository runs,
    NKS.WebDevConsole.Core.Interfaces.IDeployEventBroadcaster eventsBus,
    CancellationToken ct) =>
{
    var row = await runs.GetByIdAsync(deployId, ct);
    if (row is null || !string.Equals(row.Domain, domain, StringComparison.OrdinalIgnoreCase))
        return Results.NotFound(new { error = "deploy_not_found", deployId });
    if (row.IsPastPonr)
        return Results.Conflict(new { error = "past_point_of_no_return", detail = "Use rollback instead" });
    if (row.Status is "completed" or "failed" or "cancelled" or "rolled_back")
        return Results.Conflict(new { error = "deploy_already_terminal", currentStatus = row.Status });

    await runs.MarkCompletedAsync(deployId, success: false, exitCode: -1,
        errorMessage: "cancelled by operator", durationMs: 0, ct);
    await runs.UpdateStatusAsync(deployId, "cancelled", ct);
    await eventsBus.BroadcastAsync("deploy:complete",
        new { deployId, success = false, error = "cancelled" });
    return Results.Ok(new { deployId, status = "cancelled" });
});

// GET /sites/{domain}/groups — list multi-host deploy groups for site.
app.MapGet("/api/nks.wdc.deploy/sites/{domain}/groups", async (
    string domain, int? limit,
    NKS.WebDevConsole.Core.Interfaces.IDeployGroupsRepository groups,
    CancellationToken ct) =>
{
    var rows = await groups.ListForDomainAsync(domain, limit ?? 50, ct);
    var entries = rows.Select(g => new
    {
        id          = g.Id,
        domain      = g.Domain,
        hosts       = g.Hosts,
        hostDeployIds = g.HostDeployIds,
        phase       = g.Phase,
        startedAt   = g.StartedAt.ToString("o"),
        completedAt = g.CompletedAt?.ToString("o"),
        errorMessage = g.ErrorMessage,
        triggeredBy = g.TriggeredBy,
    }).ToList();
    return Results.Ok(new { domain, count = entries.Count, entries });
});

// POST /sites/{domain}/groups — start a multi-host deploy group.
// Phase 7.5+++ — REAL fan-out via LocalDeployBackend when each host has
// localPaths configured in settings. Hosts without localPaths get a
// dummy-group row so they remain visible in the GUI Groups tab as a
// noop entry (operator can spot which hosts are misconfigured).
// Hosts list of length < 2 → 400.
app.MapPost("/api/nks.wdc.deploy/sites/{domain}/groups", async (
    string domain, HttpContext ctx,
    NKS.WebDevConsole.Core.Interfaces.IDeployGroupsRepository groups,
    NKS.WebDevConsole.Core.Interfaces.IDeployRunsRepository runs,
    NKS.WebDevConsole.Core.Interfaces.IDeployIntentValidator intentValidator,
    NKS.WebDevConsole.Core.Interfaces.IDeployEventBroadcaster eventsBus,
    NKS.WebDevConsole.Daemon.Deploy.LocalDeployBackend localBackend,
    CancellationToken ct) =>
{
    using var doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
    var root = doc.RootElement;
    var hosts = root.TryGetProperty("hosts", out var hEl) && hEl.ValueKind == System.Text.Json.JsonValueKind.Array
        ? hEl.EnumerateArray().Select(h => h.GetString() ?? "").Where(s => s.Length > 0).ToList()
        : new List<string>();
    if (hosts.Count < 2)
        return Results.BadRequest(new { error = "groups_require_2_or_more_hosts", got = hosts.Count });

    // Phase 7.5+++ — optional MCP intent gate. Group deploy uses kind=deploy
    // (not 'group') because the per-host underlying operation IS deploy;
    // a single intent token can authorize the whole fan-out. Validates
    // against the FIRST host so MCP grants matching by exact host still
    // work (the group shares one token across all hosts). Token can come
    // from header X-Intent-Token or body.intentToken.
    var grpIntentToken = ctx.Request.Headers["X-Intent-Token"].FirstOrDefault();
    if (string.IsNullOrEmpty(grpIntentToken)
        && root.TryGetProperty("intentToken", out var grpTokenEl))
        grpIntentToken = grpTokenEl.GetString();
    if (!string.IsNullOrEmpty(grpIntentToken))
    {
        var grpAllowUnconfirmed = string.Equals(
            ctx.Request.Headers["X-Allow-Unconfirmed"].FirstOrDefault(), "true",
            StringComparison.OrdinalIgnoreCase);
        var verdict = await intentValidator.ValidateAndConsumeAsync(
            grpIntentToken, "deploy", domain, hosts[0], grpAllowUnconfirmed, ct);
        if (!verdict.Ok)
            return Results.Json(new { error = "intent_rejected", reason = verdict.Reason },
                statusCode: verdict.Reason == "pending_confirmation" ? 425 : 403);
    }

    // Resolve per-host localPaths + shared/keepReleases options up-front
    // so we can decide which hosts will run real vs noop.
    var hostConfigs = new Dictionary<string, (string? src, string? tgt, IReadOnlyList<string>? sharedDirs, IReadOnlyList<string>? sharedFiles)>();
    int? siteKeepReleases = null;
    bool allowConcurrent = true;
    try
    {
        var settingsPath = DeploySettingsPath(domain);
        if (File.Exists(settingsPath))
        {
            using var sdoc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath, ct));
            var rootEl = sdoc.RootElement;
            if (rootEl.TryGetProperty("hosts", out var hostsEl)
                && hostsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var hEl2 in hostsEl.EnumerateArray())
                {
                    if (!hEl2.TryGetProperty("name", out var nEl)) continue;
                    var name = nEl.GetString() ?? "";
                    if (!hosts.Contains(name)) continue;
                    string? src = hEl2.TryGetProperty("localSourcePath", out var lsEl) ? lsEl.GetString() : null;
                    string? tgt = hEl2.TryGetProperty("localTargetPath", out var ltEl) ? ltEl.GetString() : null;
                    List<string>? sd = null;
                    List<string>? sf = null;
                    if (hEl2.TryGetProperty("sharedDirs", out var sdEl)
                        && sdEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                        sd = sdEl.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToList();
                    if (hEl2.TryGetProperty("sharedFiles", out var sfEl)
                        && sfEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                        sf = sfEl.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToList();
                    hostConfigs[name] = (src, tgt, sd, sf);
                }
            }
            if (rootEl.TryGetProperty("advanced", out var advEl))
            {
                if (advEl.TryGetProperty("keepReleases", out var krEl) && krEl.TryGetInt32(out var krVal))
                    siteKeepReleases = krVal;
                if (advEl.TryGetProperty("allowConcurrentHosts", out var acEl)
                    && acEl.ValueKind == System.Text.Json.JsonValueKind.False)
                    allowConcurrent = false;
            }
        }
    }
    catch { /* best-effort — hosts without config become noop entries */ }

    var groupId = Guid.NewGuid().ToString("D");
    var now = DateTimeOffset.UtcNow;
    var releaseId = now.ToString("yyyyMMdd_HHmmss");

    // Spawn one DeployRunRow per host. Real ones start in 'queued' so the
    // background backend can transition them; noop ones go straight to
    // 'completed' as before so they don't sit in queued forever.
    var hostDeployIds = new Dictionary<string, string>(hosts.Count);
    var realDeploys = new List<(string deployId, string host, string src, string tgt, IReadOnlyList<string>? sd, IReadOnlyList<string>? sf)>();
    foreach (var host in hosts)
    {
        var deployId = Guid.NewGuid().ToString("D");
        var (src, tgt, sd, sf) = hostConfigs.TryGetValue(host, out var c)
            ? c : (null, null, null, null);
        var hasPaths = !string.IsNullOrEmpty(src) && !string.IsNullOrEmpty(tgt);
        await runs.InsertAsync(new NKS.WebDevConsole.Core.Interfaces.DeployRunRow(
            Id: deployId, Domain: domain, Host: host,
            ReleaseId: releaseId,
            Branch: null, CommitSha: null,
            Status: hasPaths ? "queued" : "completed",
            IsPastPonr: !hasPaths,
            StartedAt: now,
            CompletedAt: hasPaths ? null : now,
            ExitCode: hasPaths ? null : 0,
            ErrorMessage: null,
            DurationMs: hasPaths ? null : 50,
            TriggeredBy: "gui",
            BackendId: hasPaths ? "local" : "noop-group",
            CreatedAt: now, UpdatedAt: now,
            GroupId: groupId), ct);
        hostDeployIds[host] = deployId;
        if (hasPaths) realDeploys.Add((deployId, host, src!, tgt!, sd, sf));
    }

    await groups.InsertAsync(new NKS.WebDevConsole.Core.Interfaces.DeployGroupRow(
        Id: groupId, Domain: domain, Hosts: hosts,
        HostDeployIds: hostDeployIds,
        // Schema CHECK (migration 009) accepts: initializing, preflight,
        // deploying, awaiting_all_soak, all_succeeded, partial_failure,
        // rolling_back_all, rolled_back, group_failed.
        // 'deploying' when we have real backend work to do; 'all_succeeded'
        // when everything is noop (no localPaths configured for any host).
        Phase: realDeploys.Count > 0 ? "deploying" : "all_succeeded",
        StartedAt: now,
        CompletedAt: realDeploys.Count > 0 ? null : now,
        ErrorMessage: null, TriggeredBy: "gui",
        CreatedAt: now, UpdatedAt: now), ct);

    await eventsBus.BroadcastAsync("deploy:group-started",
        new { groupId, domain, hosts, realCount = realDeploys.Count });

    // Fan out — concurrent (default) or sequential per advanced config.
    _ = Task.Run(async () =>
    {
        if (allowConcurrent)
        {
            var tasks = realDeploys.Select(r =>
                localBackend.RunAsync(r.deployId, releaseId, r.src, r.tgt,
                    new NKS.WebDevConsole.Daemon.Deploy.LocalDeployBackend.Options(
                        SharedDirs: r.sd, SharedFiles: r.sf, KeepReleases: siteKeepReleases))).ToArray();
            await Task.WhenAll(tasks);
        }
        else
        {
            foreach (var r in realDeploys)
            {
                await localBackend.RunAsync(r.deployId, releaseId, r.src, r.tgt,
                    new NKS.WebDevConsole.Daemon.Deploy.LocalDeployBackend.Options(
                        SharedDirs: r.sd, SharedFiles: r.sf, KeepReleases: siteKeepReleases));
            }
        }
        await groups.UpdatePhaseAsync(groupId, "all_succeeded", isTerminal: true, errorMessage: null, default);
        await eventsBus.BroadcastAsync("deploy:group-complete",
            new { groupId, domain, success = true, realCount = realDeploys.Count });
    });

    return Results.Ok(new
    {
        groupId,
        idempotencyKey = Guid.NewGuid().ToString("D"),
        hostCount = hosts.Count,
        realCount = realDeploys.Count,
        noopCount = hosts.Count - realDeploys.Count,
    });
});

// POST /sites/{domain}/groups/{groupId}/rollback — cascade rollback
// every committed host. Phase 7.5++ flipped per-host deploy_runs rows.
// Phase 7.5+++ — also performs the REAL atomic symlink swap per host
// when localTargetPath is configured + .dep/previous_release exists.
// Hosts without localPaths get the legacy DB-only flip so the Groups
// tab → drilldown shows them as rolled_back rather than stuck Done.
app.MapPost("/api/nks.wdc.deploy/sites/{domain}/groups/{groupId}/rollback", async (
    string domain, string groupId, HttpContext grbCtx,
    NKS.WebDevConsole.Core.Interfaces.IDeployGroupsRepository groups,
    NKS.WebDevConsole.Core.Interfaces.IDeployRunsRepository runs,
    NKS.WebDevConsole.Core.Interfaces.IDeployIntentValidator intentValidator,
    CancellationToken ct) =>
{
    var grp = await groups.GetByIdAsync(groupId, ct);
    if (grp is null || !string.Equals(grp.Domain, domain, StringComparison.OrdinalIgnoreCase))
        return Results.NotFound(new { error = "group_not_found", groupId });

    // Optional MCP intent gate (kind=rollback, host=*).
    var grbIntentToken = grbCtx.Request.Headers["X-Intent-Token"].FirstOrDefault();
    if (!string.IsNullOrEmpty(grbIntentToken))
    {
        var grbAllowUnconfirmed = string.Equals(
            grbCtx.Request.Headers["X-Allow-Unconfirmed"].FirstOrDefault(), "true",
            StringComparison.OrdinalIgnoreCase);
        var verdict = await intentValidator.ValidateAndConsumeAsync(
            grbIntentToken, "rollback", domain, "*", grbAllowUnconfirmed, ct);
        if (!verdict.Ok)
            return Results.Json(new { error = "intent_rejected", reason = verdict.Reason },
                statusCode: verdict.Reason == "pending_confirmation" ? 425 : 403);
    }

    // Resolve per-host localTargetPath up-front so the cascade can do
    // real swaps where configured.
    var hostTargets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    try
    {
        var settingsPath = DeploySettingsPath(domain);
        if (File.Exists(settingsPath))
        {
            using var sdoc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath, ct));
            if (sdoc.RootElement.TryGetProperty("hosts", out var hostsEl)
                && hostsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var hEl in hostsEl.EnumerateArray())
                {
                    if (!hEl.TryGetProperty("name", out var nEl)) continue;
                    var name = nEl.GetString() ?? "";
                    if (hEl.TryGetProperty("localTargetPath", out var ltEl))
                    {
                        var t = ltEl.GetString();
                        if (!string.IsNullOrEmpty(t)) hostTargets[name] = t;
                    }
                }
            }
        }
    }
    catch { /* best-effort — hosts without entries get DB-only flip */ }

    var realSwaps = new List<object>();
    var noopHosts = new List<string>();
    foreach (var (host, deployId) in grp.HostDeployIds)
    {
        try { await runs.UpdateStatusAsync(deployId, "rolled_back", ct); } catch { }
        if (!hostTargets.TryGetValue(host, out var tgt))
        {
            noopHosts.Add(host);
            continue;
        }
        var depPrev = Path.Combine(tgt, ".dep", "previous_release");
        var currentLink = Path.Combine(tgt, "current");
        if (!File.Exists(depPrev))
        {
            noopHosts.Add(host);
            continue;
        }
        try
        {
            var prevRelease = (await File.ReadAllTextAsync(depPrev, ct)).Trim();
            if (string.IsNullOrEmpty(prevRelease) || !Directory.Exists(prevRelease))
            {
                noopHosts.Add(host);
                continue;
            }
            // Atomic swap + .dep rotation (mirrors single-host rollback).
            if (Directory.Exists(currentLink))
            {
                var fi = new DirectoryInfo(currentLink);
                if (fi.LinkTarget is not null) Directory.Delete(currentLink);
                else Directory.Delete(currentLink, recursive: true);
            }
            Directory.CreateSymbolicLink(currentLink, prevRelease);
            var depCurrent = Path.Combine(tgt, ".dep", "current_release");
            var oldCurrent = File.Exists(depCurrent)
                ? (await File.ReadAllTextAsync(depCurrent, ct)).Trim() : string.Empty;
            await File.WriteAllTextAsync(depCurrent, prevRelease, ct);
            if (!string.IsNullOrEmpty(oldCurrent) && oldCurrent != prevRelease)
                await File.WriteAllTextAsync(depPrev, oldCurrent, ct);
            realSwaps.Add(new { host, swappedTo = prevRelease });
        }
        catch (Exception ex)
        {
            realSwaps.Add(new { host, error = ex.Message });
        }
    }

    // Mark group as rolled_back via UpdatePhaseAsync — schema CHECK
    // accepts 'rolled_back' (migration 009).
    try { await groups.UpdatePhaseAsync(groupId, "rolled_back", isTerminal: true, errorMessage: null, ct); }
    catch { /* best-effort */ }

    return Results.Ok(new
    {
        groupId,
        status = "rolled_back",
        hostCount = grp.Hosts.Count,
        realSwaps,
        noopHosts,
    });
});

// Phase 7.5+ — on-demand snapshot WITHOUT a deploy. Frontend's
// "Snapshot database now" button in DeploySettingsPanel hits this.
// Real backend would actually run pg_dump / mysqldump; the dummy
// records a synthetic deploy_runs row tagged backend_id='manual-snapshot'
// so it surfaces in the snapshot list (which projects rows with
// non-null pre_deploy_backup_path) without needing an actual deploy.
app.MapPost("/api/nks.wdc.deploy/sites/{domain}/snapshot-now", async (
    string domain, HttpContext ctx,
    NKS.WebDevConsole.Core.Interfaces.IDeployRunsRepository runs,
    CancellationToken ct) =>
{
    var snapshotId = Guid.NewGuid().ToString("D");
    var now = DateTimeOffset.UtcNow;
    var sw = System.Diagnostics.Stopwatch.StartNew();

    // Phase 7.5+++ — when ANY host has localTargetPath configured + a
    // resolvable `current` symlink, ZIP that release dir into the manual
    // backups folder. Result is a REAL recovery artifact the operator
    // can extract back to the host. Without localPaths we keep the
    // historic fake-record behaviour so existing tests + the GUI list
    // still see an entry (back-compat).
    string? sourceCurrent = null;
    string? hostName = null;
    try
    {
        // Optional body { host: "..." } — picks a specific host's current/.
        // No body or no host → first host with localTargetPath wins.
        string? bodyHost = null;
        if (ctx.Request.ContentLength is > 0)
        {
            try
            {
                using var bdoc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
                if (bdoc.RootElement.TryGetProperty("host", out var hEl))
                    bodyHost = hEl.GetString();
            }
            catch { /* empty / non-JSON body is fine */ }
        }

        var settingsPath = DeploySettingsPath(domain);
        if (File.Exists(settingsPath))
        {
            using var sdoc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath, ct));
            if (sdoc.RootElement.TryGetProperty("hosts", out var hostsEl)
                && hostsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var hEl2 in hostsEl.EnumerateArray())
                {
                    if (!hEl2.TryGetProperty("name", out var nEl)) continue;
                    var n = nEl.GetString() ?? "";
                    if (!string.IsNullOrEmpty(bodyHost) && !string.Equals(n, bodyHost, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (hEl2.TryGetProperty("localTargetPath", out var ltEl))
                    {
                        var tgt = ltEl.GetString();
                        if (!string.IsNullOrEmpty(tgt))
                        {
                            var candidate = Path.Combine(tgt, "current");
                            if (Directory.Exists(candidate))
                            {
                                sourceCurrent = candidate;
                                hostName = n;
                                break;
                            }
                        }
                    }
                    if (!string.IsNullOrEmpty(bodyHost)) break; // explicit host miss → don't fall through
                }
            }
        }
    }
    catch { /* best-effort */ }

    long sizeBytes;
    string returnedPath;
    if (sourceCurrent is not null)
    {
        // Real ZIP. Resolve `current` symlink target so the archive
        // captures the actual files, not symlink metadata.
        var realRoot = sourceCurrent;
        try
        {
            var info = new DirectoryInfo(sourceCurrent);
            if (info.LinkTarget is not null && Directory.Exists(info.LinkTarget))
                realRoot = info.LinkTarget;
        }
        catch { /* fall back to current path */ }

        var backupsDir = Path.Combine(NKS.WebDevConsole.Core.Services.WdcPaths.BackupsRoot, "manual", domain);
        Directory.CreateDirectory(backupsDir);
        var realPath = Path.Combine(backupsDir, $"{snapshotId}.zip");
        try
        {
            System.IO.Compression.ZipFile.CreateFromDirectory(
                realRoot, realPath,
                System.IO.Compression.CompressionLevel.Fastest,
                includeBaseDirectory: false);
            sizeBytes = new FileInfo(realPath).Length;

            // Phase 7.5+++ — prune older zips per settings retention.
            // Default 30 days when settings missing/malformed (matches
            // defaultDeploySettings().snapshot.retentionDays in the GUI).
            var rd = ReadSnapshotRetentionDays(domain) ?? 30;
            PurgeOldSnapshots("manual", domain, rd);
        }
        catch (Exception ex)
        {
            // Bubble up — operator sees the failure rather than getting a
            // silently broken snapshot row. Common cause: file in release
            // directory is locked by another process during the zip pass.
            return Results.Json(new { error = "snapshot_zip_failed", detail = ex.Message },
                statusCode: 500);
        }
        returnedPath = $"~/.wdc/backups/manual/{domain}/{snapshotId}.zip";
    }
    else
    {
        // No local target available — record a placeholder row so GUI
        // shows an entry but flag the path as the legacy stub shape.
        sizeBytes = 1024 * 512;
        returnedPath = $"~/.wdc/backups/manual/{domain}/{snapshotId}.sql.gz";
    }

    sw.Stop();
    await runs.InsertAsync(new NKS.WebDevConsole.Core.Interfaces.DeployRunRow(
        Id: snapshotId, Domain: domain, Host: hostName ?? "manual",
        ReleaseId: now.ToString("yyyyMMdd_HHmmss") + "-manual",
        Branch: null, CommitSha: null,
        Status: "completed", IsPastPonr: false,
        StartedAt: now, CompletedAt: DateTimeOffset.UtcNow,
        ExitCode: 0, ErrorMessage: null, DurationMs: sw.ElapsedMilliseconds,
        TriggeredBy: "gui", BackendId: "manual-snapshot",
        CreatedAt: now, UpdatedAt: now), ct);
    await runs.UpdatePreDeployBackupAsync(snapshotId, returnedPath, sizeBytes, ct);

    return Results.Ok(new
    {
        snapshotId, domain,
        path = returnedPath,
        sizeBytes,
        durationMs = sw.ElapsedMilliseconds,
        host = hostName,
    });
});

// Phase 7.5+ — restore a previous snapshot. The kind on the intent token
// MUST be 'restore' (validator enforces) which the registry tags as
// Destructive — banner uses the typed-host-name confirmation flow.
//
// Two route shapes accepted (both fixed to frontend expectations):
//   POST /sites/{domain}/restore                       — body { snapshotId, intentToken }
//   POST /sites/{domain}/snapshots/{snapshotId}/restore — header X-Intent-Token, body { confirm: true }
// Both lower into the same handler below.
//
// This is a dummy that just verifies the snapshot existed; real backend
// would actually `gunzip + mysql restore` from the path.
static async Task<IResult> HandleRestoreAsync(
    string domain, string? snapshotIdFromRoute, HttpContext ctx,
    NKS.WebDevConsole.Core.Interfaces.IDeployRunsRepository runs,
    NKS.WebDevConsole.Core.Interfaces.IDeployIntentValidator intentValidator,
    NKS.WebDevConsole.Core.Interfaces.IDeployEventBroadcaster eventsBus,
    CancellationToken ct)
{
    using var doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
    var root = doc.RootElement;
    // snapshotId can come from route path OR body — route wins (more specific).
    var snapshotId = !string.IsNullOrEmpty(snapshotIdFromRoute)
        ? snapshotIdFromRoute
        : (root.TryGetProperty("snapshotId", out var sEl) ? sEl.GetString() : null);
    // Intent token from header X-Intent-Token (frontend convention) OR body field.
    var intentToken = ctx.Request.Headers["X-Intent-Token"].FirstOrDefault();
    if (string.IsNullOrEmpty(intentToken) && root.TryGetProperty("intentToken", out var tEl))
        intentToken = tEl.GetString();
    var host = root.TryGetProperty("host", out var hEl) ? hEl.GetString() ?? "production" : "production";
    if (string.IsNullOrEmpty(snapshotId))
        return Results.BadRequest(new { error = "snapshotId is required" });

    // MCP intent gate — restore requires kind='restore' specifically (NOT
    // kind='deploy'); validator enforces the kind_match check. Caller
    // can pass X-Allow-Unconfirmed for headless flows.
    if (!string.IsNullOrEmpty(intentToken))
    {
        var allowUnconfirmed = string.Equals(
            ctx.Request.Headers["X-Allow-Unconfirmed"].FirstOrDefault(), "true",
            StringComparison.OrdinalIgnoreCase);
        var verdict = await intentValidator.ValidateAndConsumeAsync(
            intentToken, "restore", domain, host, allowUnconfirmed, ct);
        if (!verdict.Ok)
        {
            return Results.Json(
                new { error = "intent_rejected", reason = verdict.Reason },
                statusCode: verdict.Reason == "pending_confirmation" ? 425 : 403);
        }
    }

    // Verify the snapshot row exists and actually has a backup path —
    // otherwise the restore would have nothing to restore from.
    var sourceRow = await runs.GetByIdAsync(snapshotId, ct);
    if (sourceRow is null)
        return Results.NotFound(new { error = "snapshot_not_found", snapshotId });
    if (string.IsNullOrEmpty(sourceRow.PreDeployBackupPath))
        return Results.BadRequest(new
        {
            error = "snapshot_has_no_backup",
            detail = $"Deploy {snapshotId[..8]} did not capture a pre-deploy snapshot.",
        });

    // Phase 7.5+++ — REAL extract when the backup path resolves to an
    // actual .zip file. Resolves the `~` prefix to the user's home dir.
    // Without a real .zip file (legacy fake snapshot rows), keeps the
    // dummy "verified-only" behaviour.
    var backupPath = sourceRow.PreDeployBackupPath;
    var resolvedBackupPath = backupPath.StartsWith("~/")
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            backupPath[2..].Replace('/', Path.DirectorySeparatorChar))
        : backupPath;

    string? targetPath = null;
    string? extractedTo = null;
    string? swappedTo = null;
    string? restoreError = null;

    if (File.Exists(resolvedBackupPath) && resolvedBackupPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
    {
        // Find the host's localTargetPath so we know where to restore.
        try
        {
            var settingsPath = DeploySettingsPath(domain);
            if (File.Exists(settingsPath))
            {
                using var sdoc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath, ct));
                if (sdoc.RootElement.TryGetProperty("hosts", out var hostsEl)
                    && hostsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var hEl2 in hostsEl.EnumerateArray())
                    {
                        if (!hEl2.TryGetProperty("name", out var nEl)) continue;
                        if (!string.Equals(nEl.GetString(), host, StringComparison.OrdinalIgnoreCase)) continue;
                        if (hEl2.TryGetProperty("localTargetPath", out var ltEl))
                            targetPath = ltEl.GetString();
                        break;
                    }
                }
            }
        }
        catch { /* best-effort */ }

        if (!string.IsNullOrEmpty(targetPath))
        {
            try
            {
                // Extract into a fresh release dir so the restore is auditable
                // alongside normal deploys (shows up in releases/ + Releases tab).
                var nowR = DateTimeOffset.UtcNow;
                var releaseId = nowR.ToString("yyyyMMdd_HHmmss") + "-restored-" + snapshotId[..8];
                var releaseDir = Path.Combine(targetPath, "releases", releaseId);
                Directory.CreateDirectory(releaseDir);
                System.IO.Compression.ZipFile.ExtractToDirectory(
                    resolvedBackupPath, releaseDir, overwriteFiles: true);
                extractedTo = releaseDir;

                // Atomic swap of current symlink → new release dir.
                var currentLink = Path.Combine(targetPath, "current");
                var depDir = Path.Combine(targetPath, ".dep");
                Directory.CreateDirectory(depDir);
                var depCurrent = Path.Combine(depDir, "current_release");
                var depPrev = Path.Combine(depDir, "previous_release");
                string? oldCurrent = File.Exists(depCurrent)
                    ? (await File.ReadAllTextAsync(depCurrent, ct)).Trim()
                    : null;
                if (Directory.Exists(currentLink))
                {
                    var fi = new DirectoryInfo(currentLink);
                    if (fi.LinkTarget is not null) Directory.Delete(currentLink);
                    else Directory.Delete(currentLink, recursive: true);
                }
                Directory.CreateSymbolicLink(currentLink, releaseDir);
                await File.WriteAllTextAsync(depCurrent, releaseDir, ct);
                if (!string.IsNullOrEmpty(oldCurrent) && oldCurrent != releaseDir)
                    await File.WriteAllTextAsync(depPrev, oldCurrent, ct);
                swappedTo = releaseDir;

                // Audit row in deploy_runs so the operation appears in
                // history + the Releases sub-tab.
                var restoreRunId = Guid.NewGuid().ToString("D");
                await runs.InsertAsync(new NKS.WebDevConsole.Core.Interfaces.DeployRunRow(
                    Id: restoreRunId, Domain: domain, Host: host,
                    ReleaseId: releaseId,
                    Branch: null, CommitSha: null,
                    Status: "completed", IsPastPonr: true,
                    StartedAt: nowR, CompletedAt: DateTimeOffset.UtcNow,
                    ExitCode: 0, ErrorMessage: null, DurationMs: 50,
                    TriggeredBy: "gui", BackendId: "local-restore",
                    CreatedAt: nowR, UpdatedAt: nowR), ct);
            }
            catch (Exception ex) { restoreError = ex.Message; }
        }
        else
        {
            restoreError = "no_local_target_for_host — restore would have nowhere to write";
        }
    }

    // Broadcast the audit event so the GUI's activity feed / drawer
    // sees something happened.
    await eventsBus.BroadcastAsync("restore:complete", new
    {
        domain, snapshotId, host,
        backupPath = sourceRow.PreDeployBackupPath,
        backupSizeBytes = sourceRow.PreDeployBackupSizeBytes ?? 0,
        extractedTo, swappedTo, error = restoreError,
    });

    return Results.Ok(new
    {
        restored = restoreError is null,
        sourceDeployId = snapshotId,
        backupPath = sourceRow.PreDeployBackupPath,
        backupSizeBytes = sourceRow.PreDeployBackupSizeBytes ?? 0,
        extractedTo,
        swappedTo,
        error = restoreError,
    });
}

// Both routes alias HandleRestoreAsync.
app.MapPost("/api/nks.wdc.deploy/sites/{domain}/restore",
    (string domain, HttpContext ctx,
     NKS.WebDevConsole.Core.Interfaces.IDeployRunsRepository runs,
     NKS.WebDevConsole.Core.Interfaces.IDeployIntentValidator intentValidator,
     NKS.WebDevConsole.Core.Interfaces.IDeployEventBroadcaster eventsBus,
     CancellationToken ct) =>
        HandleRestoreAsync(domain, null, ctx, runs, intentValidator, eventsBus, ct));

app.MapPost("/api/nks.wdc.deploy/sites/{domain}/snapshots/{snapshotId}/restore",
    (string domain, string snapshotId, HttpContext ctx,
     NKS.WebDevConsole.Core.Interfaces.IDeployRunsRepository runs,
     NKS.WebDevConsole.Core.Interfaces.IDeployIntentValidator intentValidator,
     NKS.WebDevConsole.Core.Interfaces.IDeployEventBroadcaster eventsBus,
     CancellationToken ct) =>
        HandleRestoreAsync(domain, snapshotId, ctx, runs, intentValidator, eventsBus, ct));

// Phase 7.5 dummy backend with realistic state-machine + optional MCP
// intent gate. POST body:
//   { "host": "...", "branch": "...", "intentToken": "<id>.<nonce>.<sig>" }
// If intentToken is provided, validator runs first (kind='deploy' enforced).
// On success, a background task drives status: queued→running→awaiting_soak
// →completed and broadcasts deploy events on each transition. Returns
// immediately with 202 + deployId so the GUI can subscribe to SSE.
app.MapPost("/api/nks.wdc.deploy/sites/{domain}/deploy", async (
    string domain,
    HttpContext ctx,
    NKS.WebDevConsole.Core.Interfaces.IDeployRunsRepository runs,
    NKS.WebDevConsole.Core.Interfaces.IDeployIntentValidator intentValidator,
    NKS.WebDevConsole.Core.Interfaces.IDeployEventBroadcaster eventsBus,
    NKS.WebDevConsole.Daemon.Deploy.LocalDeployBackend localBackend,
    CancellationToken ct) =>
{
    using var doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
    var root = doc.RootElement;
    var host = root.TryGetProperty("host", out var hEl) ? hEl.GetString() ?? "production" : "production";
    var branch = root.TryGetProperty("branch", out var bEl) ? bEl.GetString() : null;
    var intentToken = root.TryGetProperty("intentToken", out var tEl) ? tEl.GetString() : null;
    var triggeredBy = string.IsNullOrEmpty(intentToken) ? "gui" : "mcp";

    // Phase 7.5+++ — `localPaths: {source, target}` resolved in priority:
    //   1) Body wins (ad-hoc / E2E override).
    //   2) Fallback to per-host settings on disk so the GUI can dispatch
    //      a deploy with just `host` once the operator has configured
    //      localSourcePath/localTargetPath in the host edit dialog.
    // Real local-loopback backend only — no dummy state machine.
    // Without resolvable paths the deploy endpoint refuses with 400.
    string? localSource = null;
    string? localTarget = null;
    if (root.TryGetProperty("localPaths", out var lpEl) && lpEl.ValueKind == System.Text.Json.JsonValueKind.Object)
    {
        if (lpEl.TryGetProperty("source", out var srcEl)) localSource = srcEl.GetString();
        if (lpEl.TryGetProperty("target", out var tgtEl)) localTarget = tgtEl.GetString();
    }

    // Phase 7.5+++ nksdeploy compat — also resolve shared dirs/files +
    // keepReleases retention from settings so the LocalDeployBackend
    // can apply them. Body can override via `localOptions: {...}`.
    List<string>? optSharedDirs = null;
    List<string>? optSharedFiles = null;
    int? optKeepReleases = null;
    if (root.TryGetProperty("localOptions", out var loEl) && loEl.ValueKind == System.Text.Json.JsonValueKind.Object)
    {
        if (loEl.TryGetProperty("sharedDirs", out var sdEl) && sdEl.ValueKind == System.Text.Json.JsonValueKind.Array)
            optSharedDirs = sdEl.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToList();
        if (loEl.TryGetProperty("sharedFiles", out var sfEl) && sfEl.ValueKind == System.Text.Json.JsonValueKind.Array)
            optSharedFiles = sfEl.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToList();
        if (loEl.TryGetProperty("keepReleases", out var krEl) && krEl.TryGetInt32(out var krVal))
            optKeepReleases = krVal;
    }

    if (string.IsNullOrEmpty(localSource) || string.IsNullOrEmpty(localTarget)
        || optSharedDirs is null || optSharedFiles is null || optKeepReleases is null)
    {
        // Look up settings JSON to fill in any missing values.
        // File-per-site shape mirrors what the frontend's DeploySettingsPanel writes.
        try
        {
            var settingsPath = DeploySettingsPath(domain);
            if (File.Exists(settingsPath))
            {
                using var sdoc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath, ct));
                var rootEl = sdoc.RootElement;
                if (rootEl.TryGetProperty("hosts", out var hostsEl)
                    && hostsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var hEl2 in hostsEl.EnumerateArray())
                    {
                        if (!hEl2.TryGetProperty("name", out var nEl)) continue;
                        if (!string.Equals(nEl.GetString(), host, StringComparison.OrdinalIgnoreCase)) continue;
                        if (string.IsNullOrEmpty(localSource)
                            && hEl2.TryGetProperty("localSourcePath", out var lsEl))
                            localSource = lsEl.GetString();
                        if (string.IsNullOrEmpty(localTarget)
                            && hEl2.TryGetProperty("localTargetPath", out var ltEl))
                            localTarget = ltEl.GetString();
                        if (optSharedDirs is null && hEl2.TryGetProperty("sharedDirs", out var hsdEl)
                            && hsdEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                            optSharedDirs = hsdEl.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToList();
                        if (optSharedFiles is null && hEl2.TryGetProperty("sharedFiles", out var hsfEl)
                            && hsfEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                            optSharedFiles = hsfEl.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToList();
                        break;
                    }
                }
                // Site-wide retention from advanced.keepReleases when not host-overridden.
                if (optKeepReleases is null
                    && rootEl.TryGetProperty("advanced", out var advEl)
                    && advEl.TryGetProperty("keepReleases", out var krsEl)
                    && krsEl.TryGetInt32(out var krsVal))
                    optKeepReleases = krsVal;
            }
        }
        catch { /* swallow — fall through to 400 below if paths still empty */ }
    }
    if (string.IsNullOrEmpty(localSource) || string.IsNullOrEmpty(localTarget))
    {
        return Results.BadRequest(new
        {
            error = "localPaths_required",
            detail = "Provide localPaths: {source, target} in body, or configure localSourcePath + localTargetPath on the host in deploy settings.",
        });
    }

    // Optional MCP gate. When token provided, must be valid + confirmed
    // (or the caller passes X-Allow-Unconfirmed for CI). Plugin-extensible
    // via the kinds registry — if mcp.strict_kinds is on, only registered
    // kinds pass.
    if (!string.IsNullOrEmpty(intentToken))
    {
        var allowUnconfirmed = string.Equals(
            ctx.Request.Headers["X-Allow-Unconfirmed"].FirstOrDefault(), "true",
            StringComparison.OrdinalIgnoreCase);
        var verdict = await intentValidator.ValidateAndConsumeAsync(
            intentToken, "deploy", domain, host, allowUnconfirmed, ct);
        if (!verdict.Ok)
        {
            return Results.Json(
                new { error = "intent_rejected", reason = verdict.Reason },
                statusCode: verdict.Reason == "pending_confirmation" ? 425 : 403);
        }
    }

    var deployId = Guid.NewGuid().ToString("D");
    var now = DateTimeOffset.UtcNow;
    var releaseId = now.ToString("yyyyMMdd_HHmmss");
    await runs.InsertAsync(new NKS.WebDevConsole.Core.Interfaces.DeployRunRow(
        Id: deployId, Domain: domain, Host: host,
        ReleaseId: releaseId,
        Branch: branch, CommitSha: null,
        Status: "queued", IsPastPonr: false,
        StartedAt: now, CompletedAt: null,
        ExitCode: null, ErrorMessage: null, DurationMs: null,
        TriggeredBy: triggeredBy,
        BackendId: "local",
        CreatedAt: now, UpdatedAt: now), ct);

    // Phase 7.5+++ — body `snapshot: true` OR `snapshot: { include: true }`
    // (the latter from the GUI) triggers a REAL pre-deploy snapshot when
    // localTarget/current resolves to a real dir. Without a current dir
    // (first deploy ever), records a placeholder so the row still has a
    // backupPath for audit consistency.
    bool snapshotRequested = false;
    if (root.TryGetProperty("snapshot", out var sEl))
    {
        if (sEl.ValueKind == System.Text.Json.JsonValueKind.True)
            snapshotRequested = true;
        else if (sEl.ValueKind == System.Text.Json.JsonValueKind.Object
                 && sEl.TryGetProperty("include", out var incEl)
                 && incEl.ValueKind == System.Text.Json.JsonValueKind.True)
            snapshotRequested = true;
    }
    if (snapshotRequested)
    {
        var currentDir = Path.Combine(localTarget!, "current");
        if (Directory.Exists(currentDir))
        {
            // Resolve symlink so we zip real contents (not link metadata).
            var realRoot = currentDir;
            try
            {
                var info = new DirectoryInfo(currentDir);
                if (info.LinkTarget is not null && Directory.Exists(info.LinkTarget))
                    realRoot = info.LinkTarget;
            }
            catch { /* fall back to currentDir */ }

            var preDir = Path.Combine(NKS.WebDevConsole.Core.Services.WdcPaths.BackupsRoot,
                "pre-deploy", domain);
            Directory.CreateDirectory(preDir);
            var realPath = Path.Combine(preDir, $"{deployId}.zip");
            try
            {
                System.IO.Compression.ZipFile.CreateFromDirectory(
                    realRoot, realPath,
                    System.IO.Compression.CompressionLevel.Fastest,
                    includeBaseDirectory: false);
                var size = new FileInfo(realPath).Length;
                await runs.UpdatePreDeployBackupAsync(deployId,
                    $"~/.wdc/backups/pre-deploy/{domain}/{deployId}.zip", size, ct);

                // Phase 7.5+++ — retention prune. Default 30 days when
                // settings missing (matches defaultDeploySettings()).
                var rd = ReadSnapshotRetentionDays(domain) ?? 30;
                PurgeOldSnapshots("pre-deploy", domain, rd);
            }
            catch
            {
                // Don't block the deploy if the snapshot fails — log a
                // placeholder so audit shows the attempt + the failure
                // is visible in deploy logs.
                await runs.UpdatePreDeployBackupAsync(deployId,
                    $"~/.wdc/backups/pre-deploy/{domain}/{deployId}.zip.failed", 0, ct);
            }
        }
        else
        {
            // No prior deploy — placeholder for audit symmetry.
            await runs.UpdatePreDeployBackupAsync(deployId,
                $"~/.wdc/backups/pre-deploy/{domain}/{deployId}.empty", 0, ct);
        }
    }

    await eventsBus.BroadcastAsync("deploy:started",
        new { deployId, domain, host, triggeredBy, backend = "local" });

    // REAL local-loopback deploy. Background fire-and-forget — HTTP returns
    // 202 immediately, the backend writes status updates and SSE events as
    // it progresses through copy + symlink phases.
    var deployOptions = new NKS.WebDevConsole.Daemon.Deploy.LocalDeployBackend.Options(
        SharedDirs: optSharedDirs,
        SharedFiles: optSharedFiles,
        KeepReleases: optKeepReleases);
    _ = Task.Run(() => localBackend.RunAsync(deployId, releaseId, localSource!, localTarget!, deployOptions));
    return Results.Accepted($"/api/nks.wdc.deploy/sites/{domain}/deploys/{deployId}",
        new { deployId, status = "queued", note = "local backend — copying files" });
});

// Phase 7.5 — phase mapping moved to DeployRestHelpers for testability.
static string MapStatusToPhase(string status) =>
    NKS.WebDevConsole.Daemon.Deploy.DeployRestHelpers.MapStatusToPhase(status);

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
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api"))
    {
        McpCallerContext.SessionId  = ctx.Request.Headers["X-Mcp-Session-Id"].FirstOrDefault();
        McpCallerContext.ApiKeyId   = ctx.Request.Headers["X-Mcp-Api-Key-Id"].FirstOrDefault();
        McpCallerContext.InstanceId = ctx.Request.Headers["X-Mcp-Instance-Id"].FirstOrDefault();
    }
    await next();
});

// Auth middleware for /api/* requests.
// SECURITY: use constant-time comparison to prevent timing attacks that could leak the token.
var authTokenBytes = System.Text.Encoding.UTF8.GetBytes(authToken);
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api"))
    {
        // Parse Authorization: Bearer <token> — use prefix check instead of Replace() which
        // is fragile (multiple occurrences, whitespace) and doesn't handle case variants.
        var authHeader = ctx.Request.Headers.Authorization.FirstOrDefault();
        string? provided = null;
        if (!string.IsNullOrEmpty(authHeader) &&
            authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            provided = authHeader.Substring(7).Trim();
        }
        // SSE/WebSocket fallback: browser EventSource/WebSocket APIs cannot set
        // Authorization headers, so token-via-query is allowed *only* on
        // those streaming endpoints. Plain /api/* requests must use the header
        // — a query-string fallback there leaked the bearer into Kestrel
        // access logs and any log-shipping pipeline (Sentry breadcrumb,
        // file sink) for the token's full TTL.
        if (provided is null)
        {
            var path = ctx.Request.Path.Value ?? string.Empty;
            var streamingPath = path.StartsWith("/api/events", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/api/ws", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/api/backup/download", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/api/plugins/", StringComparison.OrdinalIgnoreCase)
                && path.EndsWith("/icon", StringComparison.OrdinalIgnoreCase);
            if (streamingPath)
            {
                provided = ctx.Request.Query["token"].FirstOrDefault();
            }
        }

        bool ok = false;
        if (!string.IsNullOrEmpty(provided))
        {
            var providedBytes = System.Text.Encoding.UTF8.GetBytes(provided);
            // FixedTimeEquals requires same length — reject different length fast
            ok = providedBytes.Length == authTokenBytes.Length
                 && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(providedBytes, authTokenBytes);
        }

        if (!ok)
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsync("Unauthorized");
            return;
        }
    }
    await next();
});

// REST Endpoints
app.MapGet("/api/status", () => Results.Ok(new
{
    status = "running",
    version = daemonVersion,
    plugins = pluginLoader.Plugins.Count,
    uptime = DaemonUptimeSeconds()
}));

app.MapGet("/api/system", async (IServiceProvider sp, BinaryManager bm, SiteManager sm, CatalogClient cc, SettingsStore settings) =>
{
    var modules = sp.GetServices<IServiceModule>().ToArray();
    // Fan out the status probes concurrently — the previous foreach awaited
    // each module sequentially, so /api/system latency scaled linearly with
    // plugin count. With 10 built-in plugins that's 10× the slowest probe;
    // Task.WhenAll brings it back to max() of the per-module times.
    var statuses = await Task.WhenAll(
        modules.Select(m => m.GetStatusAsync(CancellationToken.None)));
    var total = statuses.Length;
    var running = statuses.Count(s => s.State == ServiceState.Running);

    // Normalised OS + arch tags for frontend filtering — the catalog uses
    // lowercase "windows"/"linux"/"macos" and "x64"/"arm64" strings, so we
    // return the same shape here and let the Binaries page highlight the
    // release that matches the current host without any mapping code in JS.
    var osTag = OperatingSystem.IsWindows() ? "windows"
              : OperatingSystem.IsLinux() ? "linux"
              : OperatingSystem.IsMacOS() ? "macos"
              : "unknown";
    var archTag = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.X64 => "x64",
        System.Runtime.InteropServices.Architecture.X86 => "x86",
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        System.Runtime.InteropServices.Architecture.Arm => "arm",
        _ => "unknown",
    };

    return Results.Ok(new
    {
        daemon = new { version = daemonVersion, uptime = DaemonUptimeSeconds(), pid = Environment.ProcessId },
        services = new { running, total },
        sites = sm.Sites.Count,
        plugins = pluginLoader.Plugins.Count,
        binaries = bm.ListInstalled().Count,
        os = new
        {
            platform = Environment.OSVersion.Platform.ToString(),
            version = Environment.OSVersion.VersionString,
            machine = Environment.MachineName,
            tag = osTag,
            arch = archTag,
        },
        runtime = new { dotnet = Environment.Version.ToString(), arch = archTag },
        // Catalog status block — lets the Binaries page + Settings
        // Advanced tab surface "catalog reachable?" without a second
        // round-trip. `lastFetch` is DateTime.MinValue when the catalog
        // has never been refreshed, `cachedCount` is the number of
        // flattened BinaryRelease rows across all apps after the last
        // RefreshAsync (zero means fetch failed or catalog is empty).
        catalog = new
        {
            url = settings.CatalogUrl,
            cachedCount = cc.CachedReleases.Count,
            lastFetch = cc.LastFetch == DateTime.MinValue ? (DateTime?)null : cc.LastFetch,
            reachable = cc.CachedReleases.Count > 0,
        },
    });
});

app.MapGet("/api/plugins", (IServiceProvider sp, PluginState pluginState) =>
{
    var modules = sp.GetServices<IServiceModule>();
    // F87: pre-compute the set of enabled plugin IDs once so every row
    // can cheaply check its dependencies against the live state.
    var enabledIds = new HashSet<string>(
        pluginLoader.Plugins
            .Where(pl => pluginState.IsEnabled(pl.Instance.Id))
            .Select(pl => pl.Instance.Id),
        StringComparer.OrdinalIgnoreCase);

    // F91.6: extract plugin-contributed UI fragments (<PluginSlot> inputs).
    // Same cross-ALC reflection pattern as everything else in this file.
    static object[] ExtractContributions(object instance)
    {
        try
        {
            var uiMethod = instance.GetType().GetMethod("GetUiDefinition");
            var def = uiMethod?.Invoke(instance, null);
            if (def is null) return Array.Empty<object>();
            if (def.GetType().GetProperty("Contributions")?.GetValue(def)
                is not System.Collections.IEnumerable contribEnum) return Array.Empty<object>();
            var list = new List<object>();
            foreach (var c in contribEnum)
            {
                if (c is null) continue;
                var t = c.GetType();
                // Props is a Dictionary<string,object> across the ALC boundary —
                // flatten to a plain dict so System.Text.Json emits it as a JSON
                // object instead of opaque type metadata.
                var propsRaw = t.GetProperty("Props")?.GetValue(c);
                var propsDict = new Dictionary<string, object?>();
                if (propsRaw is System.Collections.IDictionary d)
                {
                    foreach (System.Collections.DictionaryEntry e in d)
                        propsDict[e.Key?.ToString() ?? ""] = e.Value;
                }
                list.Add(new
                {
                    slot = t.GetProperty("Slot")?.GetValue(c)?.ToString() ?? "",
                    componentType = t.GetProperty("ComponentType")?.GetValue(c)?.ToString() ?? "",
                    props = propsDict,
                    order = t.GetProperty("Order")?.GetValue(c) is int o ? o : 100,
                });
            }
            return list.ToArray();
        }
        catch { return Array.Empty<object>(); }
    }

    // F91.2: extract the generic UI surfaces the plugin declares via
    // UiSchemaBuilder (nav:{route}, site-tab:{id}, dashboard-card:{id}, …)
    // using the same cross-ALC reflection idiom as /api/plugins/ui.
    // Returned for EVERY plugin (enabled or not) so the frontend can do
    // "is surface X owned by any plugin, and is that plugin currently on?"
    static string[] ExtractUiSurfaces(object instance)
    {
        try
        {
            var uiMethod = instance.GetType().GetMethod("GetUiDefinition");
            var def = uiMethod?.Invoke(instance, null);
            if (def is null) return Array.Empty<string>();
            var surfaces = new List<string>();
            if (def.GetType().GetProperty("UiSurfaces")?.GetValue(def)
                is System.Collections.IEnumerable sEnum)
            {
                foreach (var s in sEnum)
                {
                    if (s is null) continue;
                    var key = s.ToString();
                    if (!string.IsNullOrWhiteSpace(key)) surfaces.Add(key);
                }
            }
            // Fallback / compat: if UiSurfaces was left null but NavEntries
            // exist, still project "nav:{Route}" so older plugins keep
            // working without touching their code.
            if (surfaces.Count == 0 &&
                def.GetType().GetProperty("NavEntries")?.GetValue(def)
                    is System.Collections.IEnumerable navEnum)
            {
                foreach (var n in navEnum)
                {
                    if (n is null) continue;
                    var route = n.GetType().GetProperty("Route")?.GetValue(n)?.ToString();
                    if (!string.IsNullOrWhiteSpace(route)) surfaces.Add($"nav:{route}");
                }
            }
            return surfaces.ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    return Results.Ok(pluginLoader.Plugins.Select(p =>
    {
        // Resolve service module by exact ServiceId match on the last
        // Id token (nks.wdc.apache → apache). Substring match was broken:
        // "".Contains("") is always true, so a plugin with an empty
        // last-token would falsely map to the first module in the list;
        // and "mailpit".Contains("mail") would cross-map nks.wdc.mail
        // (hypothetical) to the Mailpit module.
        var svcId = p.Instance.Id.Split('.').LastOrDefault() ?? "";
        var serviceModule = string.IsNullOrEmpty(svcId)
            ? null
            : modules.FirstOrDefault(m => m.ServiceId.Equals(svcId, StringComparison.OrdinalIgnoreCase));
        var hasService = serviceModule is not null;
        var resolvedServiceId = serviceModule?.ServiceId;

        // F87: evaluate dependency graph against currently-enabled plugins.
        // Returns empty when no deps declared or all satisfied. Frontend
        // can render each diagnostic as a warning banner on the plugin
        // card so the user knows why a plugin may not start.
        var depDiagnostics = PluginLoaderInternals.ValidateDependencies(
            p.Manifest?.Dependencies, enabledIds);

        // Resolve description from three ordered sources so every plugin
        // surfaces something meaningful in the Plugins settings page:
        //   1. Plugin-overridden IWdcPlugin.Description property (code-owned)
        //   2. plugin.json `description` field (metadata-owned, preferred
        //      because users can edit it without a rebuild)
        //   3. Fallback to the display name so the card never shows "null"
        var codeDescription = p.Instance.Description;
        var manifestDescription = p.Manifest?.Description;
        var description = !string.IsNullOrWhiteSpace(codeDescription)
            ? codeDescription
            : !string.IsNullOrWhiteSpace(manifestDescription)
                ? manifestDescription
                : $"{p.Instance.DisplayName} plugin";

        return new
        {
            id = p.Instance.Id,
            name = p.Instance.DisplayName,
            version = p.Instance.Version,
            type = hasService ? "service" : "tool",
            serviceId = resolvedServiceId,
            enabled = pluginState.IsEnabled(p.Instance.Id),
            description,
            author = p.Manifest?.Author ?? "NKS",
            license = p.Manifest?.License ?? "MIT",
            capabilities = p.Manifest?.Capabilities ?? Array.Empty<string>(),
            supportedPlatforms = p.Manifest?.SupportedPlatforms ?? Array.Empty<string>(),
            // F87: dependency graph exposed to UI. dependencies mirrors
            // the manifest verbatim; missingDependencies is the live
            // diagnostic against enabledIds (empty = OK).
            dependencies = p.Manifest?.Dependencies,
            missingDependencies = depDiagnostics,
            // F91.2: ALL UI surfaces owned by this plugin (nav entries,
            // site-edit tabs, dashboard cards, …). Always present even when
            // the plugin is disabled so the frontend can decide "this
            // surface is plugin-owned by X, is X currently on?" without any
            // hardcoded per-surface table.
            uiSurfaces = ExtractUiSurfaces(p.Instance),
            // F91.6: dynamic UI contributions rendered by <PluginSlot>.
            // Enabled plugins' contributions drive the actual render; for
            // disabled plugins the list is still returned so the shell can
            // decide "this slot has potential content, it's just off now".
            contributions = ExtractContributions(p.Instance),
            // Task 25b: optional UMD/ESM bundle URL for custom plugin pages.
            // Null when the plugin ships no custom UI.
            pageBundleUrl = p.Manifest?.PageBundleUrl,
            // Task 25b: static port defaults from plugin.json (secondary data
            // path — runtime DI IPortMetadata registrations are primary).
            manifestPorts = p.Manifest?.Ports?.Select(mp => new
            {
                key = mp.Key,
                label = mp.Label,
                defaultPort = mp.Default,
            }) ?? [],
        };
    }));
});

// Plugin marketplace — Phase 5 plan item.
// Fetches a JSON manifest from a configurable URL (NKS_WDC_MARKETPLACE_URL env or default)
// and returns the list of available plugins. Cross-references installed plugin ids so the
// UI can mark entries as "installed" / "update available". Graceful fallback to an empty
// list if the remote manifest is unreachable — the feature is best-effort, not critical path.
// Built-in fallback catalogue — used when no external marketplace is reachable.
// Every entry is a first-party plugin shipped with the NKS WDC source tree, so
// the list is always meaningful even on a fresh install with no network. When
// a real marketplace server comes online it takes precedence.
static IEnumerable<object> BuiltInMarketplaceCatalogue(HashSet<string> installedIds)
{
    var entries = new (string id, string name, string version, string description, string category)[]
    {
        ("nks.wdc.apache",  "Apache HTTP Server",  "1.0.0", "Bundled httpd with Scriban-generated vhosts, SSL via mkcert, per-site PHP FastCGI.", "Web Servers"),
        ("nks.wdc.caddy",   "Caddy",               "1.0.0", "Modern HTTP/2 + automatic HTTPS alternative to Apache, drives sites via Caddyfile fragments.", "Web Servers"),
        ("nks.wdc.nginx",   "Nginx",               "1.0.0", "High-performance HTTP server and reverse proxy — Scriban server-block templates, reload via SIGHUP, shares mkcert for TLS.", "Web Servers"),
        ("nks.wdc.php",     "PHP (Multi-version)", "1.0.0", "Multi-version PHP manager with per-version php.ini + extensions + CLI alias shims.", "Runtimes"),
        ("nks.wdc.node",    "Node.js",             "1.0.0", "Multi-version Node.js manager — detected binaries under ~/.wdc/binaries/node/ with shims for npm/npx and active-version switching.", "Runtimes"),
        ("nks.wdc.mysql",   "MySQL",               "1.0.0", "Managed MySQL server with DPAPI-protected root password, my.ini templates, database tooling.", "Databases"),
        ("nks.wdc.mariadb", "MariaDB",             "1.0.0", "Drop-in MySQL-compatible RDBMS — bundled mariadbd with DPAPI-protected root password, my.cnf templates. Alternative to MySQL for new installs.", "Databases"),
        ("nks.wdc.redis",   "Redis",               "1.0.0", "Redis cache server with managed redis.conf, graceful shutdown via redis-cli SHUTDOWN.", "Caches"),
        ("nks.wdc.mailpit", "Mailpit",             "1.0.0", "Local SMTP sink with web UI for development email testing.", "Mail"),
        ("nks.wdc.ssl",     "SSL (mkcert)",        "1.0.0", "Per-site TLS certificates via mkcert, local root CA install.", "Security"),
        ("nks.wdc.cloudflare", "Cloudflare (DNS + Tunnels)", "1.0.0", "Cloudflare DNS + Tunnel API integration — provisioned via account token, not bound to a binary service.", "Networking"),
        ("nks.wdc.hosts",   "Hosts Manager",       "1.0.0", "Windows hosts file manager with 5-backup rotation and managed block delimiters.", "System"),
        ("nks.wdc.composer", "Composer",           "1.0.0", "PHP dependency manager — per-site composer.json/lock management, framework auto-install, runs under active PHP version.", "Runtimes"),
    };
    foreach (var e in entries)
    {
        yield return new
        {
            id = e.id,
            name = e.name,
            version = e.version,
            description = e.description,
            downloadUrl = (string?)null,
            author = "NKS",
            license = "MIT",
            category = e.category,
            installed = installedIds.Contains(e.id),
            builtIn = true,
        };
    }
}

app.MapGet("/api/plugins/marketplace", async (IHttpClientFactory httpFactory) =>
{
    var marketplaceUrl = Environment.GetEnvironmentVariable("NKS_WDC_MARKETPLACE_URL")
        ?? "https://wdc.nks-hub.cz/plugins.json";

    var installedIds = new HashSet<string>(
        pluginLoader.Plugins.Select(p => p.Instance.Id),
        StringComparer.OrdinalIgnoreCase);

    try
    {
        using var client = httpFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(5);
        using var response = await client.GetAsync(marketplaceUrl);
        if (!response.IsSuccessStatusCode)
        {
            var fallback = BuiltInMarketplaceCatalogue(installedIds).ToList();
            return Results.Ok(new
            {
                source = "built-in",
                reachable = true,
                plugins = fallback,
                count = fallback.Count,
                error = $"Remote marketplace unreachable ({(int)response.StatusCode}) — showing built-in catalogue"
            });
        }

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var entries = new List<object>();
        if (doc.RootElement.TryGetProperty("plugins", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                var name = item.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? id : id;
                var version = item.TryGetProperty("version", out var vEl) ? vEl.GetString() ?? "" : "";
                var description = item.TryGetProperty("description", out var dEl) ? dEl.GetString() ?? "" : "";
                var downloadUrl = item.TryGetProperty("downloadUrl", out var duEl) ? duEl.GetString() ?? "" : "";
                var author = item.TryGetProperty("author", out var aEl) ? aEl.GetString() ?? "" : "";
                var license = item.TryGetProperty("license", out var lEl) ? lEl.GetString() ?? "" : "";
                entries.Add(new
                {
                    id,
                    name,
                    version,
                    description,
                    downloadUrl,
                    author,
                    license,
                    installed = installedIds.Contains(id),
                });
            }
        }

        return Results.Ok(new
        {
            source = marketplaceUrl,
            reachable = true,
            plugins = entries,
            count = entries.Count
        });
    }
    catch (Exception ex)
    {
        // Network/DNS/timeout — fall back to the built-in catalogue so the
        // user always has something to install from the Marketplace tab on a
        // fresh install with no network. The error field is surfaced by
        // PluginManager.vue as a warning banner.
        var fallback = BuiltInMarketplaceCatalogue(installedIds).ToList();
        return Results.Ok(new
        {
            source = "built-in",
            reachable = true,
            plugins = fallback,
            count = fallback.Count,
            error = $"Remote marketplace unreachable ({ex.Message}) — showing built-in catalogue"
        });
    }
});

// GET /api/plugins/ports — returns all IPortMetadata registrations from every loaded plugin.
// Plugins that do not register any IPortMetadata simply contribute nothing to the list.
// Returns an empty array [] when no plugin has opted in (safe for frontend to poll).
app.MapGet("/api/plugins/ports", (IServiceProvider sp) =>
{
    var ports = sp.GetServices<NKS.WebDevConsole.Core.Interfaces.IPortMetadata>();
    var dtos = ports.Select(p => new
    {
        key = p.Key,
        label = p.Label,
        pluginId = p.PluginId,
        defaultPort = p.DefaultPort,
        currentPort = p.CurrentPort,
        isActive = p.IsActive,
    });
    return Results.Ok(dtos);
});

app.MapGet("/api/plugins/{id}/ui", (string id, IServiceProvider sp) =>
{
    var plugin = pluginLoader.Plugins.FirstOrDefault(p => p.Instance.Id == id);
    if (plugin == null) return Results.NotFound();

    try
    {
        // Check if the plugin implements IFrontendPanelProvider via reflection (cross-ALC).
        // Must project panels by reflection too because PanelDef lives in the Core assembly
        // loaded under the plugin's ALC, not the daemon's default ALC — direct cast fails.
        var uiMethod = plugin.Instance.GetType().GetMethod("GetUiDefinition");
        if (uiMethod != null)
        {
            var def = uiMethod.Invoke(plugin.Instance, null);
            if (def != null)
            {
                var pidProp = def.GetType().GetProperty("PluginId");
                var catProp = def.GetType().GetProperty("Category");
                var iconProp = def.GetType().GetProperty("Icon");
                var panelsProp = def.GetType().GetProperty("Panels");

                var panelsOut = new List<object>();
                if (panelsProp?.GetValue(def) is System.Collections.IEnumerable panelsEnum)
                {
                    foreach (var p in panelsEnum)
                    {
                        if (p is null) continue;
                        var t = p.GetType();
                        var typeStr = t.GetProperty("Type")?.GetValue(p)?.ToString() ?? "";
                        var propsRaw = t.GetProperty("Props")?.GetValue(p);
                        // PanelDef.Props is IDictionary<string,object> — flatten to a plain
                        // dictionary so System.Text.Json serialises it as a JSON object.
                        var propsDict = new Dictionary<string, object?>();
                        if (propsRaw is System.Collections.IDictionary dict)
                        {
                            foreach (System.Collections.DictionaryEntry entry in dict)
                            {
                                propsDict[entry.Key?.ToString() ?? ""] = entry.Value;
                            }
                        }
                        panelsOut.Add(new { type = typeStr, props = propsDict });
                    }
                }

                // F91: project NavEntries (nullable array of NavContribution) across
                // the plugin ALC boundary using the same reflection idiom as panels.
                var navProp = def.GetType().GetProperty("NavEntries");
                var navOut = new List<object>();
                if (navProp?.GetValue(def) is System.Collections.IEnumerable navEnum)
                {
                    foreach (var n in navEnum)
                    {
                        if (n is null) continue;
                        var t = n.GetType();
                        navOut.Add(new
                        {
                            id = t.GetProperty("Id")?.GetValue(n)?.ToString() ?? "",
                            label = t.GetProperty("Label")?.GetValue(n)?.ToString() ?? "",
                            icon = t.GetProperty("Icon")?.GetValue(n)?.ToString() ?? "",
                            route = t.GetProperty("Route")?.GetValue(n)?.ToString() ?? "",
                            order = t.GetProperty("Order")?.GetValue(n) is int o ? o : 100
                        });
                    }
                }

                // If the plugin gave us an empty panel list, fall through to the default
                // (it's most likely a plugin scaffolding bug — don't punish the user with
                // a blank page).
                if (panelsOut.Count > 0)
                {
                    return Results.Ok(new
                    {
                        pluginId = pidProp?.GetValue(def)?.ToString() ?? id,
                        category = catProp?.GetValue(def)?.ToString() ?? "Services",
                        icon = iconProp?.GetValue(def)?.ToString() ?? "el-icon-setting",
                        panels = panelsOut,
                        navEntries = navOut
                    });
                }
            }
        }
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Cross-ALC reflection on GetUiDefinition failed for plugin {Id} — falling back to default UI", id);
    }

    // Fallback: generic UI schema for plugins that don't provide their own
    return Results.Ok(new
    {
        pluginId = id,
        category = "Services",
        icon = "el-icon-setting",
        panels = new[]
        {
            new { type = "service-status-card", props = (object)new { serviceId = id } },
            new { type = "log-viewer", props = (object)new { serviceId = id } }
        },
        navEntries = Array.Empty<object>()
    });
});

// F91 aggregator — single round-trip for the sidebar. Returns nav entries
// from every currently-enabled plugin, flattened + sorted by (category,
// order, label). Disabled plugins contribute nothing. Replaces the need
// for the frontend to fetch /api/plugins/{id}/ui N times when building
// the sidebar.
app.MapGet("/api/plugins/ui", (IServiceProvider sp, PluginState pluginState) =>
{
    var result = new List<object>();
    foreach (var plugin in pluginLoader.Plugins)
    {
        if (!pluginState.IsEnabled(plugin.Instance.Id)) continue;
        try
        {
            var uiMethod = plugin.Instance.GetType().GetMethod("GetUiDefinition");
            var def = uiMethod?.Invoke(plugin.Instance, null);
            if (def is null) continue;

            var pluginId = def.GetType().GetProperty("PluginId")?.GetValue(def)?.ToString()
                ?? plugin.Instance.Id;
            var category = def.GetType().GetProperty("Category")?.GetValue(def)?.ToString()
                ?? "Services";
            var fallbackIcon = def.GetType().GetProperty("Icon")?.GetValue(def)?.ToString()
                ?? "el-icon-setting";

            if (def.GetType().GetProperty("NavEntries")?.GetValue(def)
                is System.Collections.IEnumerable navEnum)
            {
                foreach (var n in navEnum)
                {
                    if (n is null) continue;
                    var t = n.GetType();
                    result.Add(new
                    {
                        pluginId,
                        category,
                        id = t.GetProperty("Id")?.GetValue(n)?.ToString() ?? "",
                        label = t.GetProperty("Label")?.GetValue(n)?.ToString() ?? "",
                        icon = (t.GetProperty("Icon")?.GetValue(n)?.ToString() is { Length: > 0 } ic)
                            ? ic : fallbackIcon,
                        route = t.GetProperty("Route")?.GetValue(n)?.ToString() ?? "",
                        order = t.GetProperty("Order")?.GetValue(n) is int o ? o : 100
                    });
                }
            }
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "F91 nav aggregation failed for plugin {Id}", plugin.Instance.Id);
        }
    }
    return Results.Ok(new { entries = result });
});

// Plugin install — Phase 7. Downloads a plugin .zip from a marketplace URL, validates,
// extracts into the plugins directory. Daemon restart required to load the new DLL
// because AssemblyLoadContext does not unload while assemblies are referenced.
// Returns { installed: true, restartRequired: true, path: "..." }.
app.MapPost("/api/plugins/install", async (HttpContext ctx, IHttpClientFactory httpFactory) =>
{
    Dictionary<string, string>? body;
    try
    {
        body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>();
    }
    catch (System.Text.Json.JsonException ex)
    {
        return Results.BadRequest(new { error = $"Invalid JSON body: {ex.Message}" });
    }
    var downloadUrl = body?.GetValueOrDefault("downloadUrl") ?? "";
    var pluginId = body?.GetValueOrDefault("id") ?? "";

    if (string.IsNullOrWhiteSpace(downloadUrl))
        return Results.BadRequest(new { error = "downloadUrl required" });
    if (string.IsNullOrWhiteSpace(pluginId))
        return Results.BadRequest(new { error = "id required" });

    // Validate plugin id — only [a-z0-9.-]+ to prevent directory traversal in extract path
    if (!System.Text.RegularExpressions.Regex.IsMatch(pluginId, @"^[a-zA-Z0-9._\-]{1,128}$"))
        return Results.BadRequest(new { error = "Invalid plugin id" });

    // Validate download URL — must be HTTPS (or HTTP for localhost dev)
    if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri))
        return Results.BadRequest(new { error = "Invalid downloadUrl" });
    var schemeOk = uri.Scheme == "https" ||
                   (uri.Scheme == "http" && (uri.Host == "localhost" || uri.Host == "127.0.0.1"));
    if (!schemeOk)
        return Results.BadRequest(new { error = "downloadUrl must be HTTPS (or HTTP localhost)" });

    var pluginsRoot = Path.GetFullPath(pluginDir);
    var targetDir = Path.GetFullPath(Path.Combine(pluginsRoot, pluginId));
    if (!targetDir.StartsWith(pluginsRoot, StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Resolved path escapes plugins root" });

    var cacheDir = Path.Combine(NKS.WebDevConsole.Core.Services.WdcPaths.CacheRoot, "plugin-installs");
    Directory.CreateDirectory(cacheDir);
    var tempZip = Path.Combine(cacheDir, $"{pluginId}-{Guid.NewGuid():N}.zip");

    try
    {
        using (var client = httpFactory.CreateClient())
        {
            client.Timeout = TimeSpan.FromMinutes(2);
            using var stream = await client.GetStreamAsync(uri);
            using var fs = File.Create(tempZip);
            await stream.CopyToAsync(fs);
        }

        // Extract to staging dir first, then atomic rename onto target
        var stagingDir = targetDir + ".staging";
        if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, recursive: true);
        Directory.CreateDirectory(stagingDir);

        using (var fs = File.OpenRead(tempZip))
        using (var zip = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Read))
        {
            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                if (entry.FullName.Contains("..")) continue; // zip-slip defense
                var dest = Path.GetFullPath(Path.Combine(stagingDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                if (!dest.StartsWith(Path.GetFullPath(stagingDir), StringComparison.OrdinalIgnoreCase))
                    continue;
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                using var entryStream = entry.Open();
                using var outFs = File.Create(dest);
                await entryStream.CopyToAsync(outFs);
            }
        }

        // Sanity check: at least one .dll matching the plugin id
        var dllExists = Directory.EnumerateFiles(stagingDir, $"*{pluginId}*.dll", SearchOption.AllDirectories).Any()
                     || Directory.EnumerateFiles(stagingDir, "*.dll", SearchOption.AllDirectories).Any();
        if (!dllExists)
        {
            Directory.Delete(stagingDir, recursive: true);
            return Results.BadRequest(new { error = "Archive contains no DLL" });
        }

        if (Directory.Exists(targetDir)) Directory.Delete(targetDir, recursive: true);
        Directory.Move(stagingDir, targetDir);

        return Results.Ok(new
        {
            installed = true,
            id = pluginId,
            path = targetDir,
            restartRequired = true,
            message = "Plugin extracted. Restart the daemon to load it."
        });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Install failed: {ex.Message}");
    }
    finally
    {
        try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
    }
});

// F91.4: uninstall a plugin. Must be disabled first (enabled plugins would
// have their DLL locked by the running ALC). Also refuses when another
// enabled plugin still depends on this one. Removes the plugin directory
// on disk — DLL stays loaded in the current process (AssemblyLoadContext
// doesn't unload while referenced), so the response flags restartRequired
// and the UI surfaces that note so users know to restart.
app.MapDelete("/api/plugins/{id}", (string id, PluginState pluginState) =>
{
    // Path safety: same [a-z0-9.-]+ rule as install so the computed plugin
    // directory can't escape the plugins root.
    if (!System.Text.RegularExpressions.Regex.IsMatch(id, @"^[a-zA-Z0-9._\-]{1,128}$"))
        return Results.BadRequest(new { error = "Invalid plugin id" });

    var plugin = pluginLoader.Plugins.FirstOrDefault(p => p.Instance.Id == id);
    if (plugin == null) return Results.NotFound(new { error = $"Plugin '{id}' not loaded" });

    if (pluginState.IsEnabled(id))
    {
        return Results.BadRequest(new
        {
            error = "plugin-is-enabled",
            id,
            message = "Disable the plugin before uninstalling.",
        });
    }

    // Refuse if any enabled plugin would lose a dependency. Uses the same
    // traversal as /disable so the UX is consistent.
    var dependents = new List<string>();
    foreach (var other in pluginLoader.Plugins)
    {
        if (other.Instance.Id == id) continue;
        if (!pluginState.IsEnabled(other.Instance.Id)) continue;
        var deps = other.Manifest?.Dependencies;
        if (deps is null) continue;
        var hardHit = deps.Hard?.Any(h => string.Equals(h, id, StringComparison.OrdinalIgnoreCase)) == true;
        if (hardHit) dependents.Add(other.Instance.Id);
    }
    if (dependents.Count > 0)
    {
        return Results.BadRequest(new { error = "has-dependents", id, dependents });
    }

    // F91.7: collect all files belonging to this plugin. Two layouts are
    // supported side-by-side:
    //   A) per-plugin subfolder (what /install writes):
    //        {pluginDir}/{id}/*
    //   B) flat build output (what the solution emits + user typically ships):
    //        {pluginDir}/{AssemblyName}.dll + .pdb + .deps.json + .runtimeconfig.json
    // Layout B is detected via the already-loaded Assembly.Location, which
    // is the authoritative source of where the DLL came from, and we
    // gather its sibling artifacts by stripping extensions.
    var targetsToDelete = new List<string>();
    string? targetDesc = null;
    try
    {
        var pluginsRoot = Path.GetFullPath(pluginDir);

        // A) dedicated folder
        var candidateDir = Path.GetFullPath(Path.Combine(pluginsRoot, id));
        if (candidateDir.StartsWith(pluginsRoot, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(candidateDir))
        {
            targetsToDelete.AddRange(Directory.EnumerateFiles(candidateDir, "*", SearchOption.AllDirectories));
            targetDesc = candidateDir;
        }

        // A.bis) plugin.json-based folder lookup
        if (targetsToDelete.Count == 0)
        {
            foreach (var dir in Directory.EnumerateDirectories(pluginsRoot))
            {
                var manifest = Path.Combine(dir, "plugin.json");
                if (!File.Exists(manifest)) continue;
                try
                {
                    var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifest));
                    if (doc.RootElement.TryGetProperty("id", out var idProp)
                        && string.Equals(idProp.GetString(), id, StringComparison.OrdinalIgnoreCase))
                    {
                        targetsToDelete.AddRange(Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories));
                        targetDesc = dir;
                        break;
                    }
                }
                catch { /* malformed manifest — skip */ }
            }
        }

        // B) flat layout — use the loaded assembly's location to find DLL
        // + siblings (pdb, deps.json, runtimeconfig.json, xml doc). Guard
        // against symlinks / location outside pluginsRoot so we never delete
        // unrelated files sitting somewhere else on disk.
        if (targetsToDelete.Count == 0)
        {
            try
            {
                var asmPath = plugin.Assembly.Location;
                if (!string.IsNullOrEmpty(asmPath) && File.Exists(asmPath))
                {
                    var asmFull = Path.GetFullPath(asmPath);
                    if (asmFull.StartsWith(pluginsRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        var baseName = Path.GetFileNameWithoutExtension(asmPath);
                        var dir = Path.GetDirectoryName(asmFull)!;
                        foreach (var f in Directory.EnumerateFiles(dir, baseName + ".*"))
                            targetsToDelete.Add(f);
                        targetDesc = asmFull;
                    }
                }
            }
            catch { /* some assemblies lack Location (in-memory loads) */ }
        }
    }
    catch (Exception ex)
    {
        return Results.Problem($"Uninstall lookup failed: {ex.Message}");
    }

    if (targetsToDelete.Count == 0)
    {
        return Results.Ok(new
        {
            uninstalled = false,
            id,
            restartRequired = true,
            message = "Plugin files not found on disk; DLL stays loaded until daemon restart.",
        });
    }

    int deleted = 0, locked = 0;
    foreach (var file in targetsToDelete)
    {
        try { File.Delete(file); deleted++; }
        catch { locked++; }
    }
    // Try removing an empty folder if we were operating on layout A
    if (targetDesc is not null && Directory.Exists(targetDesc))
    {
        try { Directory.Delete(targetDesc, recursive: true); } catch { /* leftover locked files */ }
    }

    // F91.9: record the uninstall on disk so the next daemon boot skips
    // loading this plugin AND purges any leftover locked files that we
    // couldn't delete here. Without this the plugin gets reloaded on
    // restart because the DLL survived on disk, and it reappears in the
    // UI as if nothing happened.
    pluginState.MarkUninstalled(id);

    return Results.Ok(new
    {
        uninstalled = true,
        id,
        path = targetDesc,
        deletedFiles = deleted,
        lockedFiles = locked,
        restartRequired = locked > 0 || plugin.Assembly.Location is { Length: > 0 },
        message = locked == 0
            ? $"Plugin files removed ({deleted} file(s))."
            : $"Removed {deleted} file(s); {locked} were locked by the running daemon — restart to fully unload.",
    });
});

// F91.12: restore a previously uninstalled built-in plugin. Removes the
// id from the uninstall blacklist so the next daemon boot loads it —
// but only IF the DLL is still on disk. For built-in plugins that were
// purged (locked DLL deleted on restart), the user has to rebuild the
// solution first (dev) or reinstall from catalog (prod). We detect the
// situation and tell the UI instead of silently failing.
app.MapPost("/api/plugins/restore/{id}", (string id, PluginState pluginState) =>
{
    if (!System.Text.RegularExpressions.Regex.IsMatch(id, @"^[a-zA-Z0-9._\-]{1,128}$"))
        return Results.BadRequest(new { error = "Invalid plugin id" });

    // Check whether any DLL still matches the plugin id (either via
    // plugin.json in a subfolder or NKS.WebDevConsole.Plugin.*.dll flat).
    bool dllPresent = false;
    try
    {
        var pluginsRoot = Path.GetFullPath(pluginDir);
        if (Directory.Exists(pluginsRoot))
        {
            // Subfolder layout
            var idDir = Path.Combine(pluginsRoot, id);
            if (Directory.Exists(idDir) &&
                Directory.EnumerateFiles(idDir, "*.dll", SearchOption.AllDirectories).Any())
            {
                dllPresent = true;
            }
            // Flat layout — match by assembly name suffix (e.g. NKS.WebDevConsole.Plugin.Caddy.dll)
            if (!dllPresent)
            {
                var shortName = id.Split('.').LastOrDefault() ?? id;
                foreach (var dll in Directory.EnumerateFiles(pluginsRoot, "NKS.WebDevConsole.Plugin.*.dll"))
                {
                    if (Path.GetFileNameWithoutExtension(dll).EndsWith("." + shortName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        dllPresent = true;
                        break;
                    }
                }
            }
        }
    }
    catch { /* any IO glitch — fall through to "needs rebuild" branch */ }

    pluginState.ClearUninstalled(id);

    if (!dllPresent)
    {
        return Results.Ok(new
        {
            restored = false,
            id,
            restartRequired = false,
            rebuildRequired = true,
            message = "Plugin DLL není na disku. V dev módu spusť `dotnet build`, "
                    + "pak klikni Obnovit znovu. V produkci stáhni plugin z katalogu.",
        });
    }

    // Fire restart so the loader re-scans with the cleared blacklist.
    _ = Task.Run(async () =>
    {
        await Task.Delay(300);
        try
        {
            var pf = Path.Combine(Path.GetTempPath(), "nks-wdc-daemon.port");
            if (File.Exists(pf)) File.Delete(pf);
        }
        catch { }
        Environment.Exit(99);
    });

    return Results.Ok(new
    {
        restored = true,
        id,
        restartRequired = true,
        rebuildRequired = false,
        message = "Plugin obnoven. Restartuji daemon…",
    });
});

// Plugin brand icon: streams embedded SVG resource from plugin DLL
app.MapGet("/api/plugins/{id}/icon", (string id) =>
{
    // Accept either full plugin id (nks.wdc.apache) or short service id (apache)
    var plugin = pluginLoader.Plugins.FirstOrDefault(p =>
        p.Instance.Id.Equals(id, StringComparison.OrdinalIgnoreCase)
        || p.Instance.Id.EndsWith("." + id, StringComparison.OrdinalIgnoreCase)
        || p.Instance.Id.Split('.').Last().Equals(id, StringComparison.OrdinalIgnoreCase));

    if (plugin is null)
        return Results.NotFound();

    var asm = plugin.Instance.GetType().Assembly;
    // Resource name pattern: {AssemblyName}.Resources.icon.svg
    var resourceName = asm.GetManifestResourceNames()
        .FirstOrDefault(r => r.EndsWith(".Resources.icon.svg", StringComparison.OrdinalIgnoreCase)
                          || r.EndsWith(".icon.svg", StringComparison.OrdinalIgnoreCase));
    if (resourceName is null)
        return Results.NotFound();

    using var stream = asm.GetManifestResourceStream(resourceName);
    if (stream is null)
        return Results.NotFound();

    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return Results.File(ms.ToArray(), "image/svg+xml");
});

// Service management endpoints — query real plugin modules
app.MapGet("/api/services", async (IServiceProvider sp) =>
{
    // Fan out status probes concurrently — same rationale as /api/system
    // (commit 9cf5d53). This endpoint is hit by every AppSidebar poll (5s
    // cadence) so the sequential foreach was the per-poll floor for the
    // whole services pane.
    var modules = sp.GetServices<IServiceModule>().ToArray();
    var statuses = await Task.WhenAll(
        modules.Select(m => m.GetStatusAsync(CancellationToken.None)));
    return Results.Ok(statuses);
});

app.MapGet("/api/services/{id}", async (string id, IServiceProvider sp) =>
{
    var modules = sp.GetServices<IServiceModule>();
    var module = modules.FirstOrDefault(m => m.ServiceId.Equals(id, StringComparison.OrdinalIgnoreCase)
        || m.Type.ToString().Equals(id, StringComparison.OrdinalIgnoreCase));
    if (module == null) return Results.NotFound();
    var status = await module.GetStatusAsync(CancellationToken.None);
    return Results.Ok(status);
});

app.MapPost("/api/services/{id}/start", async (string id, IServiceProvider sp, ILoggerFactory lf) =>
{
    var modules = sp.GetServices<IServiceModule>();
    var module = modules.FirstOrDefault(m => m.ServiceId.Equals(id, StringComparison.OrdinalIgnoreCase)
        || m.Type.ToString().Equals(id, StringComparison.OrdinalIgnoreCase));
    if (module == null) return Results.NotFound(new { error = $"No service module for '{id}'" });

    try
    {
        await module.StartAsync(CancellationToken.None);
        var status = await module.GetStatusAsync(CancellationToken.None);
        return Results.Ok(status);
    }
    catch (Exception ex)
    {
        lf.CreateLogger("ServiceControl").LogError(ex, "Failed to start service {Id}", id);
        return Results.Problem(
            title: $"Failed to start {id}",
            detail: ex.Message,
            statusCode: 500);
    }
});

app.MapPost("/api/services/{id}/stop", async (string id, IServiceProvider sp, ILoggerFactory lf) =>
{
    var modules = sp.GetServices<IServiceModule>();
    var module = modules.FirstOrDefault(m => m.ServiceId.Equals(id, StringComparison.OrdinalIgnoreCase)
        || m.Type.ToString().Equals(id, StringComparison.OrdinalIgnoreCase));
    if (module == null) return Results.NotFound(new { error = $"No service module for '{id}'" });

    try
    {
        await module.StopAsync(CancellationToken.None);
        var status = await module.GetStatusAsync(CancellationToken.None);
        return Results.Ok(status);
    }
    catch (Exception ex)
    {
        lf.CreateLogger("ServiceControl").LogError(ex, "Failed to stop service {Id}", id);
        return Results.Problem(
            title: $"Failed to stop {id}",
            detail: ex.Message,
            statusCode: 500);
    }
});

app.MapPost("/api/services/{id}/restart", async (string id, IServiceProvider sp, ILoggerFactory lf) =>
{
    var modules = sp.GetServices<IServiceModule>();
    var module = modules.FirstOrDefault(m => m.ServiceId.Equals(id, StringComparison.OrdinalIgnoreCase)
        || m.Type.ToString().Equals(id, StringComparison.OrdinalIgnoreCase));
    if (module == null) return Results.NotFound(new { error = $"No service module for '{id}'" });

    try
    {
        await module.StopAsync(CancellationToken.None);
        await module.StartAsync(CancellationToken.None);
        var status = await module.GetStatusAsync(CancellationToken.None);
        return Results.Ok(status);
    }
    catch (Exception ex)
    {
        lf.CreateLogger("ServiceControl").LogError(ex, "Failed to restart service {Id}", id);
        return Results.Problem(
            title: $"Failed to restart {id}",
            detail: ex.Message,
            statusCode: 500);
    }
});

// Per-site Node.js process management — delegates to NodeModule via reflection
// because the plugin is in an isolated ALC. These endpoints let the frontend
// start/stop/restart individual site processes without touching the aggregate
// "node" service module.
app.MapGet("/api/node/sites", (IServiceProvider sp) =>
{
    var modules = sp.GetServices<IServiceModule>();
    var nodeModule = modules.FirstOrDefault(m => m.ServiceId.Equals("node", StringComparison.OrdinalIgnoreCase));
    if (nodeModule == null) return Results.Ok(Array.Empty<object>());

    // Use GetMethod to tolerate missing method (stale plugin, signature drift).
    var listMethod = nodeModule.GetType().GetMethod("ListSiteProcesses");
    if (listMethod == null) return Results.Ok(Array.Empty<object>());

    try
    {
        var result = listMethod.Invoke(nodeModule, null);
        return Results.Ok(result);
    }
    catch (Exception)
    {
        return Results.Ok(Array.Empty<object>());
    }
});

// Reflection helper — invokes a named method on NodeModule across the plugin
// ALC boundary. Fails loudly with a descriptive error when the method is
// missing (stale plugin DLL, signature drift) instead of NRE-ing through
// the null-forgiving operator.
static async Task<object?> InvokeNodeMethodAsync(object module, string methodName, object[] args)
{
    var method = module.GetType().GetMethod(methodName)
        ?? throw new MissingMethodException(module.GetType().FullName, methodName);
    object? result;
    try
    {
        result = method.Invoke(module, args);
    }
    catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException is not null)
    {
        // Unwrap so the real plugin error reaches the HTTP response.
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
        throw; // unreachable
    }
    if (result is Task task)
    {
        await task.ConfigureAwait(false);
        var resultProp = task.GetType().GetProperty("Result");
        return resultProp?.GetValue(task);
    }
    return result;
}

app.MapPost("/api/node/sites/{domain}/start", async (string domain, IServiceProvider sp, ILoggerFactory lf) =>
{
    var modules = sp.GetServices<IServiceModule>();
    var nodeModule = modules.FirstOrDefault(m => m.ServiceId.Equals("node", StringComparison.OrdinalIgnoreCase));
    if (nodeModule == null) return Results.NotFound(new { error = "Node.js plugin not loaded" });

    var sm = sp.GetRequiredService<SiteManager>();
    if (!sm.Sites.TryGetValue(domain, out var site)) return Results.NotFound(new { error = $"Site '{domain}' not found" });
    if (site.NodeUpstreamPort == 0) return Results.BadRequest(new { error = "Site is not configured as a Node.js site" });

    try
    {
        await InvokeNodeMethodAsync(nodeModule, "StartSiteAsync",
            new object[] { domain, site.DocumentRoot, site.NodeUpstreamPort, site.NodeStartCommand ?? "", CancellationToken.None });
        var status = await InvokeNodeMethodAsync(nodeModule, "GetSiteStatus", new object[] { domain });
        return Results.Ok(status);
    }
    catch (Exception ex)
    {
        lf.CreateLogger("NodeControl").LogError(ex, "Failed to start Node for {Domain}", domain);
        return Results.Problem(title: $"Failed to start Node for {domain}", detail: ex.Message, statusCode: 500);
    }
});

app.MapPost("/api/node/sites/{domain}/stop", async (string domain, IServiceProvider sp, ILoggerFactory lf) =>
{
    var modules = sp.GetServices<IServiceModule>();
    var nodeModule = modules.FirstOrDefault(m => m.ServiceId.Equals("node", StringComparison.OrdinalIgnoreCase));
    if (nodeModule == null) return Results.NotFound(new { error = "Node.js plugin not loaded" });

    try
    {
        await InvokeNodeMethodAsync(nodeModule, "StopSiteAsync", new object[] { domain, CancellationToken.None });
        return Results.Ok(new { ok = true, domain });
    }
    catch (Exception ex)
    {
        lf.CreateLogger("NodeControl").LogError(ex, "Failed to stop Node for {Domain}", domain);
        return Results.Problem(title: $"Failed to stop Node for {domain}", detail: ex.Message, statusCode: 500);
    }
});

app.MapPost("/api/node/sites/{domain}/restart", async (string domain, IServiceProvider sp, ILoggerFactory lf) =>
{
    var modules = sp.GetServices<IServiceModule>();
    var nodeModule = modules.FirstOrDefault(m => m.ServiceId.Equals("node", StringComparison.OrdinalIgnoreCase));
    if (nodeModule == null) return Results.NotFound(new { error = "Node.js plugin not loaded" });

    var sm = sp.GetRequiredService<SiteManager>();
    if (!sm.Sites.TryGetValue(domain, out var site)) return Results.NotFound(new { error = $"Site '{domain}' not found" });

    try
    {
        await InvokeNodeMethodAsync(nodeModule, "StopSiteAsync", new object[] { domain, CancellationToken.None });
        await InvokeNodeMethodAsync(nodeModule, "StartSiteAsync",
            new object[] { domain, site.DocumentRoot, site.NodeUpstreamPort, site.NodeStartCommand ?? "", CancellationToken.None });
        var status = await InvokeNodeMethodAsync(nodeModule, "GetSiteStatus", new object[] { domain });
        return Results.Ok(status);
    }
    catch (Exception ex)
    {
        lf.CreateLogger("NodeControl").LogError(ex, "Failed to restart Node for {Domain}", domain);
        return Results.Problem(title: $"Failed to restart Node for {domain}", detail: ex.Message, statusCode: 500);
    }
});

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
if (skipStartupReapply)
{
    Console.WriteLine($"[startup-bg] WDC_SKIP_STARTUP_REAPPLY=1 — skipping re-apply of {startupSitesSnapshot.Count} site(s); use UI to re-apply if needed");
}
else
{
    _ = Task.Run(async () =>
    {
        foreach (var siteToApply in startupSitesSnapshot)
        {
            try
            {
                Console.WriteLine($"[startup-bg] re-applying site {siteToApply.Domain}...");
                var applyTask = startupOrchestrator.ApplyAsync(siteToApply);
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(25));
                var completed = await Task.WhenAny(applyTask, timeoutTask);
                if (completed == timeoutTask)
                {
                    Console.WriteLine($"[startup-bg] site {siteToApply.Domain} re-apply timed out after 25s — skipped; re-apply via UI");
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
        Console.WriteLine("[startup-bg] all site re-apply complete");
    });
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
var autoStartEnabled = app.Services.GetRequiredService<SettingsStore>().AutoStartEnabled;
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
            await module.StartAsync(CancellationToken.None);
            var status = await module.GetStatusAsync(CancellationToken.None);
            Console.WriteLine($"[auto-start] {status.Id}: {status.State}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[auto-start] {module.ServiceId} failed: {ex.Message}");
        }
    });
    await Task.WhenAll(startTasks);
}

// Start the backup scheduler — reads backup.scheduleHours from SettingsStore
// and creates timestamped zip backups on a timer. Dormant when set to 0.
app.Services.GetRequiredService<BackupScheduler>().Start();

app.MapGet("/api/sites", (SiteManager sm) => Results.Ok(sm.Sites.Values));

app.MapGet("/api/sites/{domain}", (string domain, SiteManager sm) =>
{
    var site = sm.Get(domain);
    return site is not null ? Results.Ok(site) : Results.NotFound();
});

// Docker Compose detection — returns whether the site's document root
// contains a compose file and, if so, which one. Used by the frontend
// to show a "Compose" badge and (in future iterations) surface lifecycle
// controls. Kept as a separate endpoint rather than inline on /api/sites
// so the cheap TOML listing doesn't have to hit the filesystem per site.
app.MapGet("/api/sites/{domain}/docker-compose", (string domain, SiteManager sm) =>
{
    var site = sm.Get(domain);
    if (site is null) return Results.NotFound();

    var composePath = DockerComposeDetector.FindComposeFile(site.DocumentRoot);
    return Results.Ok(new
    {
        hasCompose = composePath is not null,
        composeFile = composePath,
        fileName = composePath is not null ? Path.GetFileName(composePath) : null,
    });
});

// Docker Compose lifecycle — up/down/restart/ps/logs for sites with compose files.
app.MapPost("/api/sites/{domain}/docker-compose/up", async (string domain, SiteManager sm) =>
{
    var site = sm.Get(domain);
    if (site is null) return Results.NotFound();
    if (!DockerComposeDetector.HasCompose(site.DocumentRoot))
        return Results.BadRequest(new { error = "No compose file in document root" });
    var result = await DockerComposeRunner.UpAsync(site.DocumentRoot);
    return result.Success ? Results.Ok(new { ok = true, output = result.Output })
        : Results.Json(new { ok = false, exitCode = result.ExitCode, output = result.Output }, statusCode: 500);
});

app.MapPost("/api/sites/{domain}/docker-compose/down", async (string domain, SiteManager sm) =>
{
    var site = sm.Get(domain);
    if (site is null) return Results.NotFound();
    if (!DockerComposeDetector.HasCompose(site.DocumentRoot))
        return Results.BadRequest(new { error = "No compose file in document root" });
    var result = await DockerComposeRunner.DownAsync(site.DocumentRoot);
    return result.Success ? Results.Ok(new { ok = true, output = result.Output })
        : Results.Json(new { ok = false, exitCode = result.ExitCode, output = result.Output }, statusCode: 500);
});

app.MapPost("/api/sites/{domain}/docker-compose/restart", async (string domain, SiteManager sm) =>
{
    var site = sm.Get(domain);
    if (site is null) return Results.NotFound();
    if (!DockerComposeDetector.HasCompose(site.DocumentRoot))
        return Results.BadRequest(new { error = "No compose file in document root" });
    var result = await DockerComposeRunner.RestartAsync(site.DocumentRoot);
    return result.Success ? Results.Ok(new { ok = true, output = result.Output })
        : Results.Json(new { ok = false, exitCode = result.ExitCode, output = result.Output }, statusCode: 500);
});

app.MapGet("/api/sites/{domain}/docker-compose/ps", async (string domain, SiteManager sm) =>
{
    var site = sm.Get(domain);
    if (site is null) return Results.NotFound();
    if (!DockerComposeDetector.HasCompose(site.DocumentRoot))
        return Results.BadRequest(new { error = "No compose file in document root" });
    var result = await DockerComposeRunner.PsAsync(site.DocumentRoot);
    return Results.Ok(new { ok = result.Success, output = result.Output });
});

app.MapGet("/api/sites/{domain}/docker-compose/logs", async (string domain, SiteManager sm) =>
{
    var site = sm.Get(domain);
    if (site is null) return Results.NotFound();
    if (!DockerComposeDetector.HasCompose(site.DocumentRoot))
        return Results.BadRequest(new { error = "No compose file in document root" });
    var result = await DockerComposeRunner.LogsAsync(site.DocumentRoot);
    return Results.Ok(new { ok = result.Success, output = result.Output });
});

// Access log metrics — Phase 11 Performance monitoring foothold.
// Looks at each installed Apache version's logs/ directory for the
// site's per-domain access log (the vhost template always writes to
// ${APACHE_LOG_DIR}/{domain}-access.log or -ssl-access.log). Returns
// file size, line count, and last-write timestamp so the UI can show
// "1.2 MB · 15k requests · last hit 3m ago" without a full log parser.
app.MapGet("/api/sites/{domain}/metrics", (string domain, SiteManager sm, BinaryManager bm) =>
{
    var site = sm.Get(domain);
    if (site is null) return Results.NotFound();

    // Build candidate paths from every installed Apache version.
    // The vhost template writes access logs under that version's logs/
    // subdirectory with {domain}-access.log / {domain}-ssl-access.log.
    var candidates = new List<string>();
    foreach (var apache in bm.ListInstalled("apache"))
    {
        var logsDir = Path.Combine(apache.InstallPath, "logs");
        candidates.Add(Path.Combine(logsDir, $"{domain}-access.log"));
        candidates.Add(Path.Combine(logsDir, $"{domain}-ssl-access.log"));
    }

    var accessStats = AccessLogInspector.Inspect(candidates);
    return Results.Ok(new
    {
        domain,
        hasMetrics = accessStats is not null,
        accessLog = accessStats is null ? null : new
        {
            path = accessStats.Path,
            sizeBytes = accessStats.SizeBytes,
            requestCount = accessStats.LineCount,
            lastWriteUtc = accessStats.LastWrittenUtc,
        },
    });
});

// Phase 7.1: historical access log aggregation.
// Reads the full set of rotated access log files for a given calendar day and
// buckets them into granularity-sized time slots (1m / 5m / 15m / 1h).
// Returns three series — requests, bytes, errors — covering 00:00–23:59 in
// the daemon's local time zone. All buckets are always present (value 0 when
// no traffic). If no log files exist for the requested day, all series are
// returned with zeros.
//
// GET /api/sites/{domain}/metrics/historical
//   ?date=YYYY-MM-DD   (default: today in local time)
//   &granularity=5m    (1m | 5m | 15m | 1h, default 5m)
app.MapGet("/api/sites/{domain}/metrics/historical", (
    string domain,
    string? date,
    string? granularity,
    SiteManager sm,
    BinaryManager bm) =>
{
    var site = sm.Get(domain);
    if (site is null) return Results.NotFound();

    // Parse date — default to today in local time
    DateOnly requestedDate;
    if (date is null)
    {
        requestedDate = DateOnly.FromDateTime(DateTime.Now);
    }
    else if (!DateOnly.TryParseExact(date, "yyyy-MM-dd",
             System.Globalization.CultureInfo.InvariantCulture,
             System.Globalization.DateTimeStyles.None, out requestedDate))
    {
        return Results.BadRequest(new { error = "Invalid date format. Expected YYYY-MM-DD." });
    }

    var gran = AccessLogAggregator.ParseGranularity(granularity ?? "5m");
    if (gran is null)
        return Results.BadRequest(new { error = "Invalid granularity. Use 1m, 5m, 15m, or 1h." });

    // Build base log path candidates from every installed Apache version.
    // AccessLogAggregator.Aggregate will discover rotated siblings alongside
    // each base path automatically.
    var basePaths = new List<string>();
    foreach (var apache in bm.ListInstalled("apache"))
    {
        var logsDir = Path.Combine(apache.InstallPath, "logs");
        if (site.SslEnabled)
        {
            basePaths.Add(Path.Combine(logsDir, $"{domain}-ssl-access.log"));
            basePaths.Add(Path.Combine(logsDir, $"{domain}-access.log"));
        }
        else
        {
            basePaths.Add(Path.Combine(logsDir, $"{domain}-access.log"));
            basePaths.Add(Path.Combine(logsDir, $"{domain}-ssl-access.log"));
        }
    }

    try
    {
        var result = AccessLogAggregator.Aggregate(basePaths, requestedDate, gran.Value);

        return Results.Ok(new
        {
            date = result.Date.ToString("yyyy-MM-dd"),
            granularity = granularity ?? "5m",
            bucketCount = result.BucketCount,
            series = result.Series.Select(s => new
            {
                name = s.Name,
                data = s.Data.Select(p => new
                {
                    ts = p.Timestamp,
                    value = p.Value,
                }),
            }),
        });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Failed to aggregate metrics: {ex.Message}");
    }
});

// Per-site recent access log tail. Returns the last N parsed entries in
// Combined Log Format order (oldest → newest). Powers the SiteEdit
// "Recent visitors" panel: IP, path, status, timestamp, user agent.
app.MapGet("/api/sites/{domain}/access-log", (string domain, int? limit, SiteManager sm, BinaryManager bm) =>
{
    var site = sm.Get(domain);
    if (site is null) return Results.NotFound();

    var clamped = Math.Clamp(limit ?? 100, 1, 1000);
    var candidates = new List<string>();
    foreach (var apache in bm.ListInstalled("apache"))
    {
        var logsDir = Path.Combine(apache.InstallPath, "logs");
        // Prefer the SSL log for HTTPS sites — it contains the real
        // traffic once the plaintext vhost turns into a redirect stub.
        if (site.SslEnabled)
        {
            candidates.Add(Path.Combine(logsDir, $"{domain}-ssl-access.log"));
            candidates.Add(Path.Combine(logsDir, $"{domain}-access.log"));
        }
        else
        {
            candidates.Add(Path.Combine(logsDir, $"{domain}-access.log"));
            candidates.Add(Path.Combine(logsDir, $"{domain}-ssl-access.log"));
        }
    }

    try
    {
        var entries = AccessLogInspector.Tail(candidates, clamped);
        var top = entries
            .GroupBy(e => e.RemoteAddr)
            .Select(g => new { ip = g.Key, hits = g.Count() })
            .OrderByDescending(x => x.hits)
            .Take(10)
            .ToList();
        return Results.Ok(new
        {
            domain,
            count = entries.Count,
            topClients = top,
            entries = entries.Select(e => new
            {
                timestamp = e.TimestampUtc,
                ip = e.RemoteAddr,
                method = e.Method,
                path = e.Path,
                protocol = e.Protocol,
                status = e.Status,
                bytes = e.ResponseBytes,
                referer = e.Referer,
                userAgent = e.UserAgent,
            }),
        });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Failed to read access log: {ex.Message}");
    }
});

// Phase 8.1 error log aggregation — Apache error_log + PHP-FPM error_log.
// Discovers per-site and global error logs from every installed Apache version
// and from the PHP plugin's managed log directory. Returns a unified list of
// parsed entries (timestamp, severity, source, message) sorted newest-first.
// Graceful: missing files return an empty list, never an error.
app.MapGet("/api/sites/{domain}/logs/errors", (
    string domain,
    int? lines,
    DateTimeOffset? since,
    SiteManager sm,
    BinaryManager bm) =>
{
    var site = sm.Get(domain);
    if (site is null) return Results.NotFound();

    var limit = Math.Clamp(lines ?? 100, 1, 1000);

    // Build Apache error log candidate list — per every installed Apache version
    // the vhost template writes {domain}-error.log / {domain}-ssl-error.log.
    // We also check the global error.log for context (e.g. startup/config errors).
    var apacheCandidates = new List<string>();
    foreach (var apache in bm.ListInstalled("apache"))
    {
        var logsDir = Path.Combine(apache.InstallPath, "logs");
        apacheCandidates.Add(Path.Combine(logsDir, $"{domain}-error.log"));
        apacheCandidates.Add(Path.Combine(logsDir, $"{domain}-ssl-error.log"));
        apacheCandidates.Add(Path.Combine(logsDir, "error.log"));
    }
    // Fallback: WdcPaths.LogsRoot/apache/error.log (daemon's own Apache log dir)
    apacheCandidates.Add(Path.Combine(WdcPaths.LogsRoot, "apache", "error.log"));

    // Build PHP error log candidate list — per-version FPM global error log
    // (php{versionTag}-fpm-error.log) and per-version web error log
    // (php{majorMinor}-errors.log). Scan all available version dirs under
    // ~/.wdc/logs/php/ rather than hard-coding specific versions.
    var phpFpmCandidates = new List<string>();
    var phpWebCandidates = new List<string>();
    var phpLogDir = Path.Combine(WdcPaths.LogsRoot, "php");
    if (Directory.Exists(phpLogDir))
    {
        foreach (var f in Directory.EnumerateFiles(phpLogDir, "*-fpm-error.log"))
            phpFpmCandidates.Add(f);
        foreach (var f in Directory.EnumerateFiles(phpLogDir, "php*-errors.log"))
            phpWebCandidates.Add(f);
    }

    try
    {
        var entries = ErrorLogInspector.TailMultiple(
        [
            (apacheCandidates, "apache-error"),
            (phpFpmCandidates, "php-fpm-error"),
            (phpWebCandidates, "php-error"),
        ],
        limit,
        since);

        return Results.Ok(entries.Select(e => new
        {
            timestamp = e.Timestamp,
            severity = e.Severity,
            source = e.Source,
            message = e.Message,
            pid = e.Pid,
            client = e.Client,
        }));
    }
    catch (Exception ex)
    {
        return Results.Problem($"Failed to read error logs: {ex.Message}");
    }
});

// Phase 8.2 access log aggregation — Apache access_log per site.
// Discovers per-site access logs from every installed Apache version
// ({domain}-access.log + ssl variant). Returns a parsed list of Combined
// Log Format entries sorted newest-first. Graceful: missing files return
// an empty list, never an error.
app.MapGet("/api/sites/{domain}/logs/access", (
    string domain,
    int? lines,
    DateTimeOffset? since,
    SiteManager sm,
    BinaryManager bm) =>
{
    var site = sm.Get(domain);
    if (site is null) return Results.NotFound();

    var limit = Math.Clamp(lines ?? 100, 1, 1000);

    var candidates = new List<string>();
    foreach (var apache in bm.ListInstalled("apache"))
    {
        var logsDir = Path.Combine(apache.InstallPath, "logs");
        candidates.Add(Path.Combine(logsDir, $"{domain}-access.log"));
        candidates.Add(Path.Combine(logsDir, $"{domain}-ssl-access.log"));
    }

    try
    {
        var raw = AccessLogInspector.Tail(candidates, limit, 512 * 1024);

        // Tail returns oldest→newest; reverse for newest-first response.
        var entries = raw
            .Select(e => new AccessEntry(
                Timestamp: new DateTimeOffset(e.TimestampUtc, TimeSpan.Zero),
                RemoteIp: e.RemoteAddr,
                RealIp: e.EffectiveClientIp,
                Method: string.IsNullOrEmpty(e.Method) ? null : e.Method,
                Path: string.IsNullOrEmpty(e.Path) ? null : e.Path,
                Protocol: string.IsNullOrEmpty(e.Protocol) ? null : e.Protocol,
                Status: e.Status,
                Bytes: e.ResponseBytes,
                Referer: string.IsNullOrEmpty(e.Referer) ? null : e.Referer,
                UserAgent: string.IsNullOrEmpty(e.UserAgent) ? null : e.UserAgent,
                XForwardedFor: e.XForwardedFor,
                CfConnectingIp: e.CfConnectingIp))
            .Where(e => since is null || e.Timestamp >= since)
            .Reverse()
            .ToList();

        return Results.Ok(entries);
    }
    catch (Exception ex)
    {
        return Results.Problem($"Failed to read access logs: {ex.Message}");
    }
});

// Phase 11 perf monitoring: server-side history read endpoint.
// Returns time-series samples written by MetricsHistoryService background
// poller (60s cadence, 7-day retention). Frontend uses this to render
// windows beyond the 5-minute client-side ring buffer.
//
// Range is parsed as ISO-8601 minutes-back (default 60). Returns up to
// `limit` samples newest-first, each with cumulative request_count + the
// pre-computed delta-from-previous so charts can render rate without a
// second pass on the client.
app.MapGet("/api/sites/{domain}/metrics/history", (string domain, int? minutes, int? limit, Database db) =>
{
    var clampedMinutes = Math.Clamp(minutes ?? 60, 1, 60 * 24 * 7); // 1 min .. 7 days
    var clampedLimit = Math.Clamp(limit ?? 200, 1, 2000);
    try
    {
        SiteManager.ValidateDomain(domain);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }

    try
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-clampedMinutes).ToString("o");
        using var conn = db.CreateConnection();
        // Pull samples ascending so the rate calculation can walk a sliding
        // pair window. Then reverse before returning if needed.
        var rows = conn.Query<(string sampled_at, long request_count, long size_bytes, string? last_write_utc)>(
            "SELECT sampled_at, request_count, size_bytes, last_write_utc " +
            "FROM metrics_history " +
            "WHERE domain = @Domain AND sampled_at >= @Cutoff " +
            "ORDER BY sampled_at ASC " +
            "LIMIT @Limit",
            new { Domain = domain, Cutoff = cutoff, Limit = clampedLimit }
        ).Select(r => new MetricsHistoryAggregator.RawRow(
            r.sampled_at, r.request_count, r.size_bytes, r.last_write_utc));

        var samples = MetricsHistoryAggregator.ComputeDeltas(rows)
            .Select(s => (object)new
            {
                sampledAt = s.SampledAt,
                requestCount = s.RequestCount,
                sizeBytes = s.SizeBytes,
                lastWriteUtc = s.LastWriteUtc,
                requestsPerMin = s.RequestsPerMin,
            })
            .ToList();
        return Results.Ok(new { domain, minutes = clampedMinutes, samples });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Failed to load metrics history: {ex.Message}");
    }
});

app.MapPost("/api/sites", async (HttpContext ctx, SiteManager sm, SiteOrchestrator orchestrator, ILoggerFactory lf, IServiceProvider sp) =>
{
    var log = lf.CreateLogger("SiteCreate");

    // Parse the body manually so we can extract the Simple-Mode hint
    // `cloudflareTunnel: true` alongside the standard SiteConfig fields.
    // Using JsonDocument lets us read both in a single pass without needing a
    // wrapper DTO that would change the Advanced Mode contract.
    SiteConfig site;
    bool cloudflareTunnelHint;
    try
    {
        using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
        var root = doc.RootElement;

        // Deserialize the canonical SiteConfig fields — case-insensitive to
        // match ASP.NET's default Minimal API binding behaviour.
        site = root.Deserialize<SiteConfig>(caseInsensitiveJson) ?? new SiteConfig();

        // Extract the Simple-Mode hint — absent or false → unchanged behaviour.
        cloudflareTunnelHint = root.TryGetProperty("cloudflareTunnel", out var cfEl)
            && cfEl.ValueKind == JsonValueKind.True;
    }
    catch (JsonException ex)
    {
        return Results.BadRequest(new { error = $"Invalid JSON body: {ex.Message}" });
    }

    try { SiteManager.ValidateDomain(site.Domain); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    if (sm.Get(site.Domain) is not null)
        return Results.Conflict(new { error = $"Site {site.Domain} already exists" });

    // ── Simple Mode: cloudflareTunnel: true ─────────────────────────────
    // Only auto-populate when the caller did NOT already supply a full
    // cloudflare object (Advanced Mode wins over the Simple Mode hint).
    var warnings = new List<string>();
    if (cloudflareTunnelHint && site.Cloudflare is null)
    {
        log.LogInformation(
            "Simple Mode cloudflareTunnel hint for {Domain} — resolving Cloudflare plugin config",
            site.Domain);

        // Resolve the live CloudflareConfig from the plugin's DI container via
        // the same reflection helper used by all other Cloudflare endpoints.
        var cfCfg = ResolveCloudflareServiceOrNull(sp, "CloudflareConfig");
        SimpleModeCloudflareHelper.CloudflarePluginContext? pluginCtx = null;

        if (cfCfg is not null)
        {
            var cfType = cfCfg.GetType();
            var defaultZoneId = cfType.GetProperty("DefaultZoneId")?.GetValue(cfCfg) as string;
            var tunnelId      = cfType.GetProperty("TunnelId")?.GetValue(cfCfg) as string;

            // RenderSubdomain(domain) → stable template-derived subdomain
            string? renderedSubdomain = null;
            var renderMethod = cfType.GetMethod("RenderSubdomain");
            if (renderMethod is not null)
            {
                try
                {
                    renderedSubdomain = renderMethod.Invoke(cfCfg, new object[] { site.Domain }) as string;
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "RenderSubdomain failed for {Domain}", site.Domain);
                }
            }

            pluginCtx = new SimpleModeCloudflareHelper.CloudflarePluginContext
            {
                DefaultZoneId    = defaultZoneId,
                TunnelId         = tunnelId,
                RenderedSubdomain = renderedSubdomain,
            };
        }

        var buildResult = SimpleModeCloudflareHelper.TryBuild(site.Domain, pluginCtx);
        if (buildResult.Config is not null)
        {
            site.Cloudflare = buildResult.Config;
            log.LogInformation(
                "Auto-populated Cloudflare config for {Domain}: subdomain={Subdomain}, zoneId={ZoneId}",
                site.Domain, site.Cloudflare.Subdomain, site.Cloudflare.ZoneId);
        }
        else
        {
            log.LogWarning(
                "Cannot auto-provision Cloudflare tunnel for {Domain}: {Warning}. Site will be created without tunnel.",
                site.Domain, buildResult.Warning);
            warnings.Add(buildResult.Warning!);
        }
    }

    try
    {
        var created = await sm.CreateAsync(site);
        await orchestrator.ApplyAsync(created);

        // Framework auto-detect hint — soft suggestion only, never auto-runs
        var hints = new List<string>();
        try
        {
            var detectedFramework = sm.DetectFramework(created.DocumentRoot);
            var composerJsonExists = File.Exists(Path.Combine(created.DocumentRoot, "composer.json"));
            var composerLockExists = File.Exists(Path.Combine(created.DocumentRoot, "composer.lock"));
            if (detectedFramework is not null && composerJsonExists && !composerLockExists)
                hints.Add($"Framework '{detectedFramework}' detected. Run composer install to fetch dependencies.");
        }
        catch { /* non-fatal — docroot may not exist yet */ }

        if (warnings.Count > 0 || hints.Count > 0)
        {
            return Results.Created($"/api/sites/{created.Domain}", new
            {
                site = created,
                warnings,
                hints,
            });
        }
        return Results.Created($"/api/sites/{created.Domain}", created);
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (UnauthorizedAccessException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/sites/{domain}/duplicate", async (
    string domain,
    HttpContext ctx,
    SiteManager sm,
    SiteOrchestrator orchestrator,
    ILoggerFactory lf,
    CancellationToken ct) =>
{
    var source = sm.Get(domain);
    if (source is null)
        return Results.NotFound(new { error = $"Source site '{domain}' not found" });

    Dictionary<string, string>? body;
    try
    {
        body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>(cancellationToken: ct);
    }
    catch (JsonException ex)
    {
        return Results.BadRequest(new { error = $"Invalid JSON body: {ex.Message}" });
    }

    var newDomain = body?.GetValueOrDefault("newDomain") ?? "";
    var copyFiles = body?.GetValueOrDefault("copyFiles") ?? "all";

    try { SiteManager.ValidateDomain(newDomain); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }

    if (sm.Get(newDomain) is not null)
        return Results.Conflict(new { error = $"Site '{newDomain}' already exists" });

    if (copyFiles is not ("all" or "top" or "empty"))
        return Results.BadRequest(new { error = "copyFiles must be 'all', 'top', or 'empty'" });

    var logger = lf.CreateLogger("DuplicateSite");
    var warnings = new List<string>();

    var sourceRoot = source.DocumentRoot;
    var parentDir = Path.GetDirectoryName(sourceRoot.TrimEnd(Path.DirectorySeparatorChar));
    if (parentDir is null)
        return Results.Problem(title: "Cannot resolve parent of source doc-root", statusCode: 500);

    var newRoot = Path.Combine(parentDir, newDomain);

    if (Directory.Exists(newRoot))
        return Results.Conflict(new { error = $"Target doc-root already exists: {newRoot}" });

    // F65: Windows MAX_PATH guard — delegate to the pure helper in
    // Sites/SiteDuplicatePreflight.cs so the logic stays unit-testable.
    if (NKS.WebDevConsole.Daemon.Sites.SiteDuplicatePreflight.ShouldPreflight(copyFiles))
    {
        var offender = NKS.WebDevConsole.Daemon.Sites.SiteDuplicatePreflight.FindPathTooLong(sourceRoot, newRoot);
        if (offender is not null)
        {
            return Results.Problem(
                title: "Destination path too long",
                detail: $"Duplicating would create paths >{NKS.WebDevConsole.Daemon.Sites.SiteDuplicatePreflight.MaxWindowsPath} chars, exceeding the Windows MAX_PATH limit. Use a shorter target domain or enable long-path support in Windows.",
                statusCode: 409);
        }
    }

    // Step 1: create directory + copy files
    try
    {
        Directory.CreateDirectory(newRoot);
        if (copyFiles == "all")
        {
            DuplicateSiteCopyRecursive(sourceRoot, newRoot);
        }
        else if (copyFiles == "top")
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(sourceRoot))
            {
                var name = Path.GetFileName(entry);
                if (name.StartsWith('.')) continue;
                if (name is "node_modules" or "vendor") continue;
                if (Directory.Exists(entry))
                    DuplicateSiteCopyRecursive(entry, Path.Combine(newRoot, name));
                else
                    File.Copy(entry, Path.Combine(newRoot, name));
            }
        }
        // "empty" — directory already created, nothing more to do
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "copyFiles failed for duplicate of {Source} to {New}", domain, newDomain);
        try { Directory.Delete(newRoot, recursive: true); } catch { /* best-effort rollback */ }
        return Results.Problem(title: "File copy failed", detail: ex.Message, statusCode: 500);
    }

    // Step 2: register new site config + apply vhost + reload Apache
    try
    {
        var newSite = new SiteConfig
        {
            Domain        = newDomain,
            DocumentRoot  = newRoot,
            PhpVersion    = source.PhpVersion,
            SslEnabled    = source.SslEnabled,
            HttpPort      = source.HttpPort,
            HttpsPort     = source.HttpsPort,
            Framework     = source.Framework,
            NodeUpstreamPort  = source.NodeUpstreamPort,
            NodeStartCommand  = source.NodeStartCommand,
            Aliases       = [],
            Environment   = new Dictionary<string, string>(source.Environment),
            PhpSettings   = source.PhpSettings,
            ApacheSettings = source.ApacheSettings,
            // Cloudflare: keep enabled flag but clear subdomain so it doesn't collide
            Cloudflare = source.Cloudflare is null ? null : new SiteCloudflareConfig
            {
                Enabled      = source.Cloudflare.Enabled,
                Subdomain    = "",
                ZoneId       = source.Cloudflare.ZoneId,
                ZoneName     = source.Cloudflare.ZoneName,
                LocalService = source.Cloudflare.LocalService,
                Protocol     = source.Cloudflare.Protocol,
            },
        };

        var created = await sm.CreateAsync(newSite);
        await orchestrator.ApplyAsync(created);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Site registration failed for duplicate {New}", newDomain);
        // Rollback: remove TOML + generated vhost (Delete handles both) then doc-root
        try { sm.Delete(newDomain); } catch { /* ignore — may not have been added yet */ }
        try { Directory.Delete(newRoot, recursive: true); } catch { /* best-effort */ }
        return Results.Problem(title: "Site registration failed", detail: ex.Message, statusCode: 500);
    }

    logger.LogInformation("Duplicated site {Source} → {New} (copyFiles={Mode})", domain, newDomain, copyFiles);
    return Results.Created($"/api/sites/{newDomain}", new
    {
        domain       = newDomain,
        documentRoot = newRoot,
        sourceDomain = domain,
        copyFiles,
        warnings,
    });
});

static void DuplicateSiteCopyRecursive(string source, string target)
{
    Directory.CreateDirectory(target);
    foreach (var file in Directory.GetFiles(source))
        File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
    foreach (var dir in Directory.GetDirectories(source))
        DuplicateSiteCopyRecursive(dir, Path.Combine(target, Path.GetFileName(dir)));
}

app.MapPut("/api/sites/{domain}", async (string domain, SiteConfig site, SiteManager sm, SiteOrchestrator orchestrator) =>
{
    if (sm.Get(domain) is null)
        return Results.NotFound();
    site.Domain = domain;
    try
    {
        var updated = await sm.UpdateAsync(site);
        await orchestrator.ApplyAsync(updated);
        return Results.Ok(updated);
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (UnauthorizedAccessException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// PATCH /api/sites/{domain}/enabled — body: { "enabled": true|false }
// Soft-enables or soft-disables a site without deleting it. The TOML stays on
// disk so the site remains visible in the UI. When disabled, all active vhosts
// are removed and the web server is reloaded so the site stops being served.
app.MapMethods("/api/sites/{domain}/enabled", ["PATCH"], async (string domain, HttpContext ctx, SiteManager sm, SiteOrchestrator orchestrator) =>
{
    var site = sm.Get(domain);
    if (site is null)
        return Results.NotFound();

    bool? enabled = null;
    try
    {
        var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, JsonElement>>();
        if (body is not null && body.TryGetValue("enabled", out var val)
            && (val.ValueKind == JsonValueKind.True || val.ValueKind == JsonValueKind.False))
            enabled = val.GetBoolean();
    }
    catch { /* fall through to 400 */ }

    if (enabled is null)
        return Results.BadRequest(new { error = "Body must be {\"enabled\": true|false}" });

    site.Enabled = enabled.Value;
    try
    {
        var updated = await sm.UpdateAsync(site);
        await orchestrator.ApplyAsync(updated);
        return Results.Ok(updated);
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (UnauthorizedAccessException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapDelete("/api/sites/{domain}", async (string domain, SiteManager sm, SiteOrchestrator orchestrator) =>
{
    try
    {
        if (!sm.Delete(domain)) return Results.NotFound();
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (UnauthorizedAccessException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    await orchestrator.RemoveAsync(domain);
    return Results.NoContent();
});

// ── Composer endpoints ────────────────────────────────────────────────────────
// ComposerPlugin registers ComposerInvoker via IWdcPlugin.Initialize into the
// shared DI container. Because the plugin DLL is loaded in an isolated
// AssemblyLoadContext we cannot reference its types at compile time. We look up
// the type name from the loaded plugin assembly and resolve via sp.GetService(type)
// to avoid a hard project reference while still using the registered singleton.

static object? ResolveComposerInvoker(IServiceProvider sp, PluginLoader loader)
{
    var composerPlugin = loader.Plugins.FirstOrDefault(p => p.Instance.Id == "nks.wdc.composer");
    if (composerPlugin is null) return null;
    var invokerType = composerPlugin.Assembly.GetType("NKS.WebDevConsole.Plugin.Composer.ComposerInvoker");
    return invokerType is null ? null : sp.GetService(invokerType);
}

// F49c: port probe cache — set once per daemon boot by ResolveMysqlPortWithFallback.
int? _cachedMysqlPort = null;
object _mysqlPortProbeLock = new();

// F49c: Resolve MySQL port with explicit-setting → live-prober → plugin-default
// fallback chain. Plugin Port default (3306) collides with MAMP on user's machine,
// so step 2 actively probes a small port sweep with the WDC password: the real
// WDC mysqld is whichever port authenticates. Cached once per daemon boot.
int ResolveMysqlPortWithFallback(SettingsStore settings, IServiceProvider sp, PluginLoader loader, BinaryManager bm)
{
    // Step 1: explicit user config wins always, no probing.
    if (settings.TryReadMysqlPort(out var configured) && configured > 0)
        return configured;

    // Step 2: probe cache — avoid repeating the probe on every endpoint hit.
    if (_cachedMysqlPort is int cached) return cached;

    lock (_mysqlPortProbeLock)
    {
        if (_cachedMysqlPort is int doubleChecked) return doubleChecked;

        // Step 3: try port list with WDC root password via mysqladmin ping.
        var password = NKS.WebDevConsole.Core.Services.MySqlRootPassword.TryRead();
        var mysql = bm.ListInstalled("mysql").FirstOrDefault();
        var mysqladmin = mysql?.Executable is null ? null : Path.Combine(Path.GetDirectoryName(mysql.Executable)!, OperatingSystem.IsWindows() ? "mysqladmin.exe" : "mysqladmin");
        if (!string.IsNullOrEmpty(password) && mysqladmin is not null && File.Exists(mysqladmin))
        {
            var candidatePorts = new[] { 3306, 3307, 3308, 3309 };
            foreach (var port in candidatePorts)
            {
                try
                {
                    var args = new[] { "-h", "127.0.0.1", "-P", port.ToString(), "-u", "root", "ping" };
                    var env = new Dictionary<string, string?> { ["MYSQL_PWD"] = password };
                    var result = CliWrap.Cli.Wrap(mysqladmin)
                        .WithArguments(args)
                        .WithEnvironmentVariables(env)
                        .WithValidation(CliWrap.CommandResultValidation.None)
                        .ExecuteBufferedAsync()
                        .ConfigureAwait(false)
                        .GetAwaiter().GetResult();
                    if (result.ExitCode == 0 && result.StandardOutput.Contains("mysqld is alive", StringComparison.OrdinalIgnoreCase))
                    {
                        _cachedMysqlPort = port;
                        return port;
                    }
                }
                catch { /* skip port, try next */ }
            }
        }

        // Step 4: fall back to plugin default (3306) — prober exhausted.
        try
        {
            var mysqlPlugin = loader.Plugins.FirstOrDefault(p => p.Instance.Id == "nks.wdc.mysql");
            if (mysqlPlugin is not null)
            {
                var moduleType = mysqlPlugin.Assembly.GetType("NKS.WebDevConsole.Plugin.MySQL.MySqlModule");
                if (moduleType is not null)
                {
                    var module = sp.GetService(moduleType);
                    if (module is not null)
                    {
                        var portVal = moduleType.GetProperty("Port")?.GetValue(module);
                        if (portVal is int p && p > 0) { _cachedMysqlPort = p; return p; }
                    }
                }
            }
        }
        catch { }

        _cachedMysqlPort = 3306;
        return 3306;
    }
}

/// <summary>
/// F77: resolve the per-site PHP binary path so composer.phar runs under
/// the interpreter version the site has actually declared. Returns null
/// when the site has no phpVersion pinned — the invoker then falls back
/// to ComposerConfig.PhpPath (the plugin's global scan-first winner).
///
/// Layout: ~/.wdc/binaries/php/&lt;version&gt;/php.exe (matches F33
/// discovery path in ComposerConfig.ApplyOwnBinaryDefaults).
/// </summary>
static string? ResolveSitePhpBinary(NKS.WebDevConsole.Core.Models.SiteConfig site)
{
    if (string.IsNullOrWhiteSpace(site.PhpVersion)) return null;
    var binariesRoot = NKS.WebDevConsole.Core.Services.WdcPaths.BinariesRoot;
    var candidate = Path.Combine(binariesRoot, "php", site.PhpVersion,
        OperatingSystem.IsWindows() ? "php.exe" : "php");
    return File.Exists(candidate) ? candidate : null;
}

static string ResolveComposerRoot(string docroot)
{
    if (File.Exists(Path.Combine(docroot, "composer.json"))) return docroot;
    var parentDir = Path.GetDirectoryName(docroot.TrimEnd('/', '\\'));
    if (!string.IsNullOrEmpty(parentDir) && File.Exists(Path.Combine(parentDir, "composer.json")))
        return parentDir;
    return docroot;
}

static async Task<(bool ExitCode, int Code, string Stdout, string Stderr)>
    InvokeComposerAsync(object invoker, string method, object?[] args)
{
    var m = invoker.GetType().GetMethod(method)
        ?? throw new InvalidOperationException($"Method {method} not found on ComposerInvoker");
    var task = (Task)m.Invoke(invoker, args)!;
    await task;
    var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
    var exitCode = (int)result.GetType().GetProperty("ExitCode")!.GetValue(result)!;
    var stdout   = (string)result.GetType().GetProperty("Stdout")!.GetValue(result)!;
    var stderr   = (string)result.GetType().GetProperty("Stderr")!.GetValue(result)!;
    return (exitCode == 0, exitCode, stdout, stderr);
}

app.MapGet("/api/sites/{domain}/composer/status", async (string domain, SiteManager sm, ILoggerFactory lf) =>
{
    var site = sm.Get(domain);
    if (site is null) return Results.NotFound(new { error = $"Site '{domain}' not found" });

    var root = site.DocumentRoot;
    if (!Directory.Exists(root))
        return Results.NotFound(new { error = $"Document root for '{domain}' does not exist" });
    var hasJson = File.Exists(Path.Combine(root, "composer.json"));
    var composerRoot = root;
    if (!hasJson)
    {
        var parentDir = Path.GetDirectoryName(root.TrimEnd('/', '\\'));
        if (!string.IsNullOrEmpty(parentDir))
        {
            var parentJson = Path.Combine(parentDir, "composer.json");
            if (File.Exists(parentJson))
            {
                hasJson = true;
                composerRoot = parentDir;
            }
        }
    }
    var hasLock = File.Exists(Path.Combine(composerRoot, "composer.lock"));

    var packages = new List<string>();
    string? phpVersion = null;
    if (hasJson)
    {
        try
        {
            var composerJson = await File.ReadAllTextAsync(Path.Combine(composerRoot, "composer.json"));
            using var doc = System.Text.Json.JsonDocument.Parse(composerJson);
            if (doc.RootElement.TryGetProperty("require", out var require))
            {
                foreach (var pkg in require.EnumerateObject())
                    if (!pkg.Name.Equals("php", StringComparison.OrdinalIgnoreCase))
                        packages.Add($"{pkg.Name}:{pkg.Value.GetString() ?? "*"}");
                if (require.TryGetProperty("php", out var phpConstraint))
                    phpVersion = phpConstraint.GetString();
            }
        }
        catch (Exception ex)
        {
            lf.CreateLogger("Composer").LogWarning(ex, "Could not parse composer.json for {Domain}", domain);
        }
    }

    // Detect PHP framework — returns lowercase identifier or null for unknown
    string? framework = null;
    try { framework = sm.DetectFramework(root); }
    catch (Exception ex)
    {
        lf.CreateLogger("Composer").LogWarning(ex, "DetectFramework failed for {Domain}", domain);
    }

    // installSuggestion: present only when a PHP framework is detected,
    // composer.json exists, and composer.lock is missing (deps never installed)
    object? installSuggestion = null;
    if (framework is not null && hasJson && !hasLock)
    {
        installSuggestion = new
        {
            reason = "framework_detected",
            framework,
            action = "composer_install",
        };
    }

    return Results.Ok(new { hasComposerJson = hasJson, hasLock, packages, phpVersion, framework = framework ?? "none", installSuggestion, composerRoot });
});

app.MapPost("/api/sites/{domain}/composer/install", async (string domain, SiteManager sm, IServiceProvider sp, ILoggerFactory lf, CancellationToken ct) =>
{
    var site = sm.Get(domain);
    if (site is null) return Results.NotFound(new { error = $"Site '{domain}' not found" });

    var invoker = ResolveComposerInvoker(sp, pluginLoader);
    if (invoker is null)
        return Results.Problem(title: "Composer plugin not loaded",
            detail: "Install the Composer plugin and restart the daemon.", statusCode: 503);

    var logger = lf.CreateLogger("Composer");
    var installRoot = ResolveComposerRoot(site.DocumentRoot);
    logger.LogInformation("composer install for {Domain} in {Root}", domain, installRoot);
    try
    {
        var phpOverride = ResolveSitePhpBinary(site);
        var (_, exitCode, stdout, stderr) = await InvokeComposerAsync(invoker, "InstallAsync",
            [installRoot, phpOverride, ct]);
        logger.LogInformation("composer install exit={Code} for {Domain}", exitCode, domain);
        return Results.Ok(new { exitCode, stdout, stderr });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "composer install failed for {Domain}", domain);
        return Results.Problem(title: "composer install failed", detail: ex.InnerException?.Message ?? ex.Message, statusCode: 500);
    }
});

app.MapPost("/api/sites/{domain}/composer/require", async (string domain, HttpContext ctx, SiteManager sm, IServiceProvider sp, ILoggerFactory lf, CancellationToken ct) =>
{
    var site = sm.Get(domain);
    if (site is null) return Results.NotFound(new { error = $"Site '{domain}' not found" });

    var invoker = ResolveComposerInvoker(sp, pluginLoader);
    if (invoker is null)
        return Results.Problem(title: "Composer plugin not loaded",
            detail: "Install the Composer plugin and restart the daemon.", statusCode: 503);

    Dictionary<string, object?>? body;
    try { body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, object?>>(); }
    catch (System.Text.Json.JsonException ex)
    { return Results.BadRequest(new { error = $"Invalid JSON: {ex.Message}" }); }

    if (body is null || !body.TryGetValue("package", out var pkgObj) || pkgObj?.ToString() is not { Length: > 0 } package)
        return Results.BadRequest(new { error = "Body must contain { \"package\": \"vendor/name\" }" });

    if (!System.Text.RegularExpressions.Regex.IsMatch(package, @"^[A-Za-z0-9/_.:\-\^~*@]+$"))
        return Results.BadRequest(new { error = "Invalid package name" });

    // Guard against path traversal sequences even when individual chars are valid
    if (package.Contains(".."))
        return Results.BadRequest(new { error = "Invalid package name" });

    var logger = lf.CreateLogger("Composer");
    logger.LogInformation("composer require {Package} for {Domain}", package, domain);
    try
    {
        var phpOverride = ResolveSitePhpBinary(site);
        var (_, exitCode, stdout, stderr) = await InvokeComposerAsync(invoker, "RequireAsync",
            [site.DocumentRoot, package, phpOverride, ct]);
        logger.LogInformation("composer require {Package} exit={Code} for {Domain}", package, exitCode, domain);
        return Results.Ok(new { exitCode, stdout, stderr });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "composer require {Package} failed for {Domain}", package, domain);
        return Results.Problem(title: "composer require failed", detail: ex.InnerException?.Message ?? ex.Message, statusCode: 500);
    }
});

app.MapPost("/api/sites/{domain}/composer/remove", async (string domain, HttpContext ctx, SiteManager sm, IServiceProvider sp, ILoggerFactory lf, CancellationToken ct) =>
{
    var site = sm.Get(domain);
    if (site is null) return Results.NotFound(new { error = $"Site '{domain}' not found" });

    var invoker = ResolveComposerInvoker(sp, pluginLoader);
    if (invoker is null)
        return Results.Problem(title: "Composer plugin not loaded",
            detail: "Install the Composer plugin and restart the daemon.", statusCode: 503);

    Dictionary<string, object?>? body;
    try { body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, object?>>(); }
    catch (System.Text.Json.JsonException ex)
    { return Results.BadRequest(new { error = $"Invalid JSON: {ex.Message}" }); }

    if (body is null || !body.TryGetValue("package", out var pkgObj) || pkgObj?.ToString() is not { Length: > 0 } package)
        return Results.BadRequest(new { error = "Body must contain { \"package\": \"vendor/name\" }" });

    if (!System.Text.RegularExpressions.Regex.IsMatch(package, @"^[A-Za-z0-9/_.\-]+$"))
        return Results.BadRequest(new { error = "Invalid package name" });

    if (package.Contains(".."))
        return Results.BadRequest(new { error = "Invalid package name" });

    var composerRoot = ResolveComposerRoot(site.DocumentRoot);
    var logger = lf.CreateLogger("Composer");
    logger.LogInformation("composer remove {Package} for {Domain}", package, domain);
    try
    {
        var phpOverride = ResolveSitePhpBinary(site);
        var (_, exitCode, stdout, stderr) = await InvokeComposerAsync(invoker, "RunAsync",
            [composerRoot, new[] { "remove", package }, phpOverride, ct]);
        logger.LogInformation("composer remove {Package} exit={Code} for {Domain}", package, exitCode, domain);
        return Results.Ok(new { exitCode, stdout, stderr });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "composer remove {Package} failed for {Domain}", package, domain);
        return Results.Problem(title: "composer remove failed", detail: ex.InnerException?.Message ?? ex.Message, statusCode: 500);
    }
});

app.MapPost("/api/sites/{domain}/composer/init", async (string domain, HttpContext ctx, SiteManager sm, IServiceProvider sp, ILoggerFactory lf, CancellationToken ct) =>
{
    var site = sm.Get(domain);
    if (site is null) return Results.NotFound(new { error = $"Site '{domain}' not found" });
    var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>(cancellationToken: ct);
    var invoker = ResolveComposerInvoker(sp, pluginLoader);
    if (invoker is null) return Results.Problem(title: "Composer plugin not loaded", statusCode: 503);
    var composerRoot = ResolveComposerRoot(site.DocumentRoot);
    if (!File.Exists(Path.Combine(composerRoot, "composer.json")))
        composerRoot = site.DocumentRoot;
    var name = body?.GetValueOrDefault("name") ?? $"local/{domain.Replace('.', '-')}";
    var description = body?.GetValueOrDefault("description") ?? "";
    var type = body?.GetValueOrDefault("type") ?? "project";
    var stability = body?.GetValueOrDefault("stability") ?? "stable";
    var args = new List<string> { "init", "--no-interaction", $"--name={name}", $"--type={type}", $"--stability={stability}" };
    if (!string.IsNullOrWhiteSpace(description)) args.Add($"--description={description}");
    try
    {
        var phpOverride = ResolveSitePhpBinary(site);
        var (_, exitCode, stdout, stderr) = await InvokeComposerAsync(invoker, "RunAsync", [composerRoot, args.ToArray(), phpOverride, ct]);
        return Results.Ok(new { exitCode, stdout, stderr, composerRoot });
    }
    catch (Exception ex) { return Results.Problem(title: "composer init failed", detail: ex.Message, statusCode: 500); }
});

app.MapGet("/api/sites/{domain}/composer/diagnose", async (string domain, SiteManager sm, IServiceProvider sp, ILoggerFactory lf, CancellationToken ct) =>
{
    var site = sm.Get(domain);
    if (site is null) return Results.NotFound(new { error = $"Site '{domain}' not found" });
    var invoker = ResolveComposerInvoker(sp, pluginLoader);
    if (invoker is null) return Results.Problem(title: "Composer plugin not loaded", statusCode: 503);
    var composerRoot = ResolveComposerRoot(site.DocumentRoot);
    var logger = lf.CreateLogger("Composer");
    try
    {
        var phpOverride = ResolveSitePhpBinary(site);
        var (_, _, stdout, _) = await InvokeComposerAsync(invoker, "RunAsync",
            [composerRoot, new[] { "diagnose", "--no-ansi" }, phpOverride, ct]);
        var warnings = new List<string>();
        var errors = new List<string>();
        foreach (var line in (stdout ?? "").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("WARNING", StringComparison.OrdinalIgnoreCase)) warnings.Add(trimmed);
            else if (trimmed.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)) errors.Add(trimmed);
        }
        return Results.Ok(new { warnings, errors });
    }
    catch (Exception ex) { return Results.Problem(title: "composer diagnose failed", detail: ex.Message, statusCode: 500); }
});

app.MapGet("/api/sites/{domain}/composer/outdated", async (string domain, SiteManager sm, IServiceProvider sp, ILoggerFactory lf, CancellationToken ct) =>
{
    var site = sm.Get(domain);
    if (site is null) return Results.NotFound(new { error = $"Site '{domain}' not found" });
    var invoker = ResolveComposerInvoker(sp, pluginLoader);
    if (invoker is null) return Results.Problem(title: "Composer plugin not loaded", statusCode: 503);
    var composerRoot = ResolveComposerRoot(site.DocumentRoot);
    var logger = lf.CreateLogger("Composer");
    try
    {
        var phpOverride = ResolveSitePhpBinary(site);
        var (_, exitCode, stdout, _) = await InvokeComposerAsync(invoker, "RunAsync",
            [composerRoot, new[] { "outdated", "--direct", "--format=json", "--no-ansi" }, phpOverride, ct]);
        System.Collections.Generic.List<object> installed = new();
        if (!string.IsNullOrWhiteSpace(stdout))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(stdout);
                if (doc.RootElement.TryGetProperty("installed", out var arr))
                {
                    foreach (var pkg in arr.EnumerateArray())
                    {
                        installed.Add(new {
                            name = pkg.GetProperty("name").GetString(),
                            version = pkg.TryGetProperty("version", out var v) ? v.GetString() : null,
                            latest = pkg.TryGetProperty("latest", out var l) ? l.GetString() : null,
                            latestStatus = pkg.TryGetProperty("latest-status", out var s) ? s.GetString() : null,
                        });
                    }
                }
            }
            catch (Exception ex) { logger.LogWarning(ex, "composer outdated parse failed"); }
        }
        return Results.Ok(new { installed });
    }
    catch (Exception ex) { return Results.Problem(title: "composer outdated failed", detail: ex.Message, statusCode: 500); }
});

app.MapGet("/api/composer/version", async (IServiceProvider sp, PluginLoader pluginLoader, CancellationToken ct) =>
{
    var invoker = ResolveComposerInvoker(sp, pluginLoader);
    if (invoker is null)
        return Results.Ok(new { version = (string?)null, path = (string?)null, managed = false });

    string? version = null;
    string? path = null;
    string? phpPath = null;
    string? lastError = null;
    bool managed = false;

    // Read config paths FIRST so we can include them in diagnostics even if the
    // composer invocation throws (e.g. PHP not on PATH, phar missing).
    try
    {
        var configField = invoker.GetType().GetField("_config",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var config = configField?.GetValue(invoker);
        if (config is not null)
        {
            path = config.GetType().GetProperty("ExecutablePath")?.GetValue(config) as string;
            phpPath = config.GetType().GetProperty("PhpPath")?.GetValue(config) as string;
            if (path is not null)
                managed = path.StartsWith(NKS.WebDevConsole.Core.Services.WdcPaths.BinariesRoot,
                    StringComparison.OrdinalIgnoreCase);
        }
    }
    catch (Exception ex) { lastError = $"reflection: {ex.Message}"; }

    try
    {
        var tempDir = Path.GetTempPath();
        var (ok, exitCode, stdout, stderr) = await InvokeComposerAsync(invoker, "RunAsync",
            [tempDir, new[] { "--version", "--no-ansi" }, (string?)null, ct]);
        // Strip any residual ANSI escape sequences (some composer builds emit them
        // even with --no-ansi when run via CliWrap because ConEmu detection still
        // thinks it's a TTY). Pattern: ESC [ ... m
        var clean = System.Text.RegularExpressions.Regex.Replace(stdout, @"\u001B\[[0-9;]*[mK]", "");
        if (ok)
        {
            var m = System.Text.RegularExpressions.Regex.Match(clean, @"Composer(?:\s+version)?\s+(\S+)");
            if (m.Success) version = m.Groups[1].Value;
            else lastError = $"regex: stdout={clean.Substring(0, Math.Min(clean.Length, 120))}";
        }
        else
        {
            lastError = $"exit={exitCode} stderr={stderr.Substring(0, Math.Min(stderr.Length, 200))}";
        }
    }
    catch (Exception ex) { lastError = $"invoke: {ex.InnerException?.Message ?? ex.Message}"; }

    return Results.Ok(new { version, path, phpPath, managed, lastError });
});

app.MapPost("/api/composer/self-install", async (IHttpClientFactory httpFactory, IServiceProvider sp, PluginLoader pluginLoader, ILoggerFactory lf, CancellationToken ct) =>
{
    var logger = lf.CreateLogger("Composer");
    try
    {
        var composerBinRoot = Path.Combine(NKS.WebDevConsole.Core.Services.WdcPaths.BinariesRoot, "composer");
        var versionDir = Path.Combine(composerBinRoot, "latest");
        Directory.CreateDirectory(versionDir);
        var pharPath = Path.Combine(versionDir, "composer.phar");

        var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(60);
        using var resp = await http.GetAsync("https://getcomposer.org/composer.phar", ct);
        if (!resp.IsSuccessStatusCode)
            return Results.Problem(title: "Download failed", detail: $"HTTP {(int)resp.StatusCode} from getcomposer.org", statusCode: 502);
        await using (var fs = File.Create(pharPath))
            await resp.Content.CopyToAsync(fs, ct);

        // F27: defense-in-depth SHA-256 verification. getcomposer.org publishes
        // composer.phar.sha256sum alongside the phar; cross-check so a MITM or
        // compromised mirror can't substitute a trojaned installer even if TLS
        // is downgraded by a hostile cert. Missing sidecar is logged but NOT
        // fatal — upstream occasionally lags.
        try
        {
            using var sumResp = await http.GetAsync("https://getcomposer.org/composer.phar.sha256sum", ct);
            if (sumResp.IsSuccessStatusCode)
            {
                var raw = (await sumResp.Content.ReadAsStringAsync(ct)).Trim();
                // Format: "<hex>  composer.phar" — take the first whitespace-separated token.
                var expected = raw.Split([' ', '\t', '\n'], 2, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault() ?? "";
                if (expected.Length == 64)
                {
                    await using var vs = File.OpenRead(pharPath);
                    var hash = await System.Security.Cryptography.SHA256.HashDataAsync(vs, ct);
                    var actual = Convert.ToHexString(hash).ToLowerInvariant();
                    if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(pharPath); } catch { /* ignore */ }
                        return Results.Problem(
                            title: "Integrity check failed",
                            detail: $"composer.phar SHA-256 mismatch: upstream {expected}, downloaded {actual}. Aborted and removed the bad artifact.",
                            statusCode: 502);
                    }
                    logger.LogInformation("composer.phar sha-256 verified against upstream sum");
                }
            }
            else
            {
                logger.LogWarning("composer.phar.sha256sum returned {Status} — TLS is the only remaining integrity guarantee", (int)sumResp.StatusCode);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "composer.phar sha-256 verification skipped");
        }

        // Re-scan binaries so ComposerInvoker picks up the new phar without a daemon restart.
        var composerPlugin = pluginLoader.Plugins.FirstOrDefault(p => p.Instance.Id == "nks.wdc.composer");
        if (composerPlugin is not null)
        {
            var invokerType = composerPlugin.Assembly.GetType("NKS.WebDevConsole.Plugin.Composer.ComposerInvoker");
            var configType  = composerPlugin.Assembly.GetType("NKS.WebDevConsole.Plugin.Composer.ComposerConfig");
            if (invokerType is not null && configType is not null)
            {
                var invoker = sp.GetService(invokerType);
                if (invoker is not null)
                {
                    var configField = invokerType.GetField("_config", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    var config = configField?.GetValue(invoker);
                    var applyMethod = configType.GetMethod("ApplyOwnBinaryDefaults");
                    applyMethod?.Invoke(config, null);
                }
            }
        }
        logger.LogInformation("composer self-install ok -> {Path}", pharPath);
        return Results.Ok(new { path = pharPath, version = "latest" });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "composer self-install failed");
        return Results.Problem(title: "self-install failed", detail: ex.Message, statusCode: 500);
    }
});

// List config history versions for a site
app.MapGet("/api/sites/{domain}/history", (string domain, SiteManager sm) =>
{
    var site = sm.Get(domain);
    if (site is null) return Results.NotFound();

    var generatedDir = Path.Combine(NKS.WebDevConsole.Core.Services.WdcPaths.GeneratedRoot, "history");
    if (!Directory.Exists(generatedDir))
        return Results.Ok(Array.Empty<object>());

    var prefix = $"{domain}.conf.";
    var versions = Directory.GetFiles(generatedDir, prefix + "*")
        .Select(f => new
        {
            timestamp = Path.GetFileName(f).Substring(prefix.Length),
            size = new FileInfo(f).Length,
            createdAt = File.GetLastWriteTimeUtc(f),
            path = f,
        })
        .OrderByDescending(v => v.timestamp)
        .ToList();
    return Results.Ok(versions);
});

// Rollback a site's vhost config to a specific historical version
app.MapPost("/api/sites/{domain}/rollback/{timestamp}", async (string domain, string timestamp, SiteManager sm, SiteOrchestrator orchestrator) =>
{
    // SECURITY: validate both path segments before building filesystem paths. The
    // timestamp is user-supplied and could contain "../" or null bytes — without
    // validation an attacker (with a valid auth token) could overwrite any file
    // the daemon user can write. domain is already validated by sm.Get() (the
    // dictionary lookup will fail for traversal strings) but we re-check anyway
    // so the path-containment assertion below is definitely safe.
    try { SiteManager.ValidateDomain(domain); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }

    // Timestamp format produced by config history rotation is ISO-like with digits,
    // letters and safe punctuation (e.g. "20260411T140522Z"). Reject anything else.
    if (string.IsNullOrWhiteSpace(timestamp) ||
        !System.Text.RegularExpressions.Regex.IsMatch(timestamp, @"^[a-zA-Z0-9_\-.:]{1,64}$"))
    {
        return Results.BadRequest(new { error = "Invalid timestamp format" });
    }

    var site = sm.Get(domain);
    if (site is null) return Results.NotFound(new { error = $"Site {domain} not found" });

    var historyDir = Path.Combine(NKS.WebDevConsole.Core.Services.WdcPaths.GeneratedRoot, "history");
    var historyDirFull = Path.GetFullPath(historyDir);
    var historyFile = Path.GetFullPath(Path.Combine(historyDir, $"{domain}.conf.{timestamp}"));
    if (!historyFile.StartsWith(historyDirFull, StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Resolved history path escapes history root" });
    if (!File.Exists(historyFile))
        return Results.NotFound(new { error = $"History entry {timestamp} not found" });

    var generatedRoot = Path.GetFullPath(NKS.WebDevConsole.Core.Services.WdcPaths.GeneratedRoot);
    var generatedFile = Path.GetFullPath(Path.Combine(generatedRoot, $"{domain}.conf"));
    if (!generatedFile.StartsWith(generatedRoot, StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Resolved generated path escapes generated root" });

    File.Copy(historyFile, generatedFile, overwrite: true);
    // Re-apply through orchestrator so vhost is also written next to Apache binary
    await orchestrator.ApplyAsync(site);
    return Results.Ok(new { domain, restoredFrom = timestamp });
});

// Telemetry consent — Phase 7 plan item #111.
// NKS WDC sends NO telemetry by default. This endpoint lets the Settings page
// read the current consent state and update it. No actual Sentry/metrics
// transport is wired yet — this iteration establishes the consent gate and
// persistence so the wire-up can be a small future commit.
app.MapGet("/api/telemetry/consent", (TelemetryConsent consent) =>
{
    consent.Load(); // pick up manual edits to the file
    return Results.Ok(new
    {
        enabled = consent.Enabled,
        crashReports = consent.CrashReports,
        usageMetrics = consent.UsageMetrics,
        consentGivenUtc = consent.ConsentGivenUtc,
    });
});

app.MapPost("/api/telemetry/consent", async (HttpContext ctx, TelemetryConsent consent) =>
{
    Dictionary<string, bool>? body;
    try
    {
        body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, bool>>();
    }
    catch (System.Text.Json.JsonException ex)
    {
        return Results.BadRequest(new { error = $"Invalid JSON body: {ex.Message}" });
    }
    var enabled = body?.GetValueOrDefault("enabled") ?? false;
    var crashReports = body?.GetValueOrDefault("crashReports") ?? false;
    var usageMetrics = body?.GetValueOrDefault("usageMetrics") ?? false;
    try
    {
        consent.Save(enabled, crashReports, usageMetrics);
    }
    catch (Exception ex)
    {
        return Results.Problem($"Failed to persist telemetry consent: {ex.Message}");
    }
    return Results.Ok(new
    {
        enabled = consent.Enabled,
        crashReports = consent.CrashReports,
        usageMetrics = consent.UsageMetrics,
        consentGivenUtc = consent.ConsentGivenUtc,
    });
});

app.MapDelete("/api/telemetry/consent", (TelemetryConsent consent) =>
{
    try
    {
        consent.Revoke();
        return Results.NoContent();
    }
    catch (Exception ex)
    {
        return Results.Problem($"Failed to revoke telemetry consent: {ex.Message}");
    }
});

// Onboarding state — Phase 7 plan item.
// First-run detection: a simple ~/.wdc/data/onboarding-complete.flag file marks the
// wizard as finished. Before that file exists the frontend shows the onboarding
// wizard. The wizard also returns which prerequisites are already satisfied so it
// can skip ahead past any steps the user has already completed manually.
app.MapGet("/api/onboarding/state", (BinaryManager bm) =>
{
    var flagFile = Path.Combine(NKS.WebDevConsole.Core.Services.WdcPaths.DataRoot, "onboarding-complete.flag");

    var installed = bm.ListInstalled();
    var hasApache = installed.Any(b => b.App.Equals("apache", StringComparison.OrdinalIgnoreCase));
    var hasPhp = installed.Any(b => b.App.Equals("php", StringComparison.OrdinalIgnoreCase));
    var hasMysql = installed.Any(b => b.App.Equals("mysql", StringComparison.OrdinalIgnoreCase));
    var hasMkcert = installed.Any(b => b.App.Equals("mkcert", StringComparison.OrdinalIgnoreCase));

    // mkcert CA installed state — check presence of rootCA.pem in the mkcert CAROOT
    var mkcertRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "mkcert");
    var mkcertCaInstalled = File.Exists(Path.Combine(mkcertRoot, "rootCA.pem"));

    return Results.Ok(new
    {
        completed = File.Exists(flagFile),
        prerequisites = new
        {
            apacheInstalled = hasApache,
            phpInstalled = hasPhp,
            mysqlInstalled = hasMysql,
            mkcertBinaryInstalled = hasMkcert,
            mkcertCaInstalled,
        }
    });
});

app.MapPost("/api/onboarding/complete", async (SiteManager siteManager, SiteOrchestrator siteOrch) =>
{
    var flagFile = Path.Combine(NKS.WebDevConsole.Core.Services.WdcPaths.DataRoot, "onboarding-complete.flag");
    Directory.CreateDirectory(Path.GetDirectoryName(flagFile)!);
    File.WriteAllText(flagFile, DateTime.UtcNow.ToString("O"));

    // Seed a `localhost` site with an About page so the user gets a
    // working http://localhost/ out of the box — dashboard link that
    // doesn't 404. Skipped if they already have one (don't clobber).
    try
    {
        var docRoot = Path.Combine(NKS.WebDevConsole.Core.Services.WdcPaths.DataRoot, "default-site");
        Directory.CreateDirectory(docRoot);
        var indexPath = Path.Combine(docRoot, "index.php");
        if (!File.Exists(indexPath))
        {
            await File.WriteAllTextAsync(indexPath,
                "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">" +
                "<title>NKS WebDev Console — about</title>" +
                "<style>body{font-family:-apple-system,Segoe UI,Inter,sans-serif;max-width:780px;margin:4rem auto;padding:0 1.5rem;color:#1f2937;line-height:1.55}" +
                "h1{font-weight:600;letter-spacing:-0.02em}code,pre{background:#f3f4f6;padding:2px 6px;border-radius:4px;font-size:.9em}" +
                "pre{padding:12px 16px;overflow-x:auto}.pill{display:inline-block;padding:2px 10px;border-radius:999px;background:#dbeafe;color:#1e40af;font-size:.8em;margin-right:6px}</style>" +
                "</head><body><h1>NKS WebDev Console</h1>" +
                "<p><span class=\"pill\">localhost</span><span class=\"pill\">Apache + PHP-FPM</span>It works!</p>" +
                "<p>This is the default site seeded by NKS WDC at first-run. You're hitting it through Apache, which proxied the request to PHP-FPM and executed <code>index.php</code>.</p>" +
                "<h3>Runtime</h3><pre><?php\n" +
                "  echo 'PHP:         ' . PHP_VERSION . ' (' . PHP_OS . \", \" . php_sapi_name() . \"\\n\";\n" +
                "  echo 'Server:      ' . ($_SERVER['SERVER_SOFTWARE'] ?? 'n/a') . \"\\n\";\n" +
                "  echo 'Host:        ' . ($_SERVER['HTTP_HOST'] ?? 'n/a') . \"\\n\";\n" +
                "  echo 'INI:         ' . (php_ini_loaded_file() ?: '(none)') . \"\\n\";\n" +
                "?></pre>" +
                "<h3>Next steps</h3><ul>" +
                "<li>Create your own site under <strong>Sites → New</strong>.</li>" +
                "<li>Add <code>127.0.0.1 myapp.loc</code> to <code>/etc/hosts</code> (WDC prompts for admin the first time).</li>" +
                "<li>Manage PHP versions, Apache config, MariaDB, Redis from the sidebar.</li></ul>" +
                "<p style=\"margin-top:3rem;color:#6b7280;font-size:.85em;\">Generated by NKS WDC — remove this file or change <code>DocumentRoot</code> to point elsewhere.</p>" +
                "</body></html>");
        }
        if (!siteManager.Sites.Keys.Any(d => string.Equals(d, "localhost", StringComparison.OrdinalIgnoreCase)))
        {
            // CreateAsync persists the vhost TOML + registers the site in the
            // SiteManager dictionary. ApplyAsync alone would generate the Apache
            // vhost file but leave SiteManager unaware of the site, so on next
            // daemon restart the site would vanish. This is the same order the
            // /api/sites POST handler uses.
            // PhpVersion empty lets ApacheModule pick the daemon's active
            // version at orchestration time instead of pinning to a
            // possibly-not-installed "8.5". SslEnabled: true so the seed
            // site exposes https://localhost/ out of the gate — mkcert
            // issues the cert during ApplyAsync, the vhost config gets
            // both :80 and :443 blocks, and the user can click the first
            // link they see and have a working HTTPS loopback.
            var created = await siteManager.CreateAsync(new NKS.WebDevConsole.Core.Models.SiteConfig
            {
                Domain = "localhost",
                DocumentRoot = docRoot,
                PhpVersion = "",
                SslEnabled = true,
                Enabled = true,
            });
            await siteOrch.ApplyAsync(created, CancellationToken.None);
        }
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Failed to seed default localhost site");
    }
    return Results.Ok(new { completed = true });
});

// ── Backup helper functions ───────────────────────────────────────────────

BackupContentFlags ReadContentFlagsFromSettings(SettingsStore s)
{
    var flags = BackupContentFlags.None;
    if (s.GetBool("backup", "content.vhosts",      defaultValue: true))  flags |= BackupContentFlags.Vhosts;
    if (s.GetBool("backup", "content.pluginConfigs",defaultValue: true))  flags |= BackupContentFlags.PluginConfigs;
    if (s.GetBool("backup", "content.ssl",          defaultValue: true))  flags |= BackupContentFlags.Ssl;
    if (s.GetBool("backup", "content.databases",    defaultValue: false)) flags |= BackupContentFlags.Databases;
    if (s.GetBool("backup", "content.docroots",     defaultValue: false)) flags |= BackupContentFlags.Docroots;
    return flags == BackupContentFlags.None ? BackupContentFlags.Default : flags;
}

BackupContentFlags ParseContentFlagsFromDict(Dictionary<string, bool> dict)
{
    var flags = BackupContentFlags.None;
    if (dict.GetValueOrDefault("vhosts",       true))  flags |= BackupContentFlags.Vhosts;
    if (dict.GetValueOrDefault("pluginConfigs", true))  flags |= BackupContentFlags.PluginConfigs;
    if (dict.GetValueOrDefault("ssl",           true))  flags |= BackupContentFlags.Ssl;
    if (dict.GetValueOrDefault("databases",     false)) flags |= BackupContentFlags.Databases;
    if (dict.GetValueOrDefault("docroots",      false)) flags |= BackupContentFlags.Docroots;
    return flags == BackupContentFlags.None ? BackupContentFlags.Default : flags;
}

// Backup / restore — tasks 14/22/35.
// Content-flag-aware backup: vhosts, SSL, plugin-configs default ON;
// databases and docroots are opt-in.
app.MapGet("/api/backup/list", (BackupManager bm) =>
{
    var list = bm.ListBackups()
        .Select(b => new { path = b.Path, size = b.Size, createdUtc = b.Created, contentFlags = b.ContentFlags.ToString() })
        .ToList();
    return Results.Ok(new { count = list.Count, backups = list });
});

app.MapGet("/api/backup/stats", (BackupManager bm) =>
{
    var backups = bm.ListBackups();
    var totalSize = backups.Sum(b => b.Size);
    var lastCreated = backups.Count > 0 ? backups[0].Created : (DateTime?)null;
    return Results.Ok(new { count = backups.Count, totalSize, lastCreatedUtc = lastCreated });
});

app.MapPost("/api/backup", async (BackupManager bm, SettingsStore settings, HttpContext ctx) =>
{
    try
    {
        var outPath = ctx.Request.Query["out"].FirstOrDefault();
        BackupContentFlags flags = BackupContentFlags.Default;

        // Body may contain explicit contentFlags override
        if (ctx.Request.ContentLength > 0 && ctx.Request.ContentType?.Contains("json") == true)
        {
            try
            {
                var body = await ctx.Request.ReadFromJsonAsync<BackupRequestBody>(caseInsensitiveJson);
                if (body?.ContentFlags is not null)
                    flags = ParseContentFlagsFromDict(body.ContentFlags);
                else
                    flags = ReadContentFlagsFromSettings(settings);
            }
            catch { flags = ReadContentFlagsFromSettings(settings); }
        }
        else
        {
            flags = ReadContentFlagsFromSettings(settings);
        }

        var (path, files, size, resultFlags) = bm.CreateBackup(outPath, flags);
        return Results.Ok(new { path, files, size, contentFlags = resultFlags.ToString() });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Backup failed: {ex.Message}");
    }
});

app.MapDelete("/api/backup/{id}", (BackupManager bm, string id) =>
{
    var backupRoot = Path.GetFullPath(NKS.WebDevConsole.Core.Services.WdcPaths.BackupsRoot);
    var resolved = Path.GetFullPath(Path.Combine(backupRoot, id));
    if (!resolved.StartsWith(backupRoot, StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Path escapes backup root" });
    if (!File.Exists(resolved)) return Results.NotFound(new { error = "Backup not found" });
    try { File.Delete(resolved); return Results.Ok(new { deleted = id }); }
    catch (Exception ex) { return Results.Problem($"Delete failed: {ex.Message}"); }
});

app.MapGet("/api/backup/download", (BackupManager bm, string? path) =>
{
    string target;
    if (string.IsNullOrEmpty(path))
    {
        var backupsList = bm.ListBackups();
        if (backupsList.Count == 0) return Results.NotFound(new { error = "No backups available" });
        var latest = backupsList[0];
        target = latest.Path;
    }
    else
    {
        var backupRoot = Path.GetFullPath(NKS.WebDevConsole.Core.Services.WdcPaths.BackupsRoot);
        var resolved = Path.GetFullPath(path);
        if (!resolved.StartsWith(backupRoot, StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "Path escapes backup root" });
        target = resolved;
    }
    if (!File.Exists(target)) return Results.NotFound(new { error = "Backup file not found" });
    return Results.File(target, "application/zip", Path.GetFileName(target));
});

app.MapPost("/api/restore", async (BackupManager bm, HttpContext ctx) =>
{
    try
    {
        string archivePath;
        if (ctx.Request.HasFormContentType)
        {
            var form = await ctx.Request.ReadFormAsync();
            var file = form.Files.FirstOrDefault();
            if (file is null) return Results.BadRequest(new { error = "No file uploaded" });
            var tmp = Path.Combine(Path.GetTempPath(), $"wdc-restore-{Guid.NewGuid():N}.zip");
            using (var fs = File.Create(tmp))
                await file.CopyToAsync(fs);
            archivePath = tmp;
        }
        else
        {
            var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>();
            var requested = body?.GetValueOrDefault("path");
            if (string.IsNullOrWhiteSpace(requested))
                return Results.BadRequest(new { error = "path or multipart file required" });
            var backupRoot = Path.GetFullPath(NKS.WebDevConsole.Core.Services.WdcPaths.BackupsRoot);
            var resolved = Path.GetFullPath(requested);
            if (!resolved.StartsWith(backupRoot, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "Path escapes backup root" });
            archivePath = resolved;
        }

        var (restored, safety) = bm.RestoreBackup(archivePath);
        return Results.Ok(new { restored, safetyBackup = safety, archive = archivePath });
    }
    catch (System.Text.Json.JsonException ex)
    {
        return Results.BadRequest(new { error = $"Invalid JSON body: {ex.Message}" });
    }
    catch (FileNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (Exception ex) { return Results.Problem($"Restore failed: {ex.Message}"); }
});

app.MapPost("/api/backup/{id}/restore", (BackupManager bm, string id) =>
{
    var backupRoot = Path.GetFullPath(NKS.WebDevConsole.Core.Services.WdcPaths.BackupsRoot);
    var resolved = Path.GetFullPath(Path.Combine(backupRoot, id));
    if (!resolved.StartsWith(backupRoot, StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Path escapes backup root" });
    if (!File.Exists(resolved)) return Results.NotFound(new { error = "Backup not found" });
    try
    {
        var (restored, safety) = bm.RestoreBackup(resolved);
        return Results.Ok(new { restored, safetyBackup = safety, archive = id });
    }
    catch (Exception ex) { return Results.Problem($"Restore failed: {ex.Message}"); }
});

// MAMP PRO migration — Phase 5 plan item.
// Discover-only: scan well-known MAMP install paths for vhost files, return parsed sites
// without writing anything. Use POST /api/sites/migrate-mamp to actually import.
app.MapGet("/api/sites/discover-mamp", (MampMigrator migrator) =>
{
    try
    {
        var found = migrator.Discover();
        return Results.Ok(new { count = found.Count, sites = found });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Discovery failed: {ex.Message}");
    }
});

app.MapPost("/api/sites/migrate-mamp", async (MampMigrator migrator, SiteManager sm) =>
{
    try
    {
        var imported = await migrator.ImportAsync(sm);
        return Results.Ok(new { count = imported.Count, imported });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Migration failed: {ex.Message}");
    }
});

app.MapPost("/api/sites/reapply-all", async (SiteManager sm, SiteOrchestrator orchestrator) =>
{
    var results = new List<object>();
    foreach (var site in sm.Sites.Values)
    {
        try
        {
            await orchestrator.ApplyAsync(site);
            results.Add(new { domain = site.Domain, ok = true });
        }
        catch (Exception ex)
        {
            results.Add(new { domain = site.Domain, ok = false, error = ex.Message });
        }
    }
    return Results.Ok(results);
});

// ── Hosts file management ─────────────────────────────────────────────
// Requires the process to be elevated (hosts file ACL on Windows). All
// write endpoints return 403 with a clear message when not elevated so
// the frontend can show a friendly "restart as admin" prompt.

static string GetHostsPath() =>
    OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts")
        : "/etc/hosts";

static bool IsElevated()
{
    if (!OperatingSystem.IsWindows()) return true;
    using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
    var principal = new System.Security.Principal.WindowsPrincipal(identity);
    return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
}

static bool IsIpValid(string ip)
{
    return System.Net.IPAddress.TryParse(ip.Trim(), out _);
}

static bool IsHostnameValid(string h)
{
    if (string.IsNullOrWhiteSpace(h)) return false;
    h = h.Trim();
    if (h.Length > 253) return false;
    return System.Text.RegularExpressions.Regex.IsMatch(
        h, @"^[a-zA-Z0-9]([a-zA-Z0-9\-]{0,61}[a-zA-Z0-9])?(\.[a-zA-Z0-9]([a-zA-Z0-9\-]{0,61}[a-zA-Z0-9])?)*$");
}

/// <summary>Parses all lines of the hosts file into structured entries.</summary>
// F82 helper: detect IP-like tokens so ParseHostsEntries can skip
// comment headers (`## MAMP PRO hosts`) that would otherwise be
// parsed as ip="MAMP" hostname="PRO".
static bool LooksLikeIp(string token)
{
    if (string.IsNullOrEmpty(token)) return false;
    // IPv6 has at least one ':' and no spaces.
    if (token.Contains(':')) return true;
    // IPv4 dotted-quad: 4 dot-separated decimal octets 0-255.
    var octets = token.Split('.');
    if (octets.Length != 4) return false;
    foreach (var o in octets)
    {
        if (!byte.TryParse(o, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out _))
            return false;
    }
    return true;
}

static List<HostsEntryDto> ParseHostsEntries(string content, HashSet<string> wdcDomains)
{
    const string beginMarker = "# BEGIN NKS WebDev Console";
    const string endMarker   = "# END NKS WebDev Console";

    // Well-known external entries that ship with Windows
    var windowsDefaults = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "localhost", "ip6-localhost", "ip6-loopback", "broadcasthost",
    };
    var windowsDefaultIps = new HashSet<string> { "127.0.0.1", "::1", "255.255.255.255", "fe80::1%lo0" };

    var entries = new List<HostsEntryDto>();
    var lines = content.Split('\n');
    bool inWdc = false;

    for (int i = 0; i < lines.Length; i++)
    {
        var raw = lines[i].TrimEnd('\r');
        var trimmed = raw.Trim();

        if (trimmed == beginMarker) { inWdc = true; continue; }
        if (trimmed == endMarker)   { inWdc = false; continue; }

        // Skip fully-empty lines
        if (trimmed.Length == 0) continue;

        bool enabled = !trimmed.StartsWith('#');
        var working = enabled ? trimmed : trimmed.TrimStart('#').Trim();

        // Extract inline comment
        string? comment = null;
        var commentIdx = working.IndexOf('#');
        if (commentIdx >= 0)
        {
            comment = working[(commentIdx + 1)..].Trim();
            if (comment.Length == 0) comment = null;
            working = working[..commentIdx].Trim();
        }

        var parts = working.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) continue;

        var ip = parts[0];
        // F82: reject lines whose first token doesn't look like an IP. MAMP /
        // XAMPP dump section headers like `## MAMP PRO hosts` into the file —
        // after strip-# + trim the working content becomes `MAMP PRO hosts`
        // and the old parser happily created an entry with ip="MAMP"
        // hostname="PRO", which is the "bordel" showing up in the UI table.
        // Treat such lines as pure comments (skip them). IPv4 dotted-quad OR
        // any token with a colon (IPv6) is accepted; everything else is junk.
        if (!LooksLikeIp(ip)) continue;

        for (int j = 1; j < parts.Length; j++)
        {
            var hostname = parts[j];
            string source;
            if (inWdc)
                source = "wdc";
            else if (windowsDefaults.Contains(hostname) || windowsDefaultIps.Contains(ip))
                source = "external";
            else
                source = "custom";

            entries.Add(new HostsEntryDto(
                Enabled:    enabled,
                Ip:         ip,
                Hostname:   hostname,
                Source:     source,
                Comment:    comment,
                LineNumber: i + 1));
        }
    }
    return entries;
}

app.MapGet("/api/hosts", (SiteManager sm) =>
{
    var hostsPath = GetHostsPath();
    if (!File.Exists(hostsPath))
        return Results.Problem("Hosts file not found");

    try
    {
        var content = File.ReadAllText(hostsPath);
        var wdcDomains = sm.Sites.Values
            .SelectMany(s => new[] { s.Domain }.Concat(s.Aliases ?? []))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entries = ParseHostsEntries(content, wdcDomains);
        return Results.Ok(entries);
    }
    catch (Exception ex)
    {
        return Results.Problem($"Cannot read hosts file: {ex.Message}");
    }
});

app.MapPost("/api/hosts/apply", async (HttpContext ctx) =>
{
    if (!IsElevated())
        return Results.Json(new { error = "Requires administrator — restart WDC as admin" }, statusCode: 403);

    HostsApplyRequest? req;
    try { req = await ctx.Request.ReadFromJsonAsync<HostsApplyRequest>(); }
    catch { return Results.BadRequest(new { error = "Invalid JSON body" }); }

    if (req?.Entries is null)
        return Results.BadRequest(new { error = "Missing entries array" });

    foreach (var e in req.Entries)
    {
        if (!IsIpValid(e.Ip))
            return Results.BadRequest(new { error = $"Invalid IP: {e.Ip}" });
        if (!IsHostnameValid(e.Hostname))
            return Results.BadRequest(new { error = $"Invalid hostname: {e.Hostname}" });
    }

    var hostsPath = GetHostsPath();
    const string beginMarker = "# BEGIN NKS WebDev Console";
    const string endMarker   = "# END NKS WebDev Console";

    try
    {
        var content = await File.ReadAllTextAsync(hostsPath);

        // Build new managed block from wdc entries
        var wdcEntries = req.Entries.Where(e => e.Source == "wdc").ToList();
        var wdcLines = new List<string> { beginMarker };
        foreach (var e in wdcEntries)
        {
            var prefix = e.Enabled ? "" : "# ";
            var comment = string.IsNullOrWhiteSpace(e.Comment) ? "" : $" # {e.Comment.Trim()}";
            wdcLines.Add($"{prefix}{e.Ip}\t{e.Hostname}{comment}");
        }
        wdcLines.Add(endMarker);

        // Build non-wdc lines (custom + external) from request
        var nonWdcLines = new List<string>();
        foreach (var e in req.Entries.Where(e => e.Source != "wdc"))
        {
            var prefix = e.Enabled ? "" : "# ";
            var comment = string.IsNullOrWhiteSpace(e.Comment) ? "" : $" # {e.Comment.Trim()}";
            nonWdcLines.Add($"{prefix}{e.Ip}\t{e.Hostname}{comment}");
        }

        // Rebuild: strip existing managed block, replace with new one, keep external
        // Strategy: strip begin..end block, inject new block, rebuild non-wdc lines
        var beginIdx = content.IndexOf(beginMarker, StringComparison.Ordinal);
        var endIdx = content.IndexOf(endMarker, StringComparison.Ordinal);

        string before, after;
        if (beginIdx >= 0 && endIdx >= beginIdx)
        {
            var endOfEnd = endIdx + endMarker.Length;
            if (endOfEnd < content.Length && content[endOfEnd] == '\r') endOfEnd++;
            if (endOfEnd < content.Length && content[endOfEnd] == '\n') endOfEnd++;
            before = content[..beginIdx].TrimEnd();
            after = content[endOfEnd..].TrimStart();
        }
        else
        {
            before = content.TrimEnd();
            after = string.Empty;
        }

        var sb = new System.Text.StringBuilder();
        if (before.Length > 0) sb.AppendLine(before).AppendLine();
        sb.AppendLine(string.Join(Environment.NewLine, wdcLines));
        if (after.Length > 0) sb.AppendLine().Append(after);

        var newContent = sb.ToString();

        // Safety check
        if (newContent.Length < content.Length / 4 && content.Length > 200)
            return Results.Problem("Safety abort: new content suspiciously small");

        // Backup rotation (keep last 5)
        for (int i = 4; i >= 1; i--)
        {
            var src = $"{hostsPath}.wdc-backup.{i}";
            var dst = $"{hostsPath}.wdc-backup.{i + 1}";
            if (File.Exists(src)) File.Move(src, dst, overwrite: true);
        }
        File.Copy(hostsPath, $"{hostsPath}.wdc-backup.1", overwrite: true);

        await File.WriteAllTextAsync(hostsPath, newContent, System.Text.Encoding.ASCII);

        try { using var p = System.Diagnostics.Process.Start("ipconfig", "/flushdns"); p?.WaitForExit(5000); } catch { }

        return Results.Ok(new { applied = true, entryCount = req.Entries.Count });
    }
    catch (UnauthorizedAccessException)
    {
        return Results.Json(new { error = "Requires administrator — restart WDC as admin" }, statusCode: 403);
    }
    catch (Exception ex)
    {
        return Results.Problem($"Failed to apply hosts: {ex.Message}");
    }
});

app.MapPost("/api/hosts/backup", () =>
{
    var hostsPath = GetHostsPath();
    if (!File.Exists(hostsPath))
        return Results.NotFound(new { error = "Hosts file not found" });

    var backupsDir = Path.Combine(NKS.WebDevConsole.Core.Services.WdcPaths.BackupsRoot, "hosts");
    Directory.CreateDirectory(backupsDir);

    var ts = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ");
    var destPath = Path.Combine(backupsDir, $"hosts-{ts}.bak");

    try
    {
        File.Copy(hostsPath, destPath, overwrite: false);
        return Results.Ok(new { path = destPath, timestamp = ts });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Backup failed: {ex.Message}");
    }
});

app.MapPost("/api/hosts/restore", async (HttpContext ctx) =>
{
    if (!IsElevated())
        return Results.Json(new { error = "Requires administrator — restart WDC as admin" }, statusCode: 403);

    HostsRestoreRequest? req;
    try { req = await ctx.Request.ReadFromJsonAsync<HostsRestoreRequest>(); }
    catch { return Results.BadRequest(new { error = "Invalid JSON body" }); }

    if (req is null || (string.IsNullOrWhiteSpace(req.Path) && string.IsNullOrWhiteSpace(req.Content)))
        return Results.BadRequest(new { error = "Provide path or content" });

    // F58: Clamp content size to 10 MiB — defense-in-depth against malicious
    // or accidental oversized upload. A real hosts file is never more than a
    // few hundred KiB; anything north of 10 MiB is a bug or abuse.
    const int MaxHostsBytes = 10 * 1024 * 1024;

    var hostsPath = GetHostsPath();
    try
    {
        string newContent;
        if (!string.IsNullOrWhiteSpace(req.Path))
        {
            if (!File.Exists(req.Path))
                return Results.NotFound(new { error = $"Backup file not found: {req.Path}" });
            var fi = new FileInfo(req.Path);
            if (fi.Length > MaxHostsBytes)
                return Results.BadRequest(new { error = $"Backup file too large ({fi.Length} bytes, max {MaxHostsBytes})" });
            newContent = await File.ReadAllTextAsync(req.Path);
        }
        else
        {
            if (req.Content!.Length > MaxHostsBytes)
                return Results.BadRequest(new { error = $"Content too large ({req.Content.Length} chars, max {MaxHostsBytes})" });
            newContent = req.Content!;
        }

        // Safety: backup current before restore
        for (int i = 4; i >= 1; i--)
        {
            var src = $"{hostsPath}.wdc-backup.{i}";
            var dst = $"{hostsPath}.wdc-backup.{i + 1}";
            if (File.Exists(src)) File.Move(src, dst, overwrite: true);
        }
        if (File.Exists(hostsPath))
            File.Copy(hostsPath, $"{hostsPath}.wdc-backup.1", overwrite: true);

        await File.WriteAllTextAsync(hostsPath, newContent, System.Text.Encoding.ASCII);
        try { using var p = System.Diagnostics.Process.Start("ipconfig", "/flushdns"); p?.WaitForExit(5000); } catch { }

        return Results.Ok(new { restored = true });
    }
    catch (UnauthorizedAccessException)
    {
        return Results.Json(new { error = "Requires administrator — restart WDC as admin" }, statusCode: 403);
    }
    catch (Exception ex)
    {
        return Results.Problem($"Restore failed: {ex.Message}");
    }
});

app.MapPost("/api/sites/{domain}/detect-framework", async (string domain, SiteManager sm) =>
{
    var site = sm.Get(domain);
    if (site is null) return Results.NotFound();
    try
    {
        // DetectFramework reads files from documentRoot — can throw on
        // missing dir, permission denied, or path traversal in domain.
        // UpdateAsync writes the TOML config — can throw on disk full.
        var framework = sm.DetectFramework(site.DocumentRoot);
        if (framework is not null && framework != site.Framework)
        {
            site.Framework = framework;
            await sm.UpdateAsync(site);
        }
        return Results.Ok(new { domain, framework });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Detect framework failed: {ex.Message}");
    }
});

// Filesystem browse — backs the FolderBrowser dialog in the site-edit UI
// so users can pick a document root via a tree instead of typing a raw
// Windows path. No sandboxing: the GUI runs with the same permissions as
// the user who launched it anyway, and the alternative (restricting to
// %USERPROFILE%) would block legitimate picks like C:\xampp\htdocs.
app.MapGet("/api/fs/browse", (string? path) =>
{
    try
    {
        string target;
        if (string.IsNullOrWhiteSpace(path))
        {
            // Default landing: drives on Windows, $HOME otherwise
            if (OperatingSystem.IsWindows())
            {
                var drives = DriveInfo.GetDrives()
                    .Where(d => d.IsReady)
                    .Select(d => new
                    {
                        name = d.Name,
                        path = d.Name,
                        isDir = true,
                        isFile = false,
                        size = 0L,
                    })
                    .ToArray();
                return Results.Ok(new
                {
                    path = "",
                    parent = (string?)null,
                    entries = drives,
                });
            }
            target = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        else
        {
            target = Path.GetFullPath(path);
        }

        if (!Directory.Exists(target)) return Results.NotFound(new { error = $"Not a directory: {target}" });

        var parent = Directory.GetParent(target)?.FullName;

        var dirs = Directory.EnumerateDirectories(target)
            .Select(d => new DirectoryInfo(d))
            .Where(di => (di.Attributes & FileAttributes.Hidden) == 0
                     && (di.Attributes & FileAttributes.System) == 0)
            .Select(di => new
            {
                name = di.Name,
                path = di.FullName,
                isDir = true,
                isFile = false,
                size = 0L,
            });

        var files = Directory.EnumerateFiles(target)
            .Select(f => new FileInfo(f))
            .Where(fi => (fi.Attributes & FileAttributes.Hidden) == 0
                     && (fi.Attributes & FileAttributes.System) == 0)
            .Select(fi => new
            {
                name = fi.Name,
                path = fi.FullName,
                isDir = false,
                isFile = true,
                size = fi.Length,
            });

        return Results.Ok(new
        {
            path = target,
            parent,
            entries = dirs.OrderBy(e => e.name).Concat(files.OrderBy(e => e.name)).ToArray(),
        });
    }
    catch (UnauthorizedAccessException)
    {
        return Results.StatusCode(403);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

// ── Cloudflare Tunnel plugin — passthrough endpoints ─────────────────────
// The Cloudflare plugin lives in a separate AssemblyLoadContext so we
// can't reference its types directly without polluting the daemon's
// dependency graph. Resolve the plugin instance + CloudflareApi /
// CloudflareConfig via reflection and invoke methods through the shared
// IServiceProvider. Everything returns the raw Cloudflare API JSON blob
// so the frontend can render any subset without hand-written DTOs.

object? ResolveCloudflareServiceOrNull(IServiceProvider sp, string typeName)
{
    var plugin = pluginLoader.Plugins
        .FirstOrDefault(p => p.Instance.Id == "nks.wdc.cloudflare");
    if (plugin == null) return null;
    var t = plugin.Assembly.GetTypes().FirstOrDefault(x => x.Name == typeName);
    if (t == null) return null;
    return sp.GetService(t);
}

async Task<IResult> InvokeCfAsync(string apiMethodName, object[] args, IServiceProvider sp)
{
    var api = ResolveCloudflareServiceOrNull(sp, "CloudflareApi");
    if (api == null) return Results.NotFound(new { error = "Cloudflare plugin not loaded" });
    var method = api.GetType().GetMethod(apiMethodName);
    if (method == null) return Results.NotFound(new { error = $"Method {apiMethodName} not found" });
    try
    {
        var task = (Task)method.Invoke(api, args)!;
        await task.ConfigureAwait(false);
        var resultProp = task.GetType().GetProperty("Result");
        var value = resultProp?.GetValue(task);
        return Results.Ok(value);
    }
    catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException is not null)
    {
        return Results.BadRequest(new { error = tie.InnerException.Message });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}

// Settings: GET returns redacted config (secrets masked), PUT saves
app.MapGet("/api/cloudflare/config", (IServiceProvider sp) =>
{
    var cfg = ResolveCloudflareServiceOrNull(sp, "CloudflareConfig");
    if (cfg == null) return Results.NotFound(new { error = "Cloudflare plugin not loaded" });
    var redactedMethod = cfg.GetType().GetMethod("Redacted");
    var redacted = redactedMethod?.Invoke(cfg, null);
    return Results.Ok(redacted);
});

app.MapPut("/api/cloudflare/config", async (HttpContext ctx, IServiceProvider sp) =>
{
    var cfg = ResolveCloudflareServiceOrNull(sp, "CloudflareConfig");
    if (cfg == null) return Results.NotFound(new { error = "Cloudflare plugin not loaded" });

    // Accept a loose JSON body and copy known properties onto the live
    // config instance. Fields omitted from the body stay untouched.
    System.Text.Json.JsonDocument doc;
    try
    {
        doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body);
    }
    catch (System.Text.Json.JsonException ex)
    {
        // Malformed JSON in the body — return 400 with the parser's
        // line/column hint instead of letting the auth middleware
        // surface a generic 500 stack trace from the unhandled throw.
        return Results.BadRequest(new { error = $"Invalid JSON body: {ex.Message}" });
    }

    using (doc)
    {
        var root = doc.RootElement;
        var t = cfg.GetType();

        void Apply(string jsonKey, string propName)
        {
            if (!root.TryGetProperty(jsonKey, out var el)) return;
            if (el.ValueKind != System.Text.Json.JsonValueKind.String &&
                el.ValueKind != System.Text.Json.JsonValueKind.Null) return;
            var prop = t.GetProperty(propName);
            prop?.SetValue(cfg, el.ValueKind == System.Text.Json.JsonValueKind.Null ? null : el.GetString());
        }

        Apply("cloudflaredPath", "CloudflaredPath");
        Apply("tunnelToken", "TunnelToken");
        Apply("tunnelName", "TunnelName");
        Apply("tunnelId", "TunnelId");
        Apply("apiToken", "ApiToken");
        Apply("accountId", "AccountId");
        Apply("defaultZoneId", "DefaultZoneId");
        Apply("subdomainTemplate", "SubdomainTemplate");

        // Save can fail with IOException (disk full, perms) — surface that
        // as a 500 with the actual cause instead of a stack trace.
        try
        {
            var saveMethod = t.GetMethod("Save");
            saveMethod?.Invoke(cfg, null);
        }
        catch (Exception ex)
        {
            return Results.Problem($"Failed to persist Cloudflare config: {ex.InnerException?.Message ?? ex.Message}");
        }

        var redacted = t.GetMethod("Redacted")?.Invoke(cfg, null);
        return Results.Ok(redacted);
    }
});

app.MapGet("/api/cloudflare/verify", (IServiceProvider sp) =>
    InvokeCfAsync("VerifyTokenAsync", new object[] { CancellationToken.None }, sp));

// Compute a suggested public subdomain for a local domain. The Cloudflare
// plugin owns the template + install-salt hash logic, so SiteEdit can show
// a stable auto-filled value like "myapp-bffa44" that doesn't drift.
app.MapGet("/api/cloudflare/suggest-subdomain", (string domain, IServiceProvider sp) =>
{
    if (string.IsNullOrWhiteSpace(domain))
        return Results.BadRequest(new { error = "domain query param required" });
    var cfg = ResolveCloudflareServiceOrNull(sp, "CloudflareConfig");
    if (cfg == null)
        return Results.NotFound(new { error = "Cloudflare plugin not loaded" });
    var renderMethod = cfg.GetType().GetMethod("RenderSubdomain");
    if (renderMethod == null)
        return Results.Problem("RenderSubdomain not available");
    var suggestion = renderMethod.Invoke(cfg, new object[] { domain }) as string;
    return Results.Ok(new { suggestion, domain });
});

// One-token auto-setup: user pastes an API token → we verify it, list
// accounts, pick the first one, find or create a tunnel named
// NKS-WDC-Tunnel-{md5[..12]}, fetch its JWT, and persist everything.
// After this the user never has to enter an account/tunnel/jwt manually —
// zones + per-site configuration are the only remaining inputs.
app.MapPost("/api/cloudflare/auto-setup", async (HttpContext ctx, IServiceProvider sp) =>
{
    var cfg = ResolveCloudflareServiceOrNull(sp, "CloudflareConfig");
    var api = ResolveCloudflareServiceOrNull(sp, "CloudflareApi");
    if (cfg == null || api == null)
        return Results.NotFound(new { error = "Cloudflare plugin not loaded" });

    System.Text.Json.JsonDocument doc;
    try
    {
        doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body);
    }
    catch (System.Text.Json.JsonException ex)
    {
        return Results.BadRequest(new { error = $"Invalid JSON body: {ex.Message}" });
    }
    using var _doc = doc;
    if (!doc.RootElement.TryGetProperty("apiToken", out var tokenEl) ||
        tokenEl.ValueKind != System.Text.Json.JsonValueKind.String ||
        string.IsNullOrWhiteSpace(tokenEl.GetString()))
    {
        return Results.BadRequest(new { error = "apiToken is required and must be a non-empty string" });
    }
    var token = tokenEl.GetString()!;

    // 1. Stage the token onto the live config so the API wrapper picks it up.
    var tCfg = cfg.GetType();
    tCfg.GetProperty("ApiToken")?.SetValue(cfg, token);

    try
    {
        // 2. Verify — fail fast with a readable error if the token is wrong
        var verifyMethod = api.GetType().GetMethod("VerifyTokenAsync");
        var verifyTask = (Task)verifyMethod!.Invoke(api, new object[] { CancellationToken.None })!;
        await verifyTask;

        // 3. Pick account — use first returned one, or fall back to whatever
        //    the user already had saved (lets power-users override)
        var listAccounts = api.GetType().GetMethod("ListAccountsAsync");
        var accountsTask = (Task)listAccounts!.Invoke(api, new object[] { CancellationToken.None })!;
        await accountsTask;
        var accountsJson = (System.Text.Json.JsonElement)accountsTask.GetType().GetProperty("Result")!.GetValue(accountsTask)!;
        string? accountId = null;
        string? accountName = null;
        if (accountsJson.TryGetProperty("result", out var arr) &&
            arr.ValueKind == System.Text.Json.JsonValueKind.Array &&
            arr.GetArrayLength() > 0)
        {
            accountId = arr[0].GetProperty("id").GetString();
            accountName = arr[0].GetProperty("name").GetString();
        }
        if (string.IsNullOrEmpty(accountId))
            return Results.BadRequest(new { error = "Token has no associated accounts — add Account read scope" });

        tCfg.GetProperty("AccountId")?.SetValue(cfg, accountId);

        // 4. Deterministic tunnel name — same token always resolves to the
        //    same tunnel so repeated auto-setup runs don't create dupes.
        var md5 = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(token));
        var md5Hex = Convert.ToHexString(md5).ToLowerInvariant()[..12];
        var tunnelName = $"NKS-WDC-Tunnel-{md5Hex}";

        var findOrCreate = api.GetType().GetMethod("FindOrCreateTunnelAsync");
        var tunnelTask = (Task)findOrCreate!.Invoke(api, new object[] { tunnelName, CancellationToken.None })!;
        await tunnelTask;
        var tunnelJson = (System.Text.Json.JsonElement)tunnelTask.GetType().GetProperty("Result")!.GetValue(tunnelTask)!;
        var tunnelId = tunnelJson.GetProperty("id").GetString();

        tCfg.GetProperty("TunnelId")?.SetValue(cfg, tunnelId);
        tCfg.GetProperty("TunnelName")?.SetValue(cfg, tunnelName);

        // 5. Fetch JWT — cloudflared needs this to run the tunnel locally
        var getToken = api.GetType().GetMethod("GetTunnelTokenAsync");
        var jwtTask = (Task)getToken!.Invoke(api, new object[] { tunnelId!, CancellationToken.None })!;
        await jwtTask;
        var jwt = (string?)jwtTask.GetType().GetProperty("Result")!.GetValue(jwtTask);
        if (!string.IsNullOrEmpty(jwt))
            tCfg.GetProperty("TunnelToken")?.SetValue(cfg, jwt);

        // 6. Persist everything
        tCfg.GetMethod("Save")?.Invoke(cfg, null);

        return Results.Ok(new
        {
            ok = true,
            account = new { id = accountId, name = accountName },
            tunnel = new { id = tunnelId, name = tunnelName },
            tokenFetched = !string.IsNullOrEmpty(jwt),
        });
    }
    catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException is not null)
    {
        return Results.BadRequest(new { error = tie.InnerException.Message });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// Sync all sites that have Cloudflare.Enabled=true:
//   - Upserts a proxied CNAME per site → tunnelId.cfargotunnel.com
//   - Rebuilds the tunnel ingress config with one rule per site + 404
//   - Each ingress rule carries httpHostHeader = site.Domain so Apache
//     matches the LOCAL vhost rather than the public hostname
app.MapPost("/api/cloudflare/sync", async (IServiceProvider sp, SiteManager sm) =>
{
    var cfg = ResolveCloudflareServiceOrNull(sp, "CloudflareConfig");
    var api = ResolveCloudflareServiceOrNull(sp, "CloudflareApi");
    if (cfg == null || api == null)
        return Results.NotFound(new { error = "Cloudflare plugin not loaded" });

    var tCfg = cfg.GetType();
    var tunnelId = tCfg.GetProperty("TunnelId")?.GetValue(cfg) as string;
    if (string.IsNullOrWhiteSpace(tunnelId))
        return Results.BadRequest(new { error = "Tunnel not configured. Run auto-setup first." });

    var sitesWithCf = sm.Sites.Values
        .Where(s => s.Cloudflare is { Enabled: true }
                 && !string.IsNullOrWhiteSpace(s.Cloudflare.ZoneId)
                 && !string.IsNullOrWhiteSpace(s.Cloudflare.Subdomain))
        .ToList();
    var dormantCf = sm.Sites.Values
        .Where(s => s.Cloudflare is { Enabled: false }
                 && !string.IsNullOrWhiteSpace(s.Cloudflare.ZoneId)
                 && !string.IsNullOrWhiteSpace(s.Cloudflare.Subdomain)
                 && !string.IsNullOrWhiteSpace(s.Cloudflare.ZoneName))
        .ToList();

    var upserted = new List<object>();
    var deleted = new List<object>();
    try
    {
        // DNS: one CNAME per enabled site
        var upsertMethod = api.GetType().GetMethod("UpsertCnameToTunnelAsync");
        foreach (var s in sitesWithCf)
        {
            var cf = s.Cloudflare!;
            var fullName = $"{cf.Subdomain}.{cf.ZoneName}";
            var task = (Task)upsertMethod!.Invoke(api,
                new object[] { cf.ZoneId, fullName, tunnelId, CancellationToken.None })!;
            await task;
            upserted.Add(new { domain = s.Domain, cname = fullName });
        }

        // DNS: delete CNAME for dormant (disabled-but-configured) sites so
        // toggling off in SiteEdit actually takes the public hostname down.
        var deleteMethod = api.GetType().GetMethod("DeleteCnameByNameAsync");
        foreach (var s in dormantCf)
        {
            var cf = s.Cloudflare!;
            var fullName = $"{cf.Subdomain}.{cf.ZoneName}";
            try
            {
                var task = (Task)deleteMethod!.Invoke(api,
                    new object[] { cf.ZoneId, fullName, CancellationToken.None })!;
                await task;
                deleted.Add(new { domain = s.Domain, cname = fullName });
            }
            catch { /* best-effort per-site */ }
        }

        // Ingress: one rule per site with httpHostHeader override
        var ruleType = api.GetType().Assembly.GetType(
            "NKS.WebDevConsole.Plugin.Cloudflare.TunnelIngressRule")!;
        var ruleListType = typeof(List<>).MakeGenericType(ruleType);
        var rules = (System.Collections.IList)Activator.CreateInstance(ruleListType)!;
        var ruleCtor = ruleType.GetConstructors().First();

        foreach (var s in sitesWithCf)
        {
            var cf = s.Cloudflare!;
            var hostname = $"{cf.Subdomain}.{cf.ZoneName}";

            // Mirror SiteOrchestrator.SyncCloudflareIfConfiguredAsync — pick HTTPS
            // when the local vhost has SSL so cloudflared bypasses the Apache
            // HTTP→HTTPS redirect. See that method for the full rationale.
            string service;
            string? originServerName = null;
            bool noTLSVerify = false;
            if (s.SslEnabled)
            {
                var httpsPort = s.HttpsPort > 0 ? s.HttpsPort : 443;
                service = $"https://localhost:{httpsPort}";
                originServerName = s.Domain;
                noTLSVerify = true;
            }
            else
            {
                var httpPort = s.HttpPort > 0 ? s.HttpPort : 80;
                service = $"http://localhost:{httpPort}";
            }

            rules.Add(ruleCtor.Invoke(new object?[]
            {
                hostname, service, s.Domain, originServerName, noTLSVerify,
            }));
        }

        var ingressMethod = api.GetType().GetMethod("UpdateTunnelIngressAsync");
        var ingressTask = (Task)ingressMethod!.Invoke(api,
            new object[] { tunnelId, rules, CancellationToken.None })!;
        await ingressTask;

        return Results.Ok(new
        {
            ok = true,
            synced = upserted.Count,
            sites = upserted,
            deleted = deleted.Count,
            dormant = deleted,
        });
    }
    catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException is not null)
    {
        return Results.BadRequest(new { error = tie.InnerException.Message });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/api/cloudflare/zones", (IServiceProvider sp) =>
    InvokeCfAsync("ListZonesAsync", new object[] { CancellationToken.None }, sp));

app.MapGet("/api/cloudflare/zones/{zoneId}", (string zoneId, IServiceProvider sp) =>
    InvokeCfAsync("GetZoneAsync", new object[] { zoneId, CancellationToken.None }, sp));

app.MapGet("/api/cloudflare/zones/{zoneId}/dns", (string zoneId, IServiceProvider sp) =>
    InvokeCfAsync("ListDnsRecordsAsync", new object[] { zoneId, CancellationToken.None }, sp));

app.MapPost("/api/cloudflare/zones/{zoneId}/dns", async (string zoneId, HttpContext ctx, IServiceProvider sp) =>
{
    System.Text.Json.JsonDocument doc;
    try
    {
        doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body);
    }
    catch (System.Text.Json.JsonException ex)
    {
        return Results.BadRequest(new { error = $"Invalid JSON body: {ex.Message}" });
    }
    using (doc)
    {
        var root = doc.RootElement;
        try
        {
            // Each TryGetProperty + typed getter pair can throw InvalidOperationException
            // if the value is the wrong shape (e.g. proxied="yes" instead of bool true).
            // Wrap the whole shape coercion so type mismatches surface as 400 with a
            // useful hint instead of bubbling to the auth middleware as 500.
            var type = root.TryGetProperty("type", out var tEl) && tEl.ValueKind == System.Text.Json.JsonValueKind.String
                ? tEl.GetString() ?? "CNAME" : "CNAME";
            var name = root.TryGetProperty("name", out var nEl) && nEl.ValueKind == System.Text.Json.JsonValueKind.String
                ? nEl.GetString() ?? "" : "";
            var content = root.TryGetProperty("content", out var cEl) && cEl.ValueKind == System.Text.Json.JsonValueKind.String
                ? cEl.GetString() ?? "" : "";
            var proxied = !root.TryGetProperty("proxied", out var pEl) ||
                          (pEl.ValueKind == System.Text.Json.JsonValueKind.True);
            var ttl = root.TryGetProperty("ttl", out var tEl2) && tEl2.ValueKind == System.Text.Json.JsonValueKind.Number
                ? tEl2.GetInt32() : 1;
            return await InvokeCfAsync("CreateDnsRecordAsync",
                new object[] { zoneId, type, name, content, proxied, ttl, CancellationToken.None }, sp);
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { error = $"Invalid DNS record shape: {ex.Message}" });
        }
    }
});

app.MapDelete("/api/cloudflare/zones/{zoneId}/dns/{recordId}", (string zoneId, string recordId, IServiceProvider sp) =>
    InvokeCfAsync("DeleteDnsRecordAsync", new object[] { zoneId, recordId, CancellationToken.None }, sp));

app.MapGet("/api/cloudflare/tunnels", (IServiceProvider sp) =>
    InvokeCfAsync("ListTunnelsAsync", new object[] { CancellationToken.None }, sp));

app.MapGet("/api/cloudflare/tunnels/{tunnelId}/configuration", (string tunnelId, IServiceProvider sp) =>
    InvokeCfAsync("GetTunnelConfigurationAsync", new object[] { tunnelId, CancellationToken.None }, sp));

// Replace the tunnel's ingress rules. Body shape:
//   { "rules": [ { "hostname": "blog.nks-dev.cz", "service": "http://localhost:80" }, ... ] }
// CloudflareApi.UpdateTunnelIngressAsync appends the mandatory catch-all
// 404 rule automatically so callers don't have to know the protocol detail.
app.MapPut("/api/cloudflare/tunnels/{tunnelId}/configuration",
    async (string tunnelId, HttpContext ctx, IServiceProvider sp) =>
{
    var api = ResolveCloudflareServiceOrNull(sp, "CloudflareApi");
    if (api == null) return Results.NotFound(new { error = "Cloudflare plugin not loaded" });

    System.Text.Json.JsonDocument doc;
    try
    {
        doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body);
    }
    catch (System.Text.Json.JsonException ex)
    {
        return Results.BadRequest(new { error = $"Invalid JSON body: {ex.Message}" });
    }
    using var _doc = doc;
    if (!doc.RootElement.TryGetProperty("rules", out var rulesEl) ||
        rulesEl.ValueKind != System.Text.Json.JsonValueKind.Array)
    {
        return Results.BadRequest(new { error = "Missing 'rules' array" });
    }

    // Build the plugin's TunnelIngressRule record via reflection so we don't
    // need a direct type reference into the plugin's ALC.
    var ruleType = api.GetType().Assembly.GetType(
        "NKS.WebDevConsole.Plugin.Cloudflare.TunnelIngressRule");
    if (ruleType == null)
        return Results.Problem("TunnelIngressRule type not found in plugin assembly");

    var ruleListType = typeof(List<>).MakeGenericType(ruleType);
    var ruleList = (System.Collections.IList)Activator.CreateInstance(ruleListType)!;
    var ruleCtor = ruleType.GetConstructors().First();

    foreach (var el in rulesEl.EnumerateArray())
    {
        var hostname = el.TryGetProperty("hostname", out var h) ? h.GetString() ?? "" : "";
        var service = el.TryGetProperty("service", out var s) ? s.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(hostname) || string.IsNullOrEmpty(service)) continue;
        ruleList.Add(ruleCtor.Invoke(new object[] { hostname, service }));
    }

    var method = api.GetType().GetMethod("UpdateTunnelIngressAsync");
    if (method == null) return Results.Problem("UpdateTunnelIngressAsync not found");

    try
    {
        var task = (Task)method.Invoke(api, new object[] { tunnelId, ruleList, CancellationToken.None })!;
        await task.ConfigureAwait(false);
        var resultProp = task.GetType().GetProperty("Result");
        return Results.Ok(resultProp?.GetValue(task));
    }
    catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException is not null)
    {
        return Results.BadRequest(new { error = tie.InnerException.Message });
    }
});

// PHP versions — delegate to PhpPlugin via reflection
app.MapGet("/api/php/versions", () =>
{
    var phpPlugin = pluginLoader.Plugins.FirstOrDefault(p => p.Instance.Id == "nks.wdc.php");
    if (phpPlugin == null) return Results.NotFound();
    var method = phpPlugin.Instance.GetType().GetMethod("GetInstalledVersions");
    if (method == null) return Results.Ok(Array.Empty<object>());
    try
    {
        var versions = method.Invoke(phpPlugin.Instance, null);
        // Null return would serialize as `null`, which the frontend
        // .map()'s over — normalize to empty array.
        return Results.Ok(versions ?? (object)Array.Empty<object>());
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "GetInstalledVersions reflection failed on php plugin");
        return Results.Ok(Array.Empty<object>());
    }
});

// PHP extensions for a given version
app.MapGet("/api/php/{version}/extensions", async (string version) =>
{
    var phpPlugin = pluginLoader.Plugins.FirstOrDefault(p => p.Instance.Id == "nks.wdc.php");
    if (phpPlugin == null) return Results.NotFound();
    var method = phpPlugin.Instance.GetType().GetMethod("GetExtensionsForVersion");
    if (method == null) return Results.Ok(Array.Empty<object>());
    try
    {
        if (method.Invoke(phpPlugin.Instance, new object[] { version }) is Task task)
        {
            await task;
            var resultProp = task.GetType().GetProperty("Result");
            var value = resultProp?.GetValue(task);
            // Normalize null → [] so the frontend's `.map` never throws.
            return Results.Ok(value ?? (object)Array.Empty<object>());
        }
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "GetExtensionsForVersion reflection failed on php plugin for {Version}", version);
    }
    return Results.Ok(Array.Empty<object>());
});

// Toggle a PHP extension on/off for a given version. Writes a persistent
// override via PhpExtensionOverrides, patches the live php.ini files in-place
// (comments / uncomments `extension=name` lines so the change survives the
// next daemon start even before the plugin re-runs its ini generator), and
// restarts the PHP module so the change takes effect immediately.
//
// Body: { "enabled": true|false }
// Path: /api/php/{version}/extensions/{name}  (version is major.minor, e.g. "8.4")
app.MapPost("/api/php/{version}/extensions/{name}", async (
    string version,
    string name,
    HttpContext ctx,
    PhpExtensionOverrides overrides,
    IServiceProvider sp,
    ILoggerFactory lf) =>
{
    // Input validation — extension names are lowercase alnum + underscore.
    if (!System.Text.RegularExpressions.Regex.IsMatch(version, @"^\d+\.\d+$"))
        return Results.BadRequest(new { error = "version must be major.minor, e.g. 8.4" });
    if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-z0-9_]{1,64}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        return Results.BadRequest(new { error = "invalid extension name" });

    var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, object?>>();
    var enabled = body is not null
        && body.TryGetValue("enabled", out var v)
        && v switch
        {
            bool b => b,
            JsonElement j when j.ValueKind == JsonValueKind.True => true,
            _ => false,
        };

    overrides.SetOverride(version, name, enabled);

    // Patch the live php.ini files — one next to the binary (used by
    // mod_fcgid-spawned php-cgi.exe) and one under _config.ConfigBaseDirectory
    // (used by the daemon's own invocations). We comment/uncomment the
    // `extension=name` directive so the change takes effect on next spawn.
    var phpRoot = Path.Combine(NKS.WebDevConsole.Core.Services.WdcPaths.BinariesRoot, "php");
    var patched = new List<string>();
    if (Directory.Exists(phpRoot))
    {
        foreach (var verDir in Directory.GetDirectories(phpRoot))
        {
            var dirName = Path.GetFileName(verDir);
            if (!dirName.StartsWith(version)) continue;
            var iniPath = Path.Combine(verDir, "php.ini");
            if (!File.Exists(iniPath)) continue;
            try
            {
                PatchExtensionLine(iniPath, name, enabled);
                patched.Add(iniPath);
            }
            catch (Exception ex)
            {
                lf.CreateLogger("PhpExt").LogWarning(ex, "Failed to patch {Path}", iniPath);
            }
        }
    }

    // Restart the PHP module so the new ini is picked up. Non-fatal if it
    // fails — the override is already persisted for the next startup.
    try
    {
        var modules = sp.GetServices<IServiceModule>();
        var phpModule = modules.FirstOrDefault(m => m.ServiceId.Equals("php", StringComparison.OrdinalIgnoreCase));
        if (phpModule is not null)
        {
            await phpModule.StopAsync(CancellationToken.None);
            await phpModule.StartAsync(CancellationToken.None);
        }
    }
    catch (Exception ex)
    {
        lf.CreateLogger("PhpExt").LogWarning(ex, "Failed to restart PHP module after extension toggle");
    }

    return Results.Ok(new { version, name, enabled, patchedFiles = patched });

    // --- local helper ---
    static void PatchExtensionLine(string iniPath, string extName, bool shouldBeEnabled)
    {
        var lines = File.ReadAllLines(iniPath).ToList();
        var enableLine = $"extension={extName}";
        var disableLine = $";extension={extName}";
        var pattern = new System.Text.RegularExpressions.Regex(
            @"^\s*;?\s*extension\s*=\s*" + System.Text.RegularExpressions.Regex.Escape(extName) + @"\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        bool found = false;
        for (int i = 0; i < lines.Count; i++)
        {
            if (!pattern.IsMatch(lines[i])) continue;
            lines[i] = shouldBeEnabled ? enableLine : disableLine;
            found = true;
        }
        if (!found)
        {
            // Extension not yet mentioned — add under [ext] section or EOF.
            lines.Add("");
            lines.Add(shouldBeEnabled ? enableLine : disableLine);
        }
        File.WriteAllLines(iniPath, lines);
    }
});

// Version validation (checks if a version string is available for a service)
app.MapPost("/api/services/{id}/validate-version", async (string id, HttpContext ctx) =>
{
    Dictionary<string, string>? body;
    try
    {
        body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>();
    }
    catch (System.Text.Json.JsonException ex)
    {
        return Results.BadRequest(new { error = $"Invalid JSON body: {ex.Message}" });
    }
    var version = body?.GetValueOrDefault("version") ?? "";
    // For PHP: check if the version exists in detected installations.
    // Reflection wraps target exceptions, so catch and unwrap to surface
    // the real cause instead of a 500 stack trace through auth middleware.
    if (id.Contains("php", StringComparison.OrdinalIgnoreCase))
    {
        try
        {
            var phpPlugin = pluginLoader.Plugins.FirstOrDefault(p => p.Instance.Id == "nks.wdc.php");
            if (phpPlugin != null)
            {
                var method = phpPlugin.Instance.GetType().GetMethod("GetInstalledVersions");
                if (method?.Invoke(phpPlugin.Instance, null) is System.Collections.IEnumerable versions)
                {
                    foreach (var v in versions)
                    {
                        var vProp = v.GetType().GetProperty("Version");
                        if (vProp?.GetValue(v)?.ToString()?.StartsWith(version) == true)
                            return Results.Ok(new { valid = true, version = vProp.GetValue(v)?.ToString() });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            return Results.Problem(
                $"Failed to enumerate PHP versions: {ex.InnerException?.Message ?? ex.Message}");
        }
    }
    return Results.Ok(new { valid = !string.IsNullOrEmpty(version), version });
});

// Service logs
app.MapGet("/api/services/{id}/logs", async (string id, IServiceProvider sp, int? lines) =>
{
    var modules = sp.GetServices<IServiceModule>();
    var module = modules.FirstOrDefault(m => m.ServiceId.Equals(id, StringComparison.OrdinalIgnoreCase)
        || m.Type.ToString().Equals(id, StringComparison.OrdinalIgnoreCase));
    if (module == null) return Results.NotFound();
    var logs = await module.GetLogsAsync(lines ?? 100, CancellationToken.None);
    return Results.Ok(logs);
});

// Service config read — returns the main config file content for a service
app.MapGet("/api/services/{id}/config", async (string id, ServiceConfigManager configManager) =>
{
    try
    {
        var files = await configManager.GetFilesAsync(id);
        return Results.Ok(new { serviceId = id, files });
    }
    catch (Exception ex)
    {
        // Graceful fallback: plugins like Cloudflare / SSL / Hosts manage
        // their settings via the REST API, not file-based config, and the
        // ServiceConfigManager throws if asked. Return an empty file list
        // plus an info flag so the frontend can surface a "manage via
        // dedicated page" hint instead of a 500 toast.
        return Results.Ok(new
        {
            serviceId = id,
            files = Array.Empty<object>(),
            info = ex.Message,
            managedExternally = true,
        });
    }
});

app.MapPost("/api/services/{id}/config", async (
    string id,
    HttpContext ctx,
    ServiceConfigManager configManager,
    IServiceProvider sp) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<ServiceConfigWriteRequest>();
    if (body == null || string.IsNullOrWhiteSpace(body.Path))
        return Results.BadRequest(new { error = "path is required" });

    await configManager.SaveAsync(id, body.Path, body.Content ?? string.Empty, ctx.RequestAborted);

    var modules = sp.GetServices<IServiceModule>();
    var module = modules.FirstOrDefault(m => m.ServiceId.Equals(id, StringComparison.OrdinalIgnoreCase)
        || m.Type.ToString().Equals(id, StringComparison.OrdinalIgnoreCase));
    if (module == null)
        return Results.Ok(new { saved = true, applied = false, restarted = false, message = "Configuration saved. No matching service module found to apply changes." });

    var status = await module.GetStatusAsync(ctx.RequestAborted);
    if (status.State != ServiceState.Running)
        return Results.Ok(new { saved = true, applied = false, restarted = false, message = "Configuration saved. Service is stopped, so changes will apply on next start." });

    try
    {
        await module.StopAsync(ctx.RequestAborted);
        await module.StartAsync(ctx.RequestAborted);
        return Results.Ok(new { saved = true, applied = true, restarted = true, message = "Configuration saved and service restarted." });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { saved = true, applied = false, restarted = false, message = $"Configuration saved, but service restart failed: {ex.Message}" });
    }
});

// Config validation — dispatches to Apache / PHP / MySQL / Redis based on serviceId.
// Frontend sends serviceId from the ServiceConfig.vue editor; legacy callers that
// omit serviceId get Apache validation (backwards compat with the original endpoint).
// Broadcasts SSE `validation` events (phase: started|passed|failed) so the UI
// ValidationBadge can update live — originally specified in Phase 2 but never
// wired; filled in during the 2026-04-11 strict audit cycle.
app.MapPost("/api/config/validate", async (HttpContext ctx, ConfigValidator validator, BinaryManager bm, SseService sse, ServiceConfigManager configManager) =>
{
    ConfigValidateRequest? body;
    try
    {
        body = await ctx.Request.ReadFromJsonAsync<ConfigValidateRequest>();
    }
    catch (System.Text.Json.JsonException ex)
    {
        return Results.BadRequest(new { isValid = false, output = $"Invalid JSON body: {ex.Message}" });
    }
    if (body == null) return Results.BadRequest();

    var service = (body.ServiceId ?? "apache").ToLowerInvariant();
    if (!configManager.TryNormalizeManagedPath(service, body.ConfigPath, out var normalizedConfigPath, out var pathError))
        return Results.BadRequest(new { isValid = false, output = pathError });

    // Emit "started" immediately so the UI can show the spinner while
    // the validator binary runs.
    await sse.BroadcastAsync("validation", new
    {
        phase = "started",
        serviceId = service,
        configPath = normalizedConfigPath,
    });

    async Task<IResult> Finish(bool isValid, string output)
    {
        await sse.BroadcastAsync("validation", new
        {
            phase = isValid ? "passed" : "failed",
            serviceId = service,
            configPath = normalizedConfigPath,
            output,
        });
        return Results.Ok(new { isValid, output });
    }

    var apacheRootConfigPath = service is "apache" or "httpd"
        ? (await configManager.GetFilesAsync("apache")).FirstOrDefault(f => f.Name.Equals("httpd.conf", StringComparison.OrdinalIgnoreCase))?.Path
        : null;

    async Task<(bool IsValid, string Output)> ValidateDraftAsync(Func<string, Task<(bool IsValid, string Output)>> run)
    {
        if (service is "apache" or "httpd"
            && !string.IsNullOrEmpty(apacheRootConfigPath)
            && !string.Equals(normalizedConfigPath, apacheRootConfigPath, StringComparison.OrdinalIgnoreCase))
        {
            if (body.Content is null)
                return await run(apacheRootConfigPath);

            var originalContent = await File.ReadAllTextAsync(normalizedConfigPath, ctx.RequestAborted);
            await File.WriteAllTextAsync(normalizedConfigPath, body.Content, ctx.RequestAborted);
            try
            {
                return await run(apacheRootConfigPath);
            }
            finally
            {
                await File.WriteAllTextAsync(normalizedConfigPath, originalContent, ctx.RequestAborted);
            }
        }

        if (body.Content is null)
            return await run(normalizedConfigPath);

        var draftPath = await configManager.WriteDraftAsync(service, normalizedConfigPath, body.Content, ctx.RequestAborted);
        try
        {
            return await run(draftPath);
        }
        finally
        {
            try
            {
                if (File.Exists(draftPath))
                    File.Delete(draftPath);
            }
            catch
            {
                // Best-effort cleanup only — startup tmp sweep catches leftovers.
            }
        }
    }

    // Helper: resolve a managed binary executable for a given app key, never falling
    // back to PATH. Rule: NKS WDC only uses its own binaries under ~/.wdc/binaries/.
    string? ResolveBinary(string appKey, string pluginId, string? pluginProp)
    {
        string? path = null;
        if (pluginProp is not null)
        {
            var plug = pluginLoader.Plugins.FirstOrDefault(p => p.Instance.Id == pluginId);
            var prop = plug?.Instance.GetType().GetProperty(pluginProp);
            if (prop != null) path = prop.GetValue(plug!.Instance) as string;
        }
        if (string.IsNullOrEmpty(path))
            path = bm.ListInstalled(appKey).FirstOrDefault()?.Executable;
        return path;
    }

    switch (service)
    {
        case "apache":
        case "httpd":
        {
            var httpdPath = ResolveBinary("apache", "nks.wdc.apache", "HttpdPath");
            if (string.IsNullOrEmpty(httpdPath) || !File.Exists(httpdPath))
                return await Finish(false, "Apache httpd not found in managed binaries — install it first via POST /api/binaries/install");
            var (isValid, output) = await ValidateDraftAsync(path => validator.ValidateApacheConfig(httpdPath, path));
            return await Finish(isValid, output);
        }
        case "php":
        {
            var phpPath = bm.ListInstalled("php").FirstOrDefault()?.Executable;
            if (string.IsNullOrEmpty(phpPath) || !File.Exists(phpPath))
                return await Finish(false, "PHP not found in managed binaries");
            var (isValid, output) = await ValidateDraftAsync(path => validator.ValidatePhpIni(phpPath, path));
            return await Finish(isValid, output);
        }
        case "mysql":
        case "mariadb":
        {
            var mysqldPath = bm.ListInstalled("mysql").FirstOrDefault()?.Executable;
            if (string.IsNullOrEmpty(mysqldPath) || !File.Exists(mysqldPath))
                return await Finish(false, "mysqld not found in managed binaries");
            var (isValid, output) = await ValidateDraftAsync(path => validator.ValidateMyCnf(mysqldPath, path));
            return await Finish(isValid, output);
        }
        case "redis":
        {
            var redisPath = bm.ListInstalled("redis").FirstOrDefault()?.Executable;
            if (string.IsNullOrEmpty(redisPath) || !File.Exists(redisPath))
                return await Finish(false, "redis-server not found in managed binaries");
            var (isValid, output) = await ValidateDraftAsync(path => validator.ValidateRedisConf(redisPath, path));
            return await Finish(isValid, output);
        }
        default:
            return await Finish(false, $"Unknown serviceId '{body.ServiceId}'. Use apache | php | mysql | redis.");
    }
});

// Uninstall / cleanup — removes the managed state (sites, db, certs, logs,
// optionally binaries and the managed hosts file block). ALWAYS creates a
// safety backup before touching anything. Destructive endpoint: requires a
// confirmation token in the body to prevent accidental triggers.
//
// Modes:
//   dryRun:true   — report what would be removed, no writes
//   purge:true    — also wipe ~/.wdc/binaries and ~/.wdc (default: preserve binaries)
//   hosts:true    — also strip the managed BEGIN/END block from hosts file
app.MapPost("/api/uninstall", async (HttpContext ctx, BackupManager backupManager, SiteOrchestrator orchestrator, SiteManager sm, IServiceProvider sp, ILoggerFactory lf) =>
{
    Dictionary<string, object?>? body;
    try
    {
        body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, object?>>();
    }
    catch (System.Text.Json.JsonException ex)
    {
        return Results.BadRequest(new { error = $"Invalid JSON body: {ex.Message}" });
    }
    bool flag(string key) => body is not null && body.TryGetValue(key, out var v) && v switch
    {
        bool b => b,
        System.Text.Json.JsonElement j when j.ValueKind == System.Text.Json.JsonValueKind.True => true,
        _ => false,
    };
    string? str(string key) => body?.TryGetValue(key, out var v) == true ? v?.ToString() : null;

    var confirm = str("confirm");
    var dryRun = flag("dryRun");
    var purge = flag("purge");
    var removeHostsBlock = flag("hosts");

    if (!dryRun && confirm != "YES-UNINSTALL")
        return Results.BadRequest(new { error = "Destructive operation — pass confirm: \"YES-UNINSTALL\" in the body, or set dryRun: true." });

    var log = lf.CreateLogger("Uninstall");
    var wdcRoot = NKS.WebDevConsole.Core.Services.WdcPaths.Root;
    var plan = new List<string>();
    string? safetyBackupPath = null;

    // 1. Stop all services (best-effort, continue on failure).
    var modules = sp.GetServices<IServiceModule>();
    foreach (var m in modules)
    {
        plan.Add($"stop service {m.ServiceId}");
        if (!dryRun)
        {
            try
            {
                var status = await m.GetStatusAsync(CancellationToken.None);
                if (status.State != ServiceState.Stopped)
                    await m.StopAsync(CancellationToken.None);
            }
            catch (Exception ex) { log.LogWarning(ex, "Stop {Service} during uninstall", m.ServiceId); }
        }
    }

    // 2. Safety backup before ANY deletion.
    plan.Add("create pre-uninstall safety backup");
    if (!dryRun)
    {
        try
        {
            var (path, _, _, _) = backupManager.CreateBackup();
            safetyBackupPath = path;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Pre-uninstall safety backup failed");
            return Results.Problem(title: "Safety backup failed — uninstall aborted", detail: ex.Message, statusCode: 500);
        }
    }

    // 3. Remove managed hosts file block (only if requested — user may have other tooling).
    if (removeHostsBlock)
    {
        plan.Add("strip managed block from hosts file");
        if (!dryRun)
        {
            try
            {
                // Reuse the orchestrator's hosts update path with an empty domain list — that
                // regenerates the managed block as empty (or removes it entirely).
                await orchestrator.UpdateHostsBlockAsync(Array.Empty<string>(), CancellationToken.None);
            }
            catch (Exception ex) { log.LogWarning(ex, "Hosts block cleanup"); }
        }
    }

    // 4. Remove generated vhosts + site TOMLs + DB + logs. Binaries kept unless purge.
    var subpathsToRemove = new List<string>
    {
        Path.Combine(wdcRoot, "sites"),
        Path.Combine(wdcRoot, "generated"),
        Path.Combine(wdcRoot, "data"),
        Path.Combine(wdcRoot, "logs"),
        Path.Combine(wdcRoot, "ssl"),
        Path.Combine(wdcRoot, "caddy"),
        Path.Combine(wdcRoot, "cache"),
    };
    if (purge)
        subpathsToRemove.Add(Path.Combine(wdcRoot, "binaries"));

    foreach (var path in subpathsToRemove)
    {
        if (!Directory.Exists(path)) continue;
        plan.Add($"delete {path}");
        if (!dryRun)
        {
            try { Directory.Delete(path, recursive: true); }
            catch (Exception ex) { log.LogWarning(ex, "Failed to delete {Path}", path); }
        }
    }

    return Results.Ok(new
    {
        dryRun,
        purge,
        removeHostsBlock,
        safetyBackup = safetyBackupPath,
        plan,
        message = dryRun
            ? "Dry run — no changes made. Re-send with confirm: \"YES-UNINSTALL\" to execute."
            : "Uninstall complete. Stop the daemon and delete the wdc-cli binary to fully remove.",
    });
});

// Plugin enable/disable — persists to ~/.wdc/data/plugin-state.json via PluginState.
// Plugins remain loaded either way; disabled plugins are hidden from the UI service
// list and skipped by Start All.
app.MapPost("/api/plugins/{id}/enable", (string id, PluginState pluginState, ILoggerFactory lf) =>
{
    var pluginLogger = lf.CreateLogger("PluginToggle");
    var plugin = pluginLoader.Plugins.FirstOrDefault(p => p.Instance.Id == id);
    if (plugin == null)
    {
        pluginLogger.LogWarning("Enable refused: plugin {PluginId} not loaded", id);
        return Results.NotFound(new { error = $"Plugin '{id}' not loaded" });
    }
    // F91.2: enforce dependency graph on enable. Build the set of currently-
    // enabled plugins (excluding this one, since we're about to toggle it)
    // and refuse the enable if any hard or any-of dep is unsatisfied. Keeps
    // users from putting the system into a state where e.g. PHP is enabled
    // but no web-server plugin is running to host PHP-FPM. The dependency
    // diagnostics surface in the response so the UI can show "can't enable
    // PHP: at least one of [apache, nginx, caddy] must be enabled".
    var enabledIds = new HashSet<string>(
        pluginLoader.Plugins
            .Where(pl => pluginState.IsEnabled(pl.Instance.Id))
            .Select(pl => pl.Instance.Id),
        StringComparer.OrdinalIgnoreCase);
    var missing = PluginLoaderInternals.ValidateDependencies(
        plugin.Manifest?.Dependencies, enabledIds);
    if (missing.Count > 0)
    {
        pluginLogger.LogWarning("Enable refused for {PluginId}: missing deps [{Missing}]", id, string.Join(",", missing));
        return Results.BadRequest(new
        {
            error = "dependencies-not-satisfied",
            id,
            missingDependencies = missing,
        });
    }
    pluginState.SetEnabled(id, true);
    pluginLogger.LogInformation("Plugin {PluginId} enabled", id);
    return Results.Ok(new { id, enabled = true });
});

app.MapPost("/api/plugins/{id}/disable", (string id, PluginState pluginState, ILoggerFactory lf) =>
{
    var pluginLogger = lf.CreateLogger("PluginToggle");
    var plugin = pluginLoader.Plugins.FirstOrDefault(p => p.Instance.Id == id);
    if (plugin == null)
    {
        pluginLogger.LogWarning("Disable refused: plugin {PluginId} not loaded", id);
        return Results.NotFound(new { error = $"Plugin '{id}' not loaded" });
    }
    // F91.2: refuse disable when another enabled plugin would lose a
    // satisfied dependency. E.g. Composer hard-depends on PHP — disabling
    // PHP while Composer is on would orphan Composer. List the dependents
    // so the UI can guide the user to disable them first.
    var dependents = new List<string>();
    foreach (var other in pluginLoader.Plugins)
    {
        if (other.Instance.Id == id) continue;
        if (!pluginState.IsEnabled(other.Instance.Id)) continue;
        var deps = other.Manifest?.Dependencies;
        if (deps is null) continue;
        var hardHit = deps.Hard?.Any(h => string.Equals(h, id, StringComparison.OrdinalIgnoreCase)) == true;
        var anyOfHit = deps.AnyOf?.Any(group =>
            group?.Any(g => string.Equals(g, id, StringComparison.OrdinalIgnoreCase)) == true
            // AnyOf only breaks when NO other candidate from the group is enabled.
            && !group.Any(g =>
                !string.Equals(g, id, StringComparison.OrdinalIgnoreCase)
                && pluginState.IsEnabled(g))) == true;
        if (hardHit || anyOfHit) dependents.Add(other.Instance.Id);
    }
    if (dependents.Count > 0)
    {
        pluginLogger.LogWarning("Disable refused for {PluginId}: active dependents [{Dependents}]", id, string.Join(",", dependents));
        return Results.BadRequest(new
        {
            error = "has-dependents",
            id,
            dependents,
        });
    }
    pluginState.SetEnabled(id, false);
    pluginLogger.LogInformation("Plugin {PluginId} disabled", id);
    return Results.Ok(new { id, enabled = false });
});

// SSL certificates
app.MapGet("/api/ssl/certs", (SiteManager sm) =>
{
    var sslPlugin = pluginLoader.Plugins.FirstOrDefault(p => p.Instance.Id == "nks.wdc.ssl");
    if (sslPlugin == null) return Results.Ok(new { certs = Array.Empty<object>(), mkcertInstalled = false });
    var method = sslPlugin.Instance.GetType().GetMethod("GetCerts");
    if (method == null)
        return Results.Ok(new { certs = Array.Empty<object>(), mkcertInstalled = false });

    var rawResult = method.Invoke(sslPlugin.Instance, null);
    var certValues = new List<object>();
    if (rawResult is System.Collections.IEnumerable enumerable)
    {
        foreach (var item in enumerable)
        {
            var kvp = item.GetType();
            var valProp = kvp.GetProperty("Value");
            certValues.Add(valProp != null ? valProp.GetValue(item)! : item);
        }
    }

    // F81: enrich each CertInfo with live X.509 metadata (NotAfterUtc,
    // Issuer, Fingerprint) parsed from disk + orphan flag (site with the
    // cert's domain no longer exists) + expiring flag (<=14 days to
    // expiry). We build a serialization-friendly dict so we don't need
    // a shared DTO type across the plugin ALC boundary.
    var knownDomains = sm.Sites.Values
        .SelectMany(s => new[] { s.Domain }.Concat(s.Aliases ?? []))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    var enriched = new List<object>();
    foreach (var cert in certValues)
    {
        var t = cert.GetType();
        string domain = t.GetProperty("Domain")?.GetValue(cert) as string ?? "";
        string certPath = t.GetProperty("CertPath")?.GetValue(cert) as string ?? "";
        string keyPath = t.GetProperty("KeyPath")?.GetValue(cert) as string ?? "";
        DateTime createdUtc = (DateTime)(t.GetProperty("CreatedUtc")?.GetValue(cert) ?? DateTime.UtcNow);
        var aliases = (t.GetProperty("Aliases")?.GetValue(cert) as string[]) ?? Array.Empty<string>();

        DateTime? notAfterUtc = t.GetProperty("NotAfterUtc")?.GetValue(cert) as DateTime?;
        string? issuer = t.GetProperty("Issuer")?.GetValue(cert) as string;
        string? fingerprint = t.GetProperty("Fingerprint")?.GetValue(cert) as string;

        if (notAfterUtc is null && File.Exists(certPath))
        {
            try
            {
                // X509Certificate2 is IDisposable — the loader returns one
                // with a CAPI/ncrypt handle attached. Without `using` the
                // handle was kept alive until GC, which on Windows retains
                // a kernel certificate context per SSL list call.
                using var x509 = System.Security.Cryptography.X509Certificates.X509CertificateLoader
                    .LoadCertificateFromFile(certPath);
                notAfterUtc = x509.NotAfter.ToUniversalTime();
                issuer = x509.Issuer;
                fingerprint = x509.Thumbprint;
            }
            catch { /* cert unreadable — surface without metadata */ }
        }

        int? daysToExpiry = notAfterUtc.HasValue
            ? (int)Math.Floor((notAfterUtc.Value - DateTime.UtcNow).TotalDays)
            : (int?)null;
        bool expiring = daysToExpiry.HasValue && daysToExpiry.Value <= 14;
        bool expired = daysToExpiry.HasValue && daysToExpiry.Value < 0;
        bool orphan = !knownDomains.Contains(domain);

        enriched.Add(new
        {
            domain,
            certPath,
            keyPath,
            createdUtc,
            aliases,
            notAfterUtc,
            issuer,
            fingerprint,
            daysToExpiry,
            expiring,
            expired,
            orphan,
        });
    }
    return Results.Ok(new { certs = enriched, mkcertInstalled = true });
});

app.MapPost("/api/ssl/install-ca", async () =>
{
    var sslPlugin = pluginLoader.Plugins.FirstOrDefault(p => p.Instance.Id == "nks.wdc.ssl");
    if (sslPlugin == null) return Results.BadRequest(new { ok = false, message = "SSL plugin not loaded" });
    var method = sslPlugin.Instance.GetType().GetMethod("InstallCA");
    if (method == null) return Results.BadRequest(new { ok = false, message = "InstallCA method not found" });
    try
    {
        if (method.Invoke(sslPlugin.Instance, null) is not Task<bool> task)
            return Results.BadRequest(new { ok = false, message = "InstallCA returned unexpected type" });
        var success = await task;
        return success
            ? Results.Ok(new { ok = true, message = "CA installed" })
            : Results.BadRequest(new { ok = false, message = "Failed to install CA" });
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "InstallCA reflection failed");
        return Results.BadRequest(new { ok = false, message = $"InstallCA failed: {ex.Message}" });
    }
});

app.MapPost("/api/ssl/generate", async (HttpContext ctx) =>
{
    Dictionary<string, object>? body;
    try
    {
        body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, object>>();
    }
    catch (System.Text.Json.JsonException ex)
    {
        return Results.BadRequest(new { ok = false, message = $"Invalid JSON body: {ex.Message}" });
    }
    if (body == null || !body.ContainsKey("domain"))
        return Results.BadRequest(new { ok = false, message = "domain required" });

    var domain = body["domain"]?.ToString() ?? "";
    var aliases = Array.Empty<string>();
    if (body.TryGetValue("aliases", out var aliasesObj) && aliasesObj is JsonElement aliasArr && aliasArr.ValueKind == JsonValueKind.Array)
        aliases = aliasArr.EnumerateArray().Select(a => a.GetString() ?? "").Where(s => s.Length > 0).ToArray();

    var sslPlugin = pluginLoader.Plugins.FirstOrDefault(p => p.Instance.Id == "nks.wdc.ssl");
    if (sslPlugin == null) return Results.BadRequest(new { ok = false, message = "SSL plugin not loaded. Install mkcert first." });

    var method = sslPlugin.Instance.GetType().GetMethod("GenerateCert");
    if (method == null) return Results.BadRequest(new { ok = false, message = "GenerateCert method not found" });

    try
    {
        var task = (Task)method.Invoke(sslPlugin.Instance, new object[] { domain, aliases })!;
        await task;
        var resultProp = task.GetType().GetProperty("Result");
        var result = resultProp?.GetValue(task);

        if (result == null)
            return Results.BadRequest(new { ok = false, message = "mkcert not installed or failed" });

        return Results.Ok(new { ok = true, domain, message = $"Certificate generated for {domain}" });
    }
    catch (Exception ex)
    {
        // mkcert exec failure, missing binary, perms — surface the inner
        // cause from reflection's TargetInvocationException wrapper.
        return Results.Problem(
            $"Certificate generation failed: {ex.InnerException?.Message ?? ex.Message}");
    }
});

app.MapDelete("/api/ssl/certs/{domain}", (string domain) =>
{
    var sslPlugin = pluginLoader.Plugins.FirstOrDefault(p => p.Instance.Id == "nks.wdc.ssl");
    if (sslPlugin == null) return Results.BadRequest(new { ok = false, message = "SSL plugin not loaded" });
    var method = sslPlugin.Instance.GetType().GetMethod("RevokeCert");
    if (method == null) return Results.BadRequest(new { ok = false, message = "RevokeCert not found" });
    try
    {
        var success = (bool)method.Invoke(sslPlugin.Instance, new object[] { domain })!;
        return success
            ? Results.Ok(new { ok = true, message = $"Certificate for {domain} revoked" })
            : Results.NotFound(new { ok = false, message = $"No certificate found for {domain}" });
    }
    catch (Exception ex)
    {
        return Results.Problem(
            $"Certificate revoke failed: {ex.InnerException?.Message ?? ex.Message}");
    }
});

// DNS flush
app.MapPost("/api/dns/flush", async () =>
{
    try
    {
        if (OperatingSystem.IsWindows())
        {
            var result = await CliWrap.Cli.Wrap("ipconfig")
                .WithArguments("/flushdns")
                .WithValidation(CliWrap.CommandResultValidation.None)
                .ExecuteBufferedAsync();
            return Results.Ok(new { ok = result.ExitCode == 0, output = result.StandardOutput.Trim() });
        }
        return Results.Ok(new { ok = false, output = "Not supported on this platform" });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { ok = false, output = ex.Message });
    }
});

// Task 36: catalog sync status — exposes persisted lastSyncAt / ok /
// error so Settings > About can display "Last synced 2 min ago" instead
// of "nesynchronizováno". Values populated by PluginCatalogSyncService
// after every refresh tick; future catalogs (binary, config) slot into
// the same shape.
app.MapGet("/api/catalogs/status", (SettingsStore settings) =>
{
    object Status(string prefix) => new
    {
        lastSyncAt = settings.GetString("catalog", $"{prefix}.lastSyncAt"),
        ok = settings.GetString("catalog", $"{prefix}.lastSyncOk") != "false",
        error = settings.GetString("catalog", $"{prefix}.lastError") ?? "",
    };
    return Results.Ok(new
    {
        plugin = Status("plugin"),
        binary = Status("binary"),
        config = Status("config"),
    });
});

// Cloud config sync proxy — forwards to catalog-api with Bearer injection.
// The daemon auth middleware (above) already validated the daemon token that
// the frontend passed in Authorization. The catalog JWT travels in the
// X-Catalog-Token header so the two tokens don't collide.
app.MapPost("/api/sync/pull", async (HttpContext ctx, IHttpClientFactory httpFactory, SettingsStore settings, ILogger<Program> logger) =>
{
    Dictionary<string, object?>? body;
    try { body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, object?>>(); }
    catch { body = null; }

    var deviceId = body?
        .FirstOrDefault(kv => kv.Key.Equals("device_id", StringComparison.OrdinalIgnoreCase)).Value?.ToString()
        ?? ctx.Request.Query["device_id"].FirstOrDefault();

    if (string.IsNullOrWhiteSpace(deviceId))
        return Results.BadRequest(new { error = "device_id required" });

    var catalogToken = ctx.Request.Headers["X-Catalog-Token"].FirstOrDefault();
    var baseUrl = settings.CatalogUrl.TrimEnd('/');

    using var client = httpFactory.CreateClient("sync-proxy");
    using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v1/sync/config/{Uri.EscapeDataString(deviceId)}");
    if (!string.IsNullOrEmpty(catalogToken))
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {catalogToken}");

    try
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        using var resp = await client.SendAsync(req, cts.Token);

        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            return Results.NotFound(new { error = "no snapshot for device" });

        var json = await resp.Content.ReadAsStringAsync(cts.Token);
        return Results.Content(json, "application/json", System.Text.Encoding.UTF8, (int)resp.StatusCode);
    }
    catch (OperationCanceledException) when (!ctx.RequestAborted.IsCancellationRequested)
    {
        logger.LogWarning("sync/pull timed out for device {DeviceId} on {Url}", deviceId, baseUrl);
        return Results.Json(new { error = "catalog-api timeout" }, statusCode: 504);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "sync/pull proxy error for device {DeviceId}", deviceId);
        return Results.Json(new { error = ex.Message }, statusCode: 502);
    }
});

app.MapPost("/api/sync/push", async (HttpContext ctx, IHttpClientFactory httpFactory, SettingsStore settings, ILogger<Program> logger) =>
{
    var catalogToken = ctx.Request.Headers["X-Catalog-Token"].FirstOrDefault();
    var baseUrl = settings.CatalogUrl.TrimEnd('/');

    string rawBody;
    try
    {
        using var reader = new System.IO.StreamReader(ctx.Request.Body);
        rawBody = await reader.ReadToEndAsync(ctx.RequestAborted);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = $"Failed to read body: {ex.Message}" });
    }

    using var client = httpFactory.CreateClient("sync-proxy");
    using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/v1/sync/config");
    req.Content = new StringContent(rawBody, System.Text.Encoding.UTF8, "application/json");
    if (!string.IsNullOrEmpty(catalogToken))
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {catalogToken}");

    try
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        using var resp = await client.SendAsync(req, cts.Token);
        var json = await resp.Content.ReadAsStringAsync(cts.Token);
        return Results.Content(json, "application/json", System.Text.Encoding.UTF8, (int)resp.StatusCode);
    }
    catch (OperationCanceledException) when (!ctx.RequestAborted.IsCancellationRequested)
    {
        logger.LogWarning("sync/push timed out on {Url}", baseUrl);
        return Results.Json(new { error = "catalog-api timeout" }, statusCode: 504);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "sync/push proxy error");
        return Results.Json(new { error = ex.Message }, statusCode: 502);
    }
});

app.MapGet("/api/sync/exists", async (HttpContext ctx, IHttpClientFactory httpFactory, SettingsStore settings, ILogger<Program> logger) =>
{
    var deviceId = ctx.Request.Query["device_id"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(deviceId))
        return Results.BadRequest(new { error = "device_id required" });

    var catalogToken = ctx.Request.Headers["X-Catalog-Token"].FirstOrDefault();
    var baseUrl = settings.CatalogUrl.TrimEnd('/');

    using var client = httpFactory.CreateClient("sync-proxy");
    using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v1/sync/config/{Uri.EscapeDataString(deviceId)}/exists");
    if (!string.IsNullOrEmpty(catalogToken))
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {catalogToken}");

    try
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        using var resp = await client.SendAsync(req, cts.Token);
        var json = await resp.Content.ReadAsStringAsync(cts.Token);
        return Results.Content(json, "application/json", System.Text.Encoding.UTF8, (int)resp.StatusCode);
    }
    catch (OperationCanceledException) when (!ctx.RequestAborted.IsCancellationRequested)
    {
        logger.LogWarning("sync/exists timed out for device {DeviceId} on {Url}", deviceId, baseUrl);
        return Results.Json(new { error = "catalog-api timeout" }, statusCode: 504);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "sync/exists proxy error for device {DeviceId}", deviceId);
        return Results.Json(new { error = ex.Message }, statusCode: 502);
    }
});

// Settings CRUD
app.MapGet("/api/settings", async (Database db) =>
{
    using var conn = db.CreateConnection();
    var settings = await conn.QueryAsync<dynamic>("SELECT category, key, value FROM settings");
    var dict = new Dictionary<string, string>();
    foreach (var s in settings) dict[$"{s.category}.{s.key}"] = s.value;
    return Results.Ok(dict);
});

app.MapPut("/api/settings", async (HttpContext ctx, Database db) =>
{
    // Frontend sends mixed-type JSON — booleans for toggles, numbers for
    // timeouts, strings for URLs. Hard-typing to Dictionary<string,string>
    // rejected any non-string value with a 500 (JsonException: "could not
    // be converted to string"). Read as JsonElement and stringify here so
    // the storage layer stays TEXT-only (simple schema) but the API is
    // permissive.
    Dictionary<string, JsonElement>? raw;
    try
    {
        raw = await ctx.Request.ReadFromJsonAsync<Dictionary<string, JsonElement>>();
    }
    catch (JsonException ex)
    {
        return Results.BadRequest(new { error = $"Invalid JSON body: {ex.Message}" });
    }
    if (raw == null) return Results.BadRequest();

    var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var (k, v) in raw)
    {
        settings[k] = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "",
            JsonValueKind.Number => v.GetRawText(),
            // Objects/arrays → store as JSON string. The read side can parse
            // it back. This keeps richer types usable without a schema change.
            _ => v.GetRawText(),
        };
    }

    using var conn = db.CreateConnection();
    conn.Open();
    // Wrap the batch in a transaction so a sync-pull that touches many
    // keys is atomic: either all rows land or none do. Without this, a
    // partial failure (disk full, connection reset) could leave the
    // settings table with a half-applied snapshot where some keys are
    // from the new device and others are stale.
    using var tx = conn.BeginTransaction();
    try
    {
        foreach (var (compositeKey, value) in settings)
        {
            var parts = compositeKey.Split('.', 2);
            var category = parts.Length > 1 ? parts[0] : "general";
            var key = parts.Length > 1 ? parts[1] : parts[0];
            await conn.ExecuteAsync(
                "INSERT INTO settings (category, key, value) VALUES (@Category, @Key, @Value) ON CONFLICT(category, key) DO UPDATE SET value = @Value, updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')",
                new { Category = category, Key = key, Value = value },
                transaction: tx);
        }
        tx.Commit();
    }
    catch
    {
        tx.Rollback();
        throw;
    }

    // Propagate port changes to live plugin config — without this, edits
    // to ports.http / ports.https / ports.redis / ports.mailpit* sat in
    // SQLite forever and never reached the service module. We look for
    // any port key in the batch and nudge the matching service to reload
    // its config (regenerate httpd.conf etc). Reflection is used because
    // IServiceModule lives in Core but each plugin's module type is
    // cross-ALC — we only need the ReloadAsync method to be callable.
    var changedCategories = settings.Keys
        .Select(k => k.Split('.', 2)[0])
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    if (changedCategories.Contains("ports"))
    {
        var sp = ctx.RequestServices;
        var modules = sp.GetServices<IServiceModule>().ToArray();
        var portKeys = settings.Keys.Where(k => k.StartsWith("ports.", StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var module in modules)
        {
            // Heuristic: match `ports.<serviceId>` or any port key that
            // looks like it could belong to this module (http/https map
            // to apache/caddy/nginx; mysql/redis/mariadb keys map 1:1).
            var moduleId = module.ServiceId.ToLowerInvariant();
            var relevant = portKeys.Any(k =>
            {
                var key = k.Substring("ports.".Length).ToLowerInvariant();
                if (key == moduleId) return true;
                if (key.StartsWith(moduleId)) return true;
                // http/https touch any webserver module
                if ((key == "http" || key == "https") &&
                    (moduleId == "apache" || moduleId == "caddy" || moduleId == "nginx"))
                    return true;
                return false;
            });
            if (!relevant) continue;

            // Poke the module's private config object BEFORE reload so
            // the regenerated config file actually reflects the new port.
            // The plugin's SDK version doesn't expose a hydrated
            // IWdcSettings-aware constructor (would need a new SDK
            // release), so we reach into the `_config` field via
            // reflection and set HttpPort/HttpsPort directly when the
            // property names look right. Private-field access across the
            // plugin ALC is allowed because AssemblyLoadContext shares
            // the same CLR and reflection isn't blocked by default.
            try
            {
                var configField = module.GetType().GetField("_config",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var config = configField?.GetValue(module);
                if (config is not null)
                {
                    foreach (var pk in portKeys)
                    {
                        var key = pk.Substring("ports.".Length).ToLowerInvariant();
                        // Map common Settings keys to plugin-config property
                        // names. Extend this table when new plugins get
                        // their own port keys (mailpit/redis/mysql all
                        // use different SettingsKey → PropertyName names).
                        var propName = key switch
                        {
                            "http"  when moduleId == "apache" || moduleId == "caddy" || moduleId == "nginx" => "HttpPort",
                            "https" when moduleId == "apache" || moduleId == "caddy" || moduleId == "nginx" => "HttpsPort",
                            "mysql"       when moduleId == "mysql"        => "Port",
                            "mariadb"     when moduleId == "mariadb"      => "Port",
                            "redis"       when moduleId == "redis"        => "Port",
                            "mailpitsmtp" when moduleId == "mailpit"      => "SmtpPort",
                            "mailpithttp" when moduleId == "mailpit"      => "HttpPort",
                            _ => null,
                        };
                        if (propName is null) continue;
                        var prop = config.GetType().GetProperty(propName);
                        if (prop?.CanWrite == true && int.TryParse(settings[pk], out var port) && port > 0)
                        {
                            prop.SetValue(config, port);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                var logger = sp.GetRequiredService<ILogger<Program>>();
                logger.LogWarning(ex, "Port-change config hydration for {Service} failed (reflection)", module.ServiceId);
            }

            // Phase 6.20 — when the changed key is ports.http or ports.https
            // AND the module is a webserver (apache/caddy/nginx), per-site
            // vhost configs in {VhostsDirectory}/*.conf bake the port number
            // into `<VirtualHost *:PORT>` at write-time. A pure ReloadAsync
            // re-runs httpd.conf but leaves stale per-site files referencing
            // the OLD port, so Apache binds the new Listen but no vhost
            // matches → all requests fall to the default htdocs page.
            //
            // Fix: for webserver modules with HTTP/HTTPS port changes, iterate
            // every registered site and call the module's GenerateVhostAsync
            // BEFORE reload. Reflection-based for the same cross-ALC reasons
            // as the config-hydration block above.
            var webServerModule = moduleId is "apache" or "caddy" or "nginx";
            var httpPortChanged = portKeys.Any(k =>
            {
                var key = k.Substring("ports.".Length).ToLowerInvariant();
                return key == "http" || key == "https";
            });
            if (webServerModule && httpPortChanged)
            {
                try
                {
                    var generateVhost = module.GetType().GetMethod("GenerateVhostAsync");
                    if (generateVhost is not null)
                    {
                        var siteRegistry = sp.GetService<NKS.WebDevConsole.Core.Interfaces.ISiteRegistry>();
                        if (siteRegistry is not null)
                        {
                            int regenerated = 0;
                            foreach (var (_, siteCfg) in siteRegistry.Sites)
                            {
                                try
                                {
                                    // Signature is GenerateVhostAsync(SiteConfig site, CancellationToken ct = default).
                                    // Pass the optional CT explicitly so reflection doesn't trip on default-arg metadata.
                                    var task = generateVhost.Invoke(module,
                                        new object[] { siteCfg, ctx.RequestAborted }) as Task;
                                    if (task is not null) await task;
                                    regenerated++;
                                }
                                catch (Exception perSiteEx)
                                {
                                    // Per-site failure (template missing for one site, e.g.) must not abort
                                    // the whole regenerate sweep. Other sites still get fresh vhosts.
                                    var logger = sp.GetRequiredService<ILogger<Program>>();
                                    logger.LogWarning(perSiteEx,
                                        "Port-change vhost regen for {Domain} failed (continuing with rest)",
                                        siteCfg.Domain);
                                }
                            }
                            var rlog = sp.GetRequiredService<ILogger<Program>>();
                            rlog.LogInformation(
                                "Port-change vhost bulk-regen completed: {Count} site(s) refreshed for {Service}",
                                regenerated, module.ServiceId);
                        }
                    }
                }
                catch (Exception ex)
                {
                    var logger = sp.GetRequiredService<ILogger<Program>>();
                    logger.LogWarning(ex,
                        "Port-change bulk vhost regen for {Service} failed", module.ServiceId);
                }
            }

            var reload = module.GetType().GetMethod("ReloadAsync", new[] { typeof(CancellationToken) });
            if (reload is null) continue;
            try
            {
                var result = reload.Invoke(module, new object[] { ctx.RequestAborted });
                if (result is Task t) await t;
            }
            catch (Exception ex)
            {
                // A plugin's reload fault must not fail the PUT — the
                // settings row is already persisted, the next daemon
                // restart will pick it up regardless.
                var logger = sp.GetRequiredService<ILogger<Program>>();
                logger.LogWarning(ex, "Port-change reload for {Service} failed", module.ServiceId);
            }
        }
    }

    return Results.Ok(settings);
});

// Recent activity — Phase 4 Dashboard timeline. Returns the most recent rows
// from the config_history table (populated by SiteManager / ConfigValidator /
// plugin lifecycle events) so the Dashboard can render an el-timeline of
// what has happened recently: "created myapp.loc", "edited php.ini", etc.
//
// Returns a JSON array newest-first, shape:
//   [{ id, entityType, entityName, operation, changedFields, source, createdAt }]
//
// Limit is clamped to [1, 200] so the timeline query stays bounded even if
// a misbehaving client passes ?limit=999999.
app.MapGet("/api/activity", async (Database db, int? limit) =>
{
    var clamped = Math.Clamp(limit ?? 20, 1, 200);
    try
    {
        using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync(
            @"SELECT id, entity_type AS entityType, entity_name AS entityName,
                     operation, changed_fields AS changedFields, source,
                     created_at AS createdAt
              FROM config_history
              ORDER BY id DESC
              LIMIT @Limit",
            new { Limit = clamped });
        return Results.Ok(rows);
    }
    catch (Exception ex)
    {
        // Table may not exist on very first boot before migrations complete.
        // Return empty list rather than 500 so the Dashboard renders cleanly.
        return Results.Ok(new { error = ex.Message, entries = Array.Empty<object>() });
    }
});

// Database identifier validation.
// MySQL identifier rules allow unquoted names matching [a-zA-Z0-9_$] (and $ in some versions).
// We use a strict subset to avoid shell escape, path traversal, and SQL injection when the
// name is inlined into CLI args or (ab)used in SQL strings.
static bool IsValidDatabaseName(string? name)
{
    if (string.IsNullOrEmpty(name)) return false;
    if (name.Length > 64) return false; // MySQL hard limit
    return System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-zA-Z0-9_]+$");
}

// Build the base argv list for invoking mysql/mysqldump CLI. Reads the
// daemon-managed root password from MySqlRootPassword (DPAPI on Windows,
// 0600 plaintext on Unix). Returns a List<string> so callers use the
// IEnumerable<string> overload of CliWrap.WithArguments() and avoid
// shell-string parsing ambiguity entirely.
//
// Password is INTENTIONALLY NOT placed on the command line — `-p<pass>`
// would leak the password to `ps aux` / Task Manager process listings.
// Instead, callers pair this with MysqlEnvVars() which sets MYSQL_PWD,
// the env var mysql.exe respects to bypass the interactive prompt
// without exposing the password to other processes on the same host.
//
// BUG context: Before this helper, every mysql endpoint hard-coded
// `-h 127.0.0.1 -P 3306 -u root` with NO password, which broke as soon
// as the daemon's MySQL plugin set a root password via MySqlRootPassword
// .EnsureExists() during initial mysqld --initialize-insecure flow. After
// that point all GUI database operations would fail with "access denied
// for user root@localhost" and the user had no way to recover via UI.
static List<string> MysqlBaseArgs(int port = 3306)
{
    return new List<string>
    {
        "-h", "127.0.0.1",
        "-P", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "-u", "root",
    };
}

// Environment dictionary to pair with MysqlBaseArgs(). MYSQL_PWD is read
// by mysql.exe / mysqldump.exe as the root password, bypassing the
// interactive prompt. MySQL docs warn this is "extremely insecure" for
// shared / multi-user systems because env vars CAN be inspected via
// /proc/{pid}/environ, but for a single-user dev workstation it's
// strictly better than -p<pass> on the command line which is visible
// in ANY process listing.
static IReadOnlyDictionary<string, string?> MysqlEnvVars()
{
    var password = NKS.WebDevConsole.Core.Services.MySqlRootPassword.TryRead();
    return string.IsNullOrEmpty(password)
        ? new Dictionary<string, string?>()
        : new Dictionary<string, string?> { ["MYSQL_PWD"] = password };
}

// Databases — list MySQL databases via mysql CLI
app.MapGet("/api/databases", async (BinaryManager bm, SettingsStore settings, IServiceProvider sp) =>
{
    var mysql = bm.ListInstalled("mysql").FirstOrDefault();
    if (mysql?.Executable is null)
        return Results.Ok(new { error = "MySQL not installed", databases = Array.Empty<string>() });

    // mysqld lives next to the `mysql` client; name differs by OS.
    var cliName = OperatingSystem.IsWindows() ? "mysql.exe" : "mysql";
    var mysqlCli = Path.Combine(Path.GetDirectoryName(mysql.Executable)!, cliName);
    if (!File.Exists(mysqlCli))
        return Results.Ok(new { error = $"{cliName} not found", databases = Array.Empty<string>() });

    try
    {
        var listArgs = MysqlBaseArgs(ResolveMysqlPortWithFallback(settings, sp, pluginLoader, bm));
        listArgs.Add("-N");
        listArgs.Add("-e");
        listArgs.Add("SHOW DATABASES");
        var result = await CliWrap.Cli.Wrap(mysqlCli)
            .WithArguments(listArgs)
            .WithEnvironmentVariables(MysqlEnvVars())
            .WithValidation(CliWrap.CommandResultValidation.None)
            .ExecuteBufferedAsync();

        if (result.ExitCode != 0)
        {
            var stderr = result.StandardError.Trim();
            var port = ResolveMysqlPortWithFallback(settings, sp, pluginLoader, bm);
            var hint = "";
            int? suggestedPort = null;
            if (stderr.Contains("1045") || stderr.Contains("Access denied"))
            {
                hint = $"Port {port} has a mysqld process but WDC root password was rejected. Likely external MySQL (MAMP/XAMPP/Windows service) occupies this port.";
                // Pick the first free TCP port above the current one — the
                // frontend renders this as a one-click "Use port N" button
                // that POSTs /api/databases/use-alt-port.
                suggestedPort = FindFreeTcpPort(port + 1);
            }
            return Results.Ok(new { error = stderr, hint, attemptedPort = port, suggestedPort, databases = Array.Empty<string>() });
        }

        var dbs = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => d.Trim())
            .Where(d => d != "information_schema" && d != "performance_schema" && d != "sys" && d != "mysql")
            .ToList();

        return Results.Ok(new { databases = dbs });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { error = ex.Message, databases = Array.Empty<string>() });
    }
});

/// <summary>
/// Finds the first free TCP port ≥ <paramref name="startPort"/> by binding
/// a TcpListener on each candidate until one succeeds. Caps the scan at
/// 64 attempts so a fully-occupied port range doesn't hang the request.
/// </summary>
static int? FindFreeTcpPort(int startPort)
{
    for (int p = Math.Max(1024, startPort); p < startPort + 64 && p <= 65535; p++)
    {
        try
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, p);
            listener.Start();
            listener.Stop();
            return p;
        }
        catch { /* bound — try next */ }
    }
    return null;
}

// MySQL root password management. GET reports whether a password is stored
// (without ever returning it). POST accepts a new password + persists into
// the DPAPI store. The caller is responsible for having run ALTER USER on
// mysqld itself — this endpoint only syncs WDC's stored copy.
app.MapGet("/api/databases/root-password", () =>
    Results.Ok(new { exists = NKS.WebDevConsole.Core.Services.MySqlRootPassword.Exists() }));

app.MapPost("/api/databases/root-password", async (HttpContext ctx) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>();
    var password = body?.GetValueOrDefault("password") ?? "";
    if (string.IsNullOrEmpty(password))
        return Results.BadRequest(new { error = "password is required" });
    try
    {
        NKS.WebDevConsole.Core.Services.MySqlRootPassword.SetPlaintext(password);
        return Results.Ok(new { stored = true });
    }
    catch (Exception ex)
    {
        return Results.Problem(title: "failed to persist password", detail: ex.Message, statusCode: 500);
    }
});

// POST /api/plugins/mysql/change-password
// Verifies currentPwd matches the stored root password, then executes ALTER USER
// for all root@* accounts, persists the new password, and verifies connectivity.
app.MapPost("/api/plugins/mysql/change-password", async (
    HttpContext ctx,
    BinaryManager bm,
    SettingsStore settings,
    IServiceProvider sp,
    ILoggerFactory lf) =>
{
    var log = lf.CreateLogger("MySqlChangePassword");
    var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>(caseInsensitiveJson);
    var currentPwd = body?.GetValueOrDefault("currentPwd") ?? body?.GetValueOrDefault("currentpwd") ?? "";
    var newPwd = body?.GetValueOrDefault("newPwd") ?? body?.GetValueOrDefault("newpwd") ?? "";

    var validationError = MySqlPasswordHelper.ValidatePassword(newPwd);
    if (validationError is not null)
        return Results.BadRequest(new { success = false, error = validationError });

    // Verify currentPwd matches stored password.
    var stored = NKS.WebDevConsole.Core.Services.MySqlRootPassword.TryRead();
    if (stored is null)
        return Results.BadRequest(new { success = false, error = "No stored root password found. Use reset-password instead." });
    if (currentPwd != stored)
        return Results.BadRequest(new { success = false, error = "currentPwd does not match the stored root password." });

    var mysql = bm.ListInstalled("mysql").FirstOrDefault();
    if (mysql?.Executable is null)
        return Results.BadRequest(new { success = false, error = "MySQL not installed" });

    var mysqlCli = MySqlPasswordHelper.ResolveMysqlCli(mysql.Executable);
    if (mysqlCli is null)
        return Results.BadRequest(new { success = false, error = "mysql CLI not found next to mysqld" });

    var port = ResolveMysqlPortWithFallback(settings, sp, pluginLoader, bm);
    var initFile = "";
    try
    {
        log.LogInformation("change-password: writing ALTER USER init-file for port {Port}", port);
        var sql = MySqlPasswordHelper.BuildAlterUserSql(newPwd);
        initFile = MySqlPasswordHelper.WriteTempInitFile(sql);

        var changeArgs = new List<string>
        {
            "-h", "127.0.0.1",
            "-P", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-u", "root",
            "--init-command", $"source {initFile}"
        };

        log.LogInformation("change-password: executing ALTER USER via mysql CLI");
        var changeResult = await CliWrap.Cli.Wrap(mysqlCli)
            .WithArguments(changeArgs)
            .WithEnvironmentVariables(new Dictionary<string, string?> { ["MYSQL_PWD"] = currentPwd })
            .WithValidation(CliWrap.CommandResultValidation.None)
            .ExecuteBufferedAsync();

        if (changeResult.ExitCode != 0)
        {
            log.LogWarning("change-password: ALTER USER failed (exit {Code}): {Err}",
                changeResult.ExitCode, changeResult.StandardError.Trim());
            return Results.BadRequest(new
            {
                success = false,
                error = $"ALTER USER failed: {changeResult.StandardError.Trim()}"
            });
        }

        log.LogInformation("change-password: ALTER USER succeeded, persisting new password");
        NKS.WebDevConsole.Core.Services.MySqlRootPassword.SetPlaintext(newPwd);

        // Verify connectivity with new password.
        log.LogInformation("change-password: verifying new password via SELECT 1");
        var verifyArgs = new List<string>
        {
            "-h", "127.0.0.1",
            "-P", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-u", "root",
            "-N", "-e", "SELECT 1"
        };
        var verifyResult = await CliWrap.Cli.Wrap(mysqlCli)
            .WithArguments(verifyArgs)
            .WithEnvironmentVariables(new Dictionary<string, string?> { ["MYSQL_PWD"] = newPwd })
            .WithValidation(CliWrap.CommandResultValidation.None)
            .ExecuteBufferedAsync();

        var verified = verifyResult.ExitCode == 0;
        log.LogInformation("change-password: verification {Result}", verified ? "OK" : "FAILED");
        return Results.Ok(new { success = true, verified });
    }
    catch (Exception ex)
    {
        log.LogError(ex, "change-password: unexpected error");
        return Results.Problem(title: "change-password failed", detail: ex.Message, statusCode: 500);
    }
    finally
    {
        if (!string.IsNullOrEmpty(initFile))
            try { File.Delete(initFile); } catch { /* best effort */ }
    }
});

// POST /api/plugins/mysql/reset-password
// DANGER: resets root password without knowing the current one.
// Stops mysqld, spawns a skip-grant-tables instance, runs ALTER USER, then
// restarts the normal mysqld.
app.MapPost("/api/plugins/mysql/reset-password", async (
    HttpContext ctx,
    BinaryManager bm,
    SettingsStore settings,
    IServiceProvider sp,
    ILoggerFactory lf) =>
{
    var log = lf.CreateLogger("MySqlResetPassword");
    var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>(caseInsensitiveJson);
    var newPwd = body?.GetValueOrDefault("newPwd") ?? body?.GetValueOrDefault("newpwd") ?? "";

    var validationError = MySqlPasswordHelper.ValidatePassword(newPwd);
    if (validationError is not null)
        return Results.BadRequest(new { success = false, error = validationError });

    var mysql = bm.ListInstalled("mysql").FirstOrDefault();
    if (mysql?.Executable is null)
        return Results.BadRequest(new { success = false, error = "MySQL not installed" });

    var mysqldPath = mysql.Executable;
    var mysqlCli = MySqlPasswordHelper.ResolveMysqlCli(mysqldPath);
    var mysqladmin = MySqlPasswordHelper.ResolveMysqladmin(mysqldPath);
    if (mysqlCli is null)
        return Results.BadRequest(new { success = false, error = "mysql CLI not found next to mysqld" });

    var port = ResolveMysqlPortWithFallback(settings, sp, pluginLoader, bm);
    var steps = new List<string>();
    var initFile = "";
    System.Diagnostics.Process? safeProcess = null;
    var tmpPidFile = Path.Combine(Path.GetTempPath(), $"wdc-mysql-reset-{Guid.NewGuid():N}.pid");
    var tmpSocket = OperatingSystem.IsWindows() ? "" : "/tmp/wdc-mysql-reset.sock";

    // Find the MySQL IServiceModule so we can stop/start the managed service.
    var mysqlModule = sp.GetServices<IServiceModule>()
        .FirstOrDefault(m => m.ServiceId.Equals("mysql", StringComparison.OrdinalIgnoreCase));

    try
    {
        // Step 1: stop the normal mysqld.
        steps.Add("Stopping managed mysqld");
        log.LogInformation("reset-password: stopping managed mysqld");
        if (mysqlModule is not null)
        {
            try { await mysqlModule.StopAsync(CancellationToken.None); }
            catch (Exception ex) { log.LogWarning(ex, "reset-password: StopAsync threw (continuing)"); }
        }
        steps.Add("mysqld stopped");

        // Step 2: spawn skip-grant-tables mysqld on a temporary port (3307 avoids conflict).
        steps.Add("Spawning skip-grant-tables mysqld");
        log.LogInformation("reset-password: spawning skip-grant-tables mysqld");
        var skipPort = 3307;
        var dataDir = Path.Combine(NKS.WebDevConsole.Core.Services.WdcPaths.DataRoot, "mysql");

        var safeArgs = OperatingSystem.IsWindows()
            ? $"--skip-grant-tables --skip-networking=OFF --port={skipPort} " +
              $"--datadir=\"{dataDir}\" --pid-file=\"{tmpPidFile}\" --console"
            : $"--skip-grant-tables --skip-networking=OFF --port={skipPort} " +
              $"--datadir=\"{dataDir}\" --pid-file=\"{tmpPidFile}\" " +
              $"--socket=\"{tmpSocket}\" --console";

        var safePsi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = mysqldPath,
            Arguments = safeArgs,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        safeProcess = System.Diagnostics.Process.Start(safePsi)
            ?? throw new InvalidOperationException("Failed to start skip-grant-tables mysqld");

        steps.Add($"skip-grant-tables mysqld PID={safeProcess.Id}");

        // Step 3: wait for the skip-grant-tables mysqld to accept connections.
        steps.Add($"Waiting for skip-grant-tables mysqld on port {skipPort}");
        log.LogInformation("reset-password: waiting for skip-grant-tables mysqld on port {Port}", skipPort);
        var ready = await MySqlPasswordHelper.WaitForTcpPortAsync(skipPort, TimeSpan.FromSeconds(30), CancellationToken.None);
        if (!ready)
            throw new TimeoutException($"skip-grant-tables mysqld did not bind port {skipPort} within 30s");
        steps.Add("skip-grant-tables mysqld ready");

        // Step 4: execute ALTER USER via init-file.
        steps.Add("Executing ALTER USER");
        log.LogInformation("reset-password: executing ALTER USER via mysql CLI on port {Port}", skipPort);
        var sql = MySqlPasswordHelper.BuildAlterUserSql(newPwd);
        initFile = MySqlPasswordHelper.WriteTempInitFile(sql);

        // With --skip-grant-tables, FLUSH PRIVILEGES at the start re-enables grant checking
        // so ALTER USER works correctly.
        var alterArgs = new List<string>
        {
            "-h", "127.0.0.1",
            "-P", skipPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-u", "root",
            "--connect-timeout=10",
            "-e", $"source {initFile}"
        };
        var alterResult = await CliWrap.Cli.Wrap(mysqlCli)
            .WithArguments(alterArgs)
            .WithEnvironmentVariables(new Dictionary<string, string?>())
            .WithValidation(CliWrap.CommandResultValidation.None)
            .ExecuteBufferedAsync();

        if (alterResult.ExitCode != 0)
            throw new InvalidOperationException($"ALTER USER failed (exit {alterResult.ExitCode}): {alterResult.StandardError.Trim()}");
        steps.Add("ALTER USER executed");

        // Step 5: shut down the skip-grant-tables mysqld.
        steps.Add("Shutting down skip-grant-tables mysqld");
        log.LogInformation("reset-password: shutting down skip-grant-tables mysqld");
        if (mysqladmin is not null && File.Exists(mysqladmin))
        {
            try
            {
                await CliWrap.Cli.Wrap(mysqladmin)
                    .WithArguments(new[] { "-h", "127.0.0.1", "-P", skipPort.ToString(), "-u", "root", "shutdown" })
                    .WithEnvironmentVariables(new Dictionary<string, string?>())
                    .WithValidation(CliWrap.CommandResultValidation.None)
                    .ExecuteAsync();
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "reset-password: mysqladmin shutdown failed, killing process");
            }
        }
        if (safeProcess is not null && !safeProcess.HasExited)
        {
            safeProcess.Kill(entireProcessTree: true);
            await safeProcess.WaitForExitAsync();
        }
        steps.Add("skip-grant-tables mysqld stopped");

        // Step 6: persist new password.
        NKS.WebDevConsole.Core.Services.MySqlRootPassword.SetPlaintext(newPwd);
        steps.Add("Password persisted to DPAPI store");

        // Step 7: start normal mysqld.
        steps.Add("Starting normal mysqld");
        log.LogInformation("reset-password: starting normal mysqld");
        if (mysqlModule is not null)
        {
            try { await mysqlModule.StartAsync(CancellationToken.None); }
            catch (Exception ex)
            {
                log.LogWarning(ex, "reset-password: StartAsync threw");
                steps.Add($"Warning: normal mysqld start error: {ex.Message}");
            }
        }
        steps.Add("Normal mysqld started");

        // Step 8: verify new password.
        steps.Add("Verifying new password");
        log.LogInformation("reset-password: verifying new password on port {Port}", port);
        var verifyArgs = new List<string>
        {
            "-h", "127.0.0.1",
            "-P", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-u", "root",
            "-N", "-e", "SELECT 1"
        };
        var verifyResult = await CliWrap.Cli.Wrap(mysqlCli)
            .WithArguments(verifyArgs)
            .WithEnvironmentVariables(new Dictionary<string, string?> { ["MYSQL_PWD"] = newPwd })
            .WithValidation(CliWrap.CommandResultValidation.None)
            .ExecuteBufferedAsync();

        var verified = verifyResult.ExitCode == 0;
        steps.Add(verified ? "Verification OK" : $"Verification FAILED: {verifyResult.StandardError.Trim()}");
        log.LogInformation("reset-password: verification {Result}", verified ? "OK" : "FAILED");

        return Results.Ok(new { success = true, verified, steps });
    }
    catch (Exception ex)
    {
        log.LogError(ex, "reset-password: failed at step: {LastStep}", steps.LastOrDefault() ?? "unknown");
        steps.Add($"ERROR: {ex.Message}");

        // Always try to restart normal mysqld on failure.
        if (mysqlModule is not null)
        {
            try
            {
                log.LogInformation("reset-password: attempting normal mysqld restart after error");
                await mysqlModule.StartAsync(CancellationToken.None);
                steps.Add("Normal mysqld restarted after error");
            }
            catch (Exception restartEx)
            {
                log.LogWarning(restartEx, "reset-password: restart after error also failed");
                steps.Add($"Restart after error failed: {restartEx.Message}");
            }
        }

        return Results.Problem(
            title: "reset-password failed",
            detail: ex.Message,
            statusCode: 500,
            extensions: new Dictionary<string, object?> { ["steps"] = steps });
    }
    finally
    {
        if (!string.IsNullOrEmpty(initFile))
            try { File.Delete(initFile); } catch { /* best effort */ }
        try { File.Delete(tmpPidFile); } catch { /* best effort */ }
        if (!OperatingSystem.IsWindows() && !string.IsNullOrEmpty(tmpSocket))
            try { File.Delete(tmpSocket); } catch { /* best effort */ }
        if (safeProcess is not null && !safeProcess.HasExited)
            try { safeProcess.Kill(entireProcessTree: true); } catch { /* best effort */ }
        safeProcess?.Dispose();
    }
});

// Auto-heal flow: when /api/databases returns a 1045 + suggestedPort, the
// frontend can POST here to flip ports.mysql to the suggested free port and
// restart the WDC mysqld so the user doesn't have to dig through Settings.
app.MapPost("/api/databases/use-alt-port", async (
    HttpContext ctx,
    SettingsStore settings,
    PluginLoader loader,
    CancellationToken ct) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, int>>();
    var newPort = body?.GetValueOrDefault("port") ?? 0;
    if (newPort < 1024 || newPort > 65535)
        return Results.BadRequest(new { error = "port out of range" });

    settings.Set("ports", "mysql", newPort.ToString(System.Globalization.CultureInfo.InvariantCulture));

    // Kick the MySQL / MariaDB plugin to restart on the new port. Lookup is
    // lenient — some installs only carry one of the two.
    var mysql = loader.Plugins.FirstOrDefault(p => p.Instance.Id == "nks.wdc.mysql" || p.Instance.Id == "nks.wdc.mariadb");
    if (mysql?.Instance is IServiceModule svc)
    {
        try { await svc.StopAsync(ct); } catch { /* already stopped is fine */ }
        try { await svc.StartAsync(ct); } catch { /* surface via next /api/databases probe */ }
    }
    return Results.Ok(new { port = newPort, restarted = mysql is not null });
});

// Create database
app.MapPost("/api/databases", async (HttpContext ctx, BinaryManager bm, SettingsStore settings, IServiceProvider sp) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>();
    var dbName = body?.GetValueOrDefault("name") ?? "";
    if (!IsValidDatabaseName(dbName))
        return Results.BadRequest(new { error = "Invalid database name — allowed chars: letters, digits, underscore (max 64 chars)" });

    var mysql = bm.ListInstalled("mysql").FirstOrDefault();
    if (mysql?.Executable is null)
        return Results.BadRequest(new { error = "MySQL not installed" });

    var mysqlCli = Path.Combine(Path.GetDirectoryName(mysql.Executable)!, OperatingSystem.IsWindows() ? "mysql.exe" : "mysql");
    var args = MysqlBaseArgs(ResolveMysqlPortWithFallback(settings, sp, pluginLoader, bm));
    args.Add("-e");
    args.Add($"CREATE DATABASE IF NOT EXISTS `{dbName}`");
    try
    {
        var result = await CliWrap.Cli.Wrap(mysqlCli)
            .WithArguments(args)
            .WithEnvironmentVariables(MysqlEnvVars())
            .WithValidation(CliWrap.CommandResultValidation.None)
            .ExecuteBufferedAsync();
        return result.ExitCode == 0
            ? Results.Created($"/api/databases/{dbName}", new { name = dbName })
            : Results.BadRequest(new { error = result.StandardError.Trim() });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = $"Failed to invoke mysql: {ex.Message}" });
    }
});

// Drop database
app.MapDelete("/api/databases/{name}", async (string name, BinaryManager bm, SettingsStore settings, IServiceProvider sp) =>
{
    if (!IsValidDatabaseName(name))
        return Results.BadRequest(new { error = "Invalid database name" });

    var mysql = bm.ListInstalled("mysql").FirstOrDefault();
    if (mysql?.Executable is null)
        return Results.BadRequest(new { error = "MySQL not installed" });

    var mysqlCli = Path.Combine(Path.GetDirectoryName(mysql.Executable)!, OperatingSystem.IsWindows() ? "mysql.exe" : "mysql");
    var args = MysqlBaseArgs(ResolveMysqlPortWithFallback(settings, sp, pluginLoader, bm));
    args.Add("-e");
    args.Add($"DROP DATABASE IF EXISTS `{name}`");
    try
    {
        var result = await CliWrap.Cli.Wrap(mysqlCli)
            .WithArguments(args)
            .WithEnvironmentVariables(MysqlEnvVars())
            .WithValidation(CliWrap.CommandResultValidation.None)
            .ExecuteBufferedAsync();
        return result.ExitCode == 0
            ? Results.NoContent()
            : Results.BadRequest(new { error = result.StandardError.Trim() });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = $"Failed to invoke mysql: {ex.Message}" });
    }
});

// Database tables
app.MapGet("/api/databases/{name}/tables", async (string name, BinaryManager bm, SettingsStore settings, IServiceProvider sp) =>
{
    if (!IsValidDatabaseName(name))
        return Results.BadRequest(new { error = "Invalid database name" });
    var mysql = bm.ListInstalled("mysql").FirstOrDefault();
    if (mysql?.Executable is null)
        return Results.BadRequest(new { error = "MySQL not installed" });
    var mysqlCli = Path.Combine(Path.GetDirectoryName(mysql.Executable)!, OperatingSystem.IsWindows() ? "mysql.exe" : "mysql");
    var args = MysqlBaseArgs(ResolveMysqlPortWithFallback(settings, sp, pluginLoader, bm));
    args.Add("-N");
    args.Add("-e");
    args.Add($"SELECT TABLE_NAME, TABLE_ROWS, ROUND(((DATA_LENGTH + INDEX_LENGTH) / 1024 / 1024), 2) AS size_mb FROM information_schema.TABLES WHERE TABLE_SCHEMA = '{name}' ORDER BY TABLE_NAME");
    CliWrap.Buffered.BufferedCommandResult result;
    try
    {
        result = await CliWrap.Cli.Wrap(mysqlCli)
            .WithArguments(args)
            .WithEnvironmentVariables(MysqlEnvVars())
            .WithValidation(CliWrap.CommandResultValidation.None)
            .ExecuteBufferedAsync();
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = $"Failed to invoke mysql: {ex.Message}" });
    }
    if (result.ExitCode != 0)
        return Results.BadRequest(new { error = result.StandardError.Trim() });
    var tables = result.StandardOutput.Trim()
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line =>
        {
            var parts = line.Split('\t');
            return new { name = parts[0], rows = parts.Length > 1 ? parts[1] : "0", size = parts.Length > 2 ? parts[2] + " MB" : "0 MB" };
        }).ToList();
    return Results.Ok(new { tables });
});

// Database size
app.MapGet("/api/databases/{name}/size", async (string name, BinaryManager bm, SettingsStore settings, IServiceProvider sp) =>
{
    if (!IsValidDatabaseName(name))
        return Results.BadRequest(new { error = "Invalid database name" });
    var mysql = bm.ListInstalled("mysql").FirstOrDefault();
    if (mysql?.Executable is null)
        return Results.BadRequest(new { error = "MySQL not installed" });
    var mysqlCli = Path.Combine(Path.GetDirectoryName(mysql.Executable)!, OperatingSystem.IsWindows() ? "mysql.exe" : "mysql");
    var args = MysqlBaseArgs(ResolveMysqlPortWithFallback(settings, sp, pluginLoader, bm));
    args.Add("-N");
    args.Add("-e");
    args.Add($"SELECT ROUND(SUM(DATA_LENGTH + INDEX_LENGTH) / 1024 / 1024, 2) FROM information_schema.TABLES WHERE TABLE_SCHEMA = '{name}'");
    try
    {
        var result = await CliWrap.Cli.Wrap(mysqlCli)
            .WithArguments(args)
            .WithEnvironmentVariables(MysqlEnvVars())
            .WithValidation(CliWrap.CommandResultValidation.None)
            .ExecuteBufferedAsync();
        if (result.ExitCode != 0)
            return Results.BadRequest(new { error = result.StandardError.Trim() });
        return Results.Ok(new { size = result.StandardOutput.Trim() + " MB" });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = $"Failed to invoke mysql: {ex.Message}" });
    }
});

// Database query execution
app.MapPost("/api/databases/{name}/query", async (string name, HttpContext ctx, BinaryManager bm, SettingsStore settings, IServiceProvider sp) =>
{
    if (!IsValidDatabaseName(name))
        return Results.BadRequest(new { error = "Invalid database name" });
    var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>();
    var sql = body?.GetValueOrDefault("sql") ?? "";
    if (string.IsNullOrWhiteSpace(sql))
        return Results.BadRequest(new { error = "sql required" });
    var mysql = bm.ListInstalled("mysql").FirstOrDefault();
    if (mysql?.Executable is null)
        return Results.BadRequest(new { error = "MySQL not installed" });
    var mysqlCli = Path.Combine(Path.GetDirectoryName(mysql.Executable)!, OperatingSystem.IsWindows() ? "mysql.exe" : "mysql");
    var args = MysqlBaseArgs(ResolveMysqlPortWithFallback(settings, sp, pluginLoader, bm));
    args.Add(name);
    args.Add("-e");
    args.Add(sql);
    CliWrap.Buffered.BufferedCommandResult result;
    try
    {
        result = await CliWrap.Cli.Wrap(mysqlCli)
            .WithArguments(args)
            .WithEnvironmentVariables(MysqlEnvVars())
            .WithValidation(CliWrap.CommandResultValidation.None)
            .ExecuteBufferedAsync();
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = $"Failed to invoke mysql: {ex.Message}" });
    }
    if (result.ExitCode != 0)
        return Results.BadRequest(new { error = result.StandardError.Trim() });
    // Parse tab-separated output to JSON
    var lines = result.StandardOutput.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    if (lines.Length == 0)
        return Results.Ok(new { rows = Array.Empty<object>(), message = "Query executed successfully (no output)" });
    var headers = lines[0].Split('\t').Select(h => h.Trim()).ToArray();
    var rows = lines.Skip(1).Select(l =>
    {
        var vals = l.Split('\t').Select(v => v.Trim()).ToArray();
        var row = new Dictionary<string, string>();
        for (int i = 0; i < headers.Length && i < vals.Length; i++)
            row[headers[i]] = vals[i];
        return row;
    }).ToList();
    return Results.Ok(new { columns = headers, rows, rowCount = rows.Count });
});

// Database export (mysqldump)
app.MapGet("/api/databases/{name}/export", async (string name, BinaryManager bm, SettingsStore settings, IServiceProvider sp) =>
{
    if (!IsValidDatabaseName(name))
        return Results.BadRequest(new { error = "Invalid database name" });
    var mysql = bm.ListInstalled("mysql").FirstOrDefault();
    if (mysql?.Executable is null)
        return Results.BadRequest(new { error = "MySQL not installed" });
    var mysqldump = Path.Combine(Path.GetDirectoryName(mysql.Executable)!, OperatingSystem.IsWindows() ? "mysqldump.exe" : "mysqldump");
    if (!File.Exists(mysqldump))
        return Results.BadRequest(new { error = "mysqldump.exe not found" });
    var args = MysqlBaseArgs(ResolveMysqlPortWithFallback(settings, sp, pluginLoader, bm));
    args.Add(name);
    try
    {
        var result = await CliWrap.Cli.Wrap(mysqldump)
            .WithArguments(args)
            .WithEnvironmentVariables(MysqlEnvVars())
            .WithValidation(CliWrap.CommandResultValidation.None)
            .ExecuteBufferedAsync();
        if (result.ExitCode != 0)
            return Results.BadRequest(new { error = result.StandardError.Trim() });
        return Results.Text(result.StandardOutput, "application/sql");
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = $"Failed to invoke mysqldump: {ex.Message}" });
    }
});

// Database import (mysql < file)
app.MapPost("/api/databases/{name}/import", async (string name, HttpContext ctx, BinaryManager bm, SettingsStore settings, IServiceProvider sp) =>
{
    if (!IsValidDatabaseName(name))
        return Results.BadRequest(new { error = "Invalid database name" });
    var mysql = bm.ListInstalled("mysql").FirstOrDefault();
    if (mysql?.Executable is null)
        return Results.BadRequest(new { error = "MySQL not installed" });
    var mysqlCli = Path.Combine(Path.GetDirectoryName(mysql.Executable)!, OperatingSystem.IsWindows() ? "mysql.exe" : "mysql");

    // Read uploaded SQL file or raw body
    string sql;
    if (ctx.Request.HasFormContentType)
    {
        var form = await ctx.Request.ReadFormAsync();
        var file = form.Files.FirstOrDefault();
        if (file is null) return Results.BadRequest(new { error = "No file uploaded" });
        using var reader = new StreamReader(file.OpenReadStream());
        sql = await reader.ReadToEndAsync();
    }
    else
    {
        // `leaveOpen: true` because ASP.NET owns the request body stream;
        // StreamReader's default Dispose closes the underlying stream,
        // which would fight the hosting layer. The `using` still releases
        // the reader's ~1 KB read buffer immediately.
        using var reader = new StreamReader(ctx.Request.Body, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: -1, leaveOpen: true);
        sql = await reader.ReadToEndAsync();
    }

    var tmpFile = Path.GetTempFileName();
    await File.WriteAllTextAsync(tmpFile, sql);
    var args = MysqlBaseArgs(ResolveMysqlPortWithFallback(settings, sp, pluginLoader, bm));
    args.Add(name);
    try
    {
        var result = await CliWrap.Cli.Wrap(mysqlCli)
            .WithArguments(args)
            .WithEnvironmentVariables(MysqlEnvVars())
            .WithStandardInputPipe(CliWrap.PipeSource.FromFile(tmpFile))
            .WithValidation(CliWrap.CommandResultValidation.None)
            .ExecuteBufferedAsync();
        return result.ExitCode == 0
            ? Results.Ok(new { ok = true, message = "Import completed" })
            : Results.BadRequest(new { error = result.StandardError.Trim() });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = $"Failed to invoke mysql: {ex.Message}" });
    }
    finally
    {
        try { File.Delete(tmpFile); } catch { /* best-effort */ }
    }
});

// ── Binary catalog + installation management ──────────────────────────────
// GET /api/binaries/catalog          → all known releases
// GET /api/binaries/catalog/{app}    → releases for one app
// GET /api/binaries/installed        → currently installed binaries
// GET /api/binaries/installed/{app}  → installed versions for one app
// POST /api/binaries/install         → { app, version } downloads + extracts
// DELETE /api/binaries/{app}/{version} → uninstall

app.MapGet("/api/binaries/catalog", (CatalogClient cc) =>
    Results.Ok(cc.CachedReleases));

app.MapGet("/api/binaries/catalog/{app}", (string app, CatalogClient cc) =>
    Results.Ok(cc.ForApp(app)));

// Latest release for {app} on the current daemon's OS/arch. Powers the
// onboarding wizard — lets the UI request "apache latest" without
// hardcoding a version that may not exist on the user's platform
// (e.g. macOS/arm64 has apache 2.4.66 only; Windows has many).
// Query params allow overriding OS/arch for cross-platform tooling.
app.MapGet("/api/binaries/catalog/{app}/latest", (string app, string? os, string? arch, CatalogClient cc) =>
{
    var targetOs = (os ?? CatalogClient.CurrentOs()).ToLowerInvariant();
    var targetArch = (arch ?? CatalogClient.CurrentArch()).ToLowerInvariant();
    var candidates = cc.ForApp(app)
        .Where(r =>
            string.Equals(r.Os, targetOs, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.Arch, targetArch, StringComparison.OrdinalIgnoreCase))
        .ToList();
    if (candidates.Count == 0)
        return Results.NotFound(new { error = $"No {app} release available for {targetOs}/{targetArch}" });
    // Sort by semver-ish Version descending (stable: empty segments treated as 0)
    candidates.Sort((a, b) =>
    {
        int Compare(string va, string vb)
        {
            var pa = va.Split('.'); var pb = vb.Split('.');
            for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
            {
                int.TryParse(i < pa.Length ? pa[i] : "0", out var na);
                int.TryParse(i < pb.Length ? pb[i] : "0", out var nb);
                if (na != nb) return nb.CompareTo(na);
            }
            return 0;
        }
        return Compare(a.Version, b.Version);
    });
    var latest = candidates[0];
    return Results.Ok(new { app = latest.App, version = latest.Version, os = latest.Os, arch = latest.Arch, url = latest.Url });
});

app.MapPost("/api/binaries/catalog/refresh", async (CatalogClient cc, CancellationToken ct) =>
{
    var count = await cc.RefreshAsync(ct);
    return Results.Ok(new { count, lastFetch = cc.LastFetch });
});

// F95 plugin catalog surface — parallels the binaries catalog so the
// frontend marketplace view + the daemon startup sync both pull from a
// single source of truth (catalog-api's /api/v1/plugins/catalog).
app.MapGet("/api/plugins/catalog", (PluginCatalogClient pc) =>
    Results.Ok(new { count = pc.Cached.Count, lastFetch = pc.LastFetch, plugins = pc.Cached }));

app.MapPost("/api/plugins/catalog/refresh", async (PluginCatalogClient pc, CancellationToken ct) =>
{
    var count = await pc.RefreshAsync(ct);
    return Results.Ok(new { count, lastFetch = pc.LastFetch });
});

app.MapPost("/api/plugins/catalog/sync", async (
    PluginCatalogClient pc,
    PluginDownloader pd,
    CancellationToken ct) =>
{
    // Ensure cache is warm — sync against a stale list would miss new releases.
    await pc.RefreshAsync(ct);
    var installed = await pd.SyncLatestAsync(pc.Cached, ct);
    return Results.Ok(new
    {
        catalogCount = pc.Cached.Count,
        installedThisCall = installed,
        cacheRoot = PluginDownloader.CacheRoot()
    });
});

// F95 telemetry — summarises plugin catalog + cache health for the Settings
// page. Poll target: lastFetch (UTC, null if catalog has never been
// refreshed), catalogCount (live), cachedCount (plugins materialised on
// disk via SyncLatestAsync), cacheRoot (so a user can grep the folder by
// hand when something looks off).
app.MapGet("/api/plugins/catalog/status", (PluginCatalogClient pc) =>
{
    var cacheRoot = PluginDownloader.CacheRoot();
    var cachedCount = 0;
    try
    {
        foreach (var _ in PluginDownloader.EnumerateLatestVersionDirs()) cachedCount++;
    }
    catch { /* root may not exist yet — treat as zero-cached */ }
    return Results.Ok(new
    {
        catalogCount = pc.Cached.Count,
        cachedCount,
        lastFetch = pc.LastFetch == DateTime.MinValue ? (DateTime?)null : pc.LastFetch,
        cacheRoot,
    });
});

app.MapGet("/api/binaries/installed", (BinaryManager bm) =>
    Results.Ok(bm.ListInstalled()));

app.MapGet("/api/binaries/installed/{app}", (string app, BinaryManager bm) =>
    Results.Ok(bm.ListInstalled(app)));

app.MapPost("/api/binaries/install", async (HttpContext ctx, BinaryManager bm) =>
{
    InstallBinaryRequest? req;
    try
    {
        req = await ctx.Request.ReadFromJsonAsync<InstallBinaryRequest>();
    }
    catch (System.Text.Json.JsonException ex)
    {
        return Results.BadRequest(new { error = $"Invalid JSON body: {ex.Message}" });
    }
    if (req is null || string.IsNullOrWhiteSpace(req.App) || string.IsNullOrWhiteSpace(req.Version))
        return Results.BadRequest(new { error = "app and version required" });

    try
    {
        BinaryManager.ValidateAppVersion(req.App, req.Version);
        var installed = await bm.EnsureInstalledAsync(req.App, req.Version, progress: null, ct: ctx.RequestAborted);
        return Results.Ok(installed);
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (UnauthorizedAccessException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (Exception ex) { return Results.Problem($"Install failed: {ex.Message}"); }
});

app.MapDelete("/api/binaries/{app}/{version}", (string app, string version, BinaryManager bm) =>
{
    try
    {
        BinaryManager.ValidateAppVersion(app, version);
        bm.Uninstall(app, version);
        return Results.NoContent();
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (UnauthorizedAccessException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// WebSocket log streaming — real-time per-service log lines with zero
// batching delay. Complements the SSE /api/events endpoint which handles
// service state, metrics, and validation events. The log viewer in the
// frontend connects here for immediate log output.
app.Map("/api/logs/{id}/stream", async (HttpContext ctx, string id, WebSocketLogStreamer streamer) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsync("WebSocket upgrade required");
        return;
    }

    var modules = ctx.RequestServices.GetServices<IServiceModule>();
    var module = modules.FirstOrDefault(m => m.ServiceId.Equals(id, StringComparison.OrdinalIgnoreCase));
    if (module == null)
    {
        ctx.Response.StatusCode = 404;
        await ctx.Response.WriteAsync($"Service '{id}' not found");
        return;
    }

    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();

    // Send recent log history first so the client doesn't start with an empty viewer
    var history = await module.GetLogsAsync(50, ctx.RequestAborted);
    foreach (var line in history)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new { line, ts = DateTime.UtcNow });
        var bytes = System.Text.Encoding.UTF8.GetBytes(payload);
        await ws.SendAsync(new ArraySegment<byte>(bytes),
            System.Net.WebSockets.WebSocketMessageType.Text, true, ctx.RequestAborted);
    }

    // Stream new lines as they arrive
    await streamer.StreamAsync(id, ws, ctx.RequestAborted);
});

// SSE endpoint
app.MapGet("/api/events", async (HttpContext ctx, SseService sse) =>
{
    ctx.Response.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers.Connection = "keep-alive";

    var clientId = sse.AddClient(ctx.Response);

    // Send initial connected event
    await ctx.Response.WriteAsync($"event: connected\ndata: {{\"clientId\":\"{clientId}\"}}\n\n");
    await ctx.Response.Body.FlushAsync();

    // Keep connection alive until client disconnects
    try
    {
        await Task.Delay(Timeout.Infinite, ctx.RequestAborted);
    }
    catch (TaskCanceledException) { }
    finally
    {
        sse.RemoveClient(clientId);
    }
});

// Graceful shutdown — stop all services + cleanup
app.Lifetime.ApplicationStopping.Register(() =>
{
    var shutdownLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Shutdown");
    shutdownLogger.LogInformation("Shutdown requested — stopping services and cleaning port file");
    app.Services.GetRequiredService<ShutdownCoordinator>()
        .StopAllAsync(
            app.Services.GetServices<IServiceModule>(),
            pluginLoader.Plugins,
            portFile,
            CancellationToken.None)
        .GetAwaiter()
        .GetResult();
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
