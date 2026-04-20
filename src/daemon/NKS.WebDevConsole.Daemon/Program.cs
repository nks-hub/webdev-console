using System.Text.Json;
using Dapper;
using NKS.WebDevConsole.Daemon.Plugin;
using NKS.WebDevConsole.Daemon.Services;
using NKS.WebDevConsole.Daemon.Sites;
using NKS.WebDevConsole.Daemon.Binaries;
using NKS.WebDevConsole.Daemon.Backup;
using NKS.WebDevConsole.Daemon.Config;
using NKS.WebDevConsole.Daemon.Data;
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
builder.Services.AddSingleton<BinaryManager>();

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

// Phase 1: Load plugin assemblies and call Initialize (registers DI services) BEFORE Build
var earlyLoggerFactory = LoggerFactory.Create(b => b.AddConsole());
var pluginLoader = new PluginLoader(earlyLoggerFactory.CreateLogger<PluginLoader>());
// Production: plugins/ next to daemon binary. Dev: build/plugins/ at repo root.
var pluginDir = Path.Combine(AppContext.BaseDirectory, "plugins");
if (!Directory.Exists(pluginDir))
{
    var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
    pluginDir = Path.Combine(repoRoot, "build", "plugins");
}
pluginLoader.LoadPlugins(pluginDir);

