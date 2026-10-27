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

namespace NKS.WebDevConsole.Daemon.Binaries;

/// <summary>
/// Binary and plugin catalog browsing plus binary install/uninstall.
/// Moved verbatim out of Program.cs; registered from there at the same
/// point in the pipeline so route order is unchanged.
/// </summary>
internal static class BinaryEndpoints
{
    public static void MapBinaryEndpoints(this WebApplication app)
    {
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

        // MCP intent-gated under kind=binary_install. Installing a new binary
        // (PHP, MySQL, Redis, ...) downloads and unpacks an executable into
        // ~/.wdc/binaries that then gets run by service modules. AI-driven
        // install of an attacker-controlled URL = privilege escalation. Header-
        // driven gate so the GUI install button stays untouched.
        app.MapPost("/api/binaries/install", async (
            HttpContext ctx,
            BinaryManager bm,
            SseService sse,
            NKS.WebDevConsole.Core.Interfaces.IDeployIntentValidator intentValidator) =>
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

            var biIntentToken = ctx.Request.Headers["X-Intent-Token"].FirstOrDefault();
            if (!string.IsNullOrEmpty(biIntentToken) || NKS.WebDevConsole.Daemon.Mcp.McpCallerContext.IsMcpToken)
            {
                var biAllowUnconfirmed = string.Equals(
                    ctx.Request.Headers["X-Allow-Unconfirmed"].FirstOrDefault(), "true",
                    StringComparison.OrdinalIgnoreCase);
                var biVerdict = await intentValidator.ValidateAndConsumeAsync(
                    biIntentToken ?? "", "binary_install", domain: req.App, host: "*binary*",
                    biAllowUnconfirmed, ctx.RequestAborted);
                if (!biVerdict.Ok)
                    return Results.Json(
                        new { error = "intent_rejected", reason = biVerdict.Reason, detail = biVerdict.Detail },
                        statusCode: biVerdict.Reason == "pending_confirmation" ? 425 : 403);
            }

            try
            {
                BinaryManager.ValidateAppVersion(req.App, req.Version);
                var installed = await bm.EnsureInstalledAsync(req.App, req.Version,
                    new BinaryInstallProgress(sse.BroadcastAsync), ctx.RequestAborted);
                try { await sse.BroadcastAsync("binary:installed", new { app = installed.App, version = installed.Version }); }
                catch { /* SSE fan-out is best-effort */ }
                return Results.Ok(installed);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (UnauthorizedAccessException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (Exception ex)
            {
                var (status, code, upstreamStatus) = ClassifyInstallError(ex);
                return Results.Json(
                    new { error = ex.Message, code, app = req.App, version = req.Version, upstreamStatus },
                    statusCode: status);
            }
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
    }

    /// <summary>
    /// Maps an install failure to an HTTP status and a stable code the UI
    /// translates. Upstream problems (mirror refused, served a web page,
    /// hash mismatch) are 502, not 500: the daemon itself is fine.
    /// </summary>
    internal static (int Status, string Code, int? UpstreamStatus) ClassifyInstallError(Exception ex) => ex switch
    {
        BinaryInstallException { Code: "no_build_for_platform" } => (404, "no_build_for_platform", null),
        BinaryInstallException bie => (502, bie.Code, null),
        HttpRequestException hre when hre.StatusCode is not null => (502, "download_failed", (int)hre.StatusCode),
        HttpRequestException or TaskCanceledException { InnerException: TimeoutException } => (502, "download_unreachable", null),
        OperationCanceledException => (499, "cancelled", null),
        _ => (500, "install_failed", null),
    };
}

/// <summary>
/// Forwards download progress to SSE as <c>binary:progress</c> events,
/// at most one per <paramref name="minIntervalMs"/> plus the final chunk.
/// </summary>
internal sealed class BinaryInstallProgress(Func<string, object, Task> broadcast, int minIntervalMs = 250)
    : IProgress<DownloadProgress>
{
    private long _lastSent = -1;

    public void Report(DownloadProgress p)
    {
        var now = Environment.TickCount64;
        var finished = p.Total > 0 && p.Downloaded >= p.Total;
        if (!finished && _lastSent >= 0 && now - _lastSent < minIntervalMs) return;
        _lastSent = now;
        _ = SendAsync(new { app = p.App, version = p.Version, downloaded = p.Downloaded, total = p.Total });
    }

    private async Task SendAsync(object payload)
    {
        try { await broadcast("binary:progress", payload); }
        catch { /* a dead SSE client must not fail the install */ }
    }
}
