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

namespace NKS.WebDevConsole.Daemon.Sites;

/// <summary>
/// Site CRUD, docker-compose, metrics and log routes.
/// Moved verbatim out of Program.cs; registered from there at the same
/// point in the pipeline so route order is unchanged.
/// </summary>
internal static class SiteEndpoints
{
    public static void MapSiteEndpoints(this WebApplication app, PluginLoader pluginLoader, System.Text.Json.JsonSerializerOptions caseInsensitiveJson)
    {
        app.MapGet("/api/sites", (SiteManager sm) => Results.Ok(sm.Sites.Values));

        app.MapGet("/api/sites/bind-addresses", () => Results.Ok(GetBindAddressOptions()));

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
        // Discovers the site's own Apache error log in every installed Apache version
        // and the logs of the PHP plugin's managed log directory. Returns a unified list of
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

            try
            {
                var entries = SiteErrorLogReader.Read(
                    domain,
                    bm.ListInstalled("apache").Select(apache => apache.InstallPath),
                    Path.Combine(WdcPaths.LogsRoot, "apache", "error.log"),
                    Path.Combine(WdcPaths.LogsRoot, "php"),
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
                var cfCfg = NKS.WebDevConsole.Daemon.Cloudflare.CloudflareEndpoints.ResolveServiceOrNull(pluginLoader, sp, "CloudflareConfig");
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

            try { SiteManager.ValidateDocumentRoot(site.DocumentRoot); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }

            bool createdRoot;
            try { createdRoot = SiteManager.EnsureDocumentRoot(site.DocumentRoot); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                return Results.BadRequest(new { error = $"Cannot create the site folder {site.DocumentRoot}: {ex.Message}" });
            }

            // A rejected create must not leave an empty folder behind (Delete is non-recursive).
            void DropCreatedRoot()
            {
                if (!createdRoot || sm.Get(site.Domain) is not null) return;
                try { Directory.Delete(site.DocumentRoot); } catch { /* not empty or in use: leave it */ }
            }

            try
            {
                var created = await sm.CreateAsync(site);
                var applyResult = await orchestrator.ApplyAsync(created);
                // Surface failed apply steps (cert, vhost, hosts…) — they used to be
                // logged only, so the site looked created and working when it wasn't.
                foreach (var step in applyResult.Steps.Where(st => st.Status == SiteApplyStep.Failed))
                    warnings.Add($"{step.Name}: {step.Detail}");

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

                // Bind-IP NIC sanity: surface (don't reject) if the operator configured
                // an explicit IP that isn't currently assigned to any Up interface.
                foreach (var w in SiteManager.CollectBindAddressWarnings(created))
                {
                    warnings.Add(w);
                    log.LogWarning("Site {Domain} bind warning: {Warning}", created.Domain, w);
                }

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
            catch (ArgumentException ex) { DropCreatedRoot(); return Results.BadRequest(new { error = ex.Message }); }
            catch (UnauthorizedAccessException ex) { DropCreatedRoot(); return Results.BadRequest(new { error = ex.Message }); }
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
                    BindAddress   = source.BindAddress,
                    BindAddresses = source.BindAddresses,
                    LocalhostLoopbackEnabled = source.LocalhostLoopbackEnabled,
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

        app.MapPut("/api/sites/{domain}", async (string domain, SiteConfig site, SiteManager sm, SiteOrchestrator orchestrator, ILoggerFactory lf) =>
        {
            if (sm.Get(domain) is null)
                return Results.NotFound();
            site.Domain = domain;
            try
            {
                var updated = await sm.UpdateAsync(site);
                var applyResult = await orchestrator.ApplyAsync(updated);
                var bindWarnings = SiteManager.CollectBindAddressWarnings(updated);
                var warnings = bindWarnings
                    .Concat(applyResult.Steps
                        .Where(st => st.Status == SiteApplyStep.Failed)
                        .Select(st => $"{st.Name}: {st.Detail}"))
                    .ToList();
                if (warnings.Count > 0)
                {
                    var log = lf.CreateLogger("SiteUpdate");
                    foreach (var w in bindWarnings)
                        log.LogWarning("Site {Domain} bind warning: {Warning}", updated.Domain, w);
                    return Results.Ok(new { site = updated, warnings });
                }
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

        // MCP intent-gated under kind=site_delete. AI with a wildcard session
        // grant could otherwise wipe every site as a single irreversible chain;
        // gate forces the same intent + always-confirm pipeline as deploy ops.
        // Header-driven so the GUI delete button stays untouched.
        app.MapDelete("/api/sites/{domain}", async (
            string domain,
            SiteManager sm,
            SiteOrchestrator orchestrator,
            NKS.WebDevConsole.Core.Interfaces.IDeployIntentValidator intentValidator,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var sdIntentToken = ctx.Request.Headers["X-Intent-Token"].FirstOrDefault();
            if (!string.IsNullOrEmpty(sdIntentToken) || NKS.WebDevConsole.Daemon.Mcp.McpCallerContext.IsMcpToken)
            {
                var sdAllowUnconfirmed = string.Equals(
                    ctx.Request.Headers["X-Allow-Unconfirmed"].FirstOrDefault(), "true",
                    StringComparison.OrdinalIgnoreCase);
                var sdVerdict = await intentValidator.ValidateAndConsumeAsync(
                    sdIntentToken ?? "", "site_delete", domain, host: "*site*", sdAllowUnconfirmed, ct);
                if (!sdVerdict.Ok)
                    return Results.Json(
                        new { error = "intent_rejected", reason = sdVerdict.Reason, detail = sdVerdict.Detail },
                        statusCode: sdVerdict.Reason == "pending_confirmation" ? 425 : 403);
            }

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

        // Re-run the apply pipeline for one site and return the per-step
        // result (ssl, vhost, php, reload, hosts, …). Used by the site
        // page's pipeline view to retry after fixing whatever failed,
        // without saving the config again.
        app.MapPost("/api/sites/{domain}/apply", async (string domain, SiteManager sm, SiteOrchestrator orchestrator, CancellationToken ct) =>
        {
            var site = sm.Get(domain);
            if (site is null) return Results.NotFound(new { error = $"Site {domain} not found" });
            var result = await orchestrator.ApplyAsync(site, ct);
            return Results.Ok(result.ToPayload());
        });

        // Last apply result of every site, so a window that opened (or reloaded)
        // after the startup re-apply still shows which sites ended in an error.
        app.MapGet("/api/sites/apply-status", (SiteOrchestrator orchestrator) =>
            Results.Ok(orchestrator.LastApplyResults.Select(r => r.ToPayload())));
    }

    internal static IReadOnlyList<BindAddressOption> GetBindAddressOptions()
    {
        var options = new List<BindAddressOption>
        {
            new("*", "*",
                "Name-based virtual host on every Apache listener. Use only when hostnames are unique across all sites on the same ports.",
                true, false, null),
            new(IPAddress.Loopback.ToString(), IPAddress.Loopback.ToString(),
                "Loopback-only. Good default for localhost and private local sites.",
                false, true, "Loopback"),
            new(IPAddress.IPv6Loopback.ToString(), IPAddress.IPv6Loopback.ToString(),
                "IPv6 loopback-only. Use when the site should be reachable on ::1.",
                false, true, "Loopback"),
        };
        var seen = new HashSet<string>(options.Select(o => o.Value), StringComparer.OrdinalIgnoreCase);

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces()
                     .Where(i => i.OperationalStatus == OperationalStatus.Up))
        {
            IPInterfaceProperties props;
            try { props = ni.GetIPProperties(); }
            catch { continue; }

            foreach (var unicast in props.UnicastAddresses)
            {
                var ip = unicast.Address;
                if (ip.AddressFamily is not (System.Net.Sockets.AddressFamily.InterNetwork
                    or System.Net.Sockets.AddressFamily.InterNetworkV6))
                    continue;
                if (ip.IsIPv6LinkLocal || ip.IsIPv6Multicast || IPAddress.IsLoopback(ip))
                    continue;
                var value = ip.ToString();
                if (!seen.Add(value))
                    continue;

                options.Add(new BindAddressOption(
                    value,
                    $"{value} - {ni.Name}",
                    "Bind this site only on this device address. Requests on other addresses cannot fall through to this vhost.",
                    false,
                    false,
                    ni.Name));
            }
        }

        return options;
    }
}