// F95 phase 1: when no local plugin build is found, fall back to the
// on-disk cache populated from the catalog-api release artifacts at
// ~/.wdc/plugins/<id>/<version>/. This is the production path once F99
// removes src/plugins/ from the monorepo — end-users get plugins via the
// catalog download flow, developers still use build/plugins locally.
// Remote fetch itself is deferred to a background service post-Build so
// pre-build phase stays fast + offline-tolerant; here we only load what
// is already on disk.
var hasLocalPlugins = pluginLoader.Plugins.Count > 0;
if (!hasLocalPlugins)
{
    foreach (var cacheDir in PluginDownloader.EnumerateLatestVersionDirs())
    {
        pluginLoader.LoadPlugins(cacheDir);
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

var app = builder.Build();
app.UseWebSockets();
app.UseCors();

// Sentry crash reporting — opt-in only. The TelemetryConsent singleton is the
// sole gate: if the user hasn't ticked the Settings page toggle, Sentry is
// never initialised and no network calls happen. DSN comes from the env var
// NKS_WDC_SENTRY_DSN; if absent, the feature is inert even with consent so
// self-hosters can point at their own Sentry instance before enabling.
//
// Privacy scrubbing matches the doc comment in TelemetryConsent.cs:
// allowed — .NET version, OS version, daemon version, stack trace
// forbidden — file paths, hostnames, site names, DB contents, passwords
{
    var consent = app.Services.GetRequiredService<TelemetryConsent>();
    var dsn = Environment.GetEnvironmentVariable("NKS_WDC_SENTRY_DSN");
    var sentryLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Sentry");
    if (consent.Enabled && consent.CrashReports && !string.IsNullOrWhiteSpace(dsn))
    {
        try
        {
            Sentry.SentrySdk.Init(o =>
            {
                o.Dsn = dsn;
                o.Release = typeof(Program).Assembly.GetName().Version?.ToString() ?? "dev";
                o.Environment = "production";
                // Never capture anything we can't prove is safe.
                // SendDefaultPii:false strips IP, username, email, cookies, headers.
                o.SendDefaultPii = false;
                o.AutoSessionTracking = false;
                o.AttachStacktrace = true;
                o.ServerName = ""; // never include the hostname
                // Additional scrubbing: drop the machine server name, any context
                // the SDK auto-populates that could leak local paths, and reset
                // the user object on every event.
                o.SetBeforeSend((sentryEvent, _) =>
                {
                    sentryEvent.ServerName = "";
                    sentryEvent.User = new Sentry.SentryUser();
                    return sentryEvent;
                });
            });
            sentryLog.LogInformation("Sentry crash reporting initialised (consent given)");
        }
        catch (Exception ex)
        {
            sentryLog.LogWarning(ex, "Sentry init failed — crash reporting disabled for this session");
        }
    }
    else if (consent.Enabled && consent.CrashReports && string.IsNullOrWhiteSpace(dsn))
    {
        sentryLog.LogInformation("Sentry consent given but NKS_WDC_SENTRY_DSN not set — no reports will be sent");
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
    File.WriteAllText(tmpFile, $"{port}\n{authToken}");
    File.Move(tmpFile, portFile, overwrite: true);
    Console.WriteLine($"[daemon] listening on port {port}, port file: {portFile}");
});

// Health endpoint — no auth required (for monitoring + Electron daemon detection)
app.MapGet("/healthz", () => Results.Ok(new { ok = true, timestamp = DateTime.UtcNow }));
app.MapPost("/api/admin/shutdown", (IHostApplicationLifetime lifetime) =>
{
    _ = Task.Run(() => lifetime.StopApplication());
    return Results.Accepted();
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
        provided ??= ctx.Request.Query["token"].FirstOrDefault();

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
    var modules = sp.GetServices<IServiceModule>();
    var running = 0; var total = 0;
    foreach (var m in modules) { total++; var s = await m.GetStatusAsync(CancellationToken.None); if (s.State == ServiceState.Running) running++; }

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
    return Results.Ok(pluginLoader.Plugins.Select(p =>
    {
        var svcId = p.Instance.Id.Split('.').LastOrDefault() ?? "";
        var hasService = modules.Any(m => m.ServiceId.Contains(svcId, StringComparison.OrdinalIgnoreCase));

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
    var modules = sp.GetServices<IServiceModule>();
    var statuses = new List<object>();
    foreach (var m in modules)
    {
        var status = await m.GetStatusAsync(CancellationToken.None);
        statuses.Add(status);
    }
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
var startupOrchestrator = app.Services.GetRequiredService<SiteOrchestrator>();
var startupSitesSnapshot = siteManager.Sites.Values.ToList();
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

// Sweep orphan *.tmp files left over from a previous daemon crash or taskkill
// during an in-progress AtomicWriter.WriteAsync. Only touches files older than
// 1 hour so we don't clobber an in-flight write from a concurrent tool.
try
{
    var sitesRoot = NKS.WebDevConsole.Core.Services.WdcPaths.SitesRoot;
    var generatedRoot = NKS.WebDevConsole.Core.Services.WdcPaths.GeneratedRoot;
    var orphanCount = 0;
    if (Directory.Exists(sitesRoot))
        orphanCount += AtomicWriter.CleanupOrphanTempFiles(sitesRoot);
    if (Directory.Exists(generatedRoot))
        orphanCount += AtomicWriter.CleanupOrphanTempFiles(generatedRoot);
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
    var startTasks = modules.Select(async module =>
    {
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
        var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        site = root.Deserialize<SiteConfig>(jsonOpts) ?? new SiteConfig();

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
        var mysqladmin = mysql?.Executable is null ? null : Path.Combine(Path.GetDirectoryName(mysql.Executable)!, "mysqladmin.exe");
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
    InvokeComposerAsync(object invoker, string method, object[] args)
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

app.MapPost("/api/onboarding/complete", () =>
{
    var flagFile = Path.Combine(NKS.WebDevConsole.Core.Services.WdcPaths.DataRoot, "onboarding-complete.flag");
    Directory.CreateDirectory(Path.GetDirectoryName(flagFile)!);
    File.WriteAllText(flagFile, DateTime.UtcNow.ToString("O"));
    return Results.Ok(new { completed = true });
});

// Backup / restore — Phase 7 plan item.
// Creates a zip of ~/.wdc/sites + ~/.wdc/data/state.db + ~/.wdc/ssl/sites + ~/.wdc/caddy.
// Restore is atomic with an automatic pre-restore safety backup.
app.MapGet("/api/backup/list", (BackupManager bm) =>
{
    var list = bm.ListBackups()
        .Select(b => new { path = b.Path, size = b.Size, createdUtc = b.Created })
        .ToList();
    return Results.Ok(new { count = list.Count, backups = list });
});

app.MapPost("/api/backup", (BackupManager bm, HttpContext ctx) =>
{
    try
    {
        var outPath = ctx.Request.Query["out"].FirstOrDefault();
        var (path, files, size) = bm.CreateBackup(outPath);
        return Results.Ok(new { path, files, size });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Backup failed: {ex.Message}");
    }
});

app.MapGet("/api/backup/download", (BackupManager bm, string? path) =>
{
    string target;
    if (string.IsNullOrEmpty(path))
    {
        var latest = bm.ListBackups().FirstOrDefault();
        if (latest.Path is null) return Results.NotFound(new { error = "No backups available" });
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
        // Malformed JSON in the body — distinct from server-side restore
        // failure. Return 400 so the frontend can show a parse error
        // instead of "Restore failed" which would suggest a server bug.
        return Results.BadRequest(new { error = $"Invalid JSON body: {ex.Message}" });
    }
    catch (FileNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
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

        try { System.Diagnostics.Process.Start("ipconfig", "/flushdns")?.WaitForExit(5000); } catch { }

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
        try { System.Diagnostics.Process.Start("ipconfig", "/flushdns")?.WaitForExit(5000); } catch { }

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
    if (method != null)
    {
        var versions = method.Invoke(phpPlugin.Instance, null);
        return Results.Ok(versions);
    }
    return Results.Ok(Array.Empty<object>());
});

// PHP extensions for a given version
app.MapGet("/api/php/{version}/extensions", async (string version) =>
{
    var phpPlugin = pluginLoader.Plugins.FirstOrDefault(p => p.Instance.Id == "nks.wdc.php");
    if (phpPlugin == null) return Results.NotFound();
    var method = phpPlugin.Instance.GetType().GetMethod("GetExtensionsForVersion");
    if (method != null)
    {
        var task = method.Invoke(phpPlugin.Instance, new object[] { version }) as Task;
        if (task != null)
        {
            await task;
            var resultProp = task.GetType().GetProperty("Result");
            return Results.Ok(resultProp?.GetValue(task));
        }
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
            var (path, _, _) = backupManager.CreateBackup();
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
app.MapPost("/api/plugins/{id}/enable", (string id, PluginState pluginState) =>
{
    var plugin = pluginLoader.Plugins.FirstOrDefault(p => p.Instance.Id == id);
    if (plugin == null) return Results.NotFound(new { error = $"Plugin '{id}' not loaded" });
    pluginState.SetEnabled(id, true);
    return Results.Ok(new { id, enabled = true });
});

app.MapPost("/api/plugins/{id}/disable", (string id, PluginState pluginState) =>
{
    var plugin = pluginLoader.Plugins.FirstOrDefault(p => p.Instance.Id == id);
    if (plugin == null) return Results.NotFound(new { error = $"Plugin '{id}' not loaded" });
    pluginState.SetEnabled(id, false);
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
                var x509 = System.Security.Cryptography.X509Certificates.X509CertificateLoader
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
    var task = (Task<bool>)method.Invoke(sslPlugin.Instance, null)!;
    var success = await task;
    return success
        ? Results.Ok(new { ok = true, message = "CA installed" })
        : Results.BadRequest(new { ok = false, message = "Failed to install CA" });
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
    var settings = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>();
    if (settings == null) return Results.BadRequest();
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

    var mysqlCli = Path.Combine(Path.GetDirectoryName(mysql.Executable)!, "mysql.exe");
    if (!File.Exists(mysqlCli))
        return Results.Ok(new { error = "mysql.exe not found", databases = Array.Empty<string>() });

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
            if (stderr.Contains("1045") || stderr.Contains("Access denied"))
                hint = $"Port {port} has a mysqld process but WDC root password was rejected. Likely external MySQL (MAMP/XAMPP/Windows service) occupies this port. Fix: Services tab → Start WDC MySQL on different port, OR Settings → Porty → change MySQL port.";
            return Results.Ok(new { error = stderr, hint, attemptedPort = port, databases = Array.Empty<string>() });
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

    var mysqlCli = Path.Combine(Path.GetDirectoryName(mysql.Executable)!, "mysql.exe");
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

    var mysqlCli = Path.Combine(Path.GetDirectoryName(mysql.Executable)!, "mysql.exe");
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
    var mysqlCli = Path.Combine(Path.GetDirectoryName(mysql.Executable)!, "mysql.exe");
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
    var mysqlCli = Path.Combine(Path.GetDirectoryName(mysql.Executable)!, "mysql.exe");
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
    var mysqlCli = Path.Combine(Path.GetDirectoryName(mysql.Executable)!, "mysql.exe");
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
    var mysqldump = Path.Combine(Path.GetDirectoryName(mysql.Executable)!, "mysqldump.exe");
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
    var mysqlCli = Path.Combine(Path.GetDirectoryName(mysql.Executable)!, "mysql.exe");

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
        sql = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
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
    app.Services.GetRequiredService<ShutdownCoordinator>()
        .StopAllAsync(
            app.Services.GetServices<IServiceModule>(),
            pluginLoader.Plugins,
            portFile,
            CancellationToken.None)
        .GetAwaiter()
        .GetResult();
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

await app.RunAsync();

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
