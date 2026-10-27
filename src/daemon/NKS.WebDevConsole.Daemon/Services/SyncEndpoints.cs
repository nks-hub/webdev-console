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

namespace NKS.WebDevConsole.Daemon.Services;

/// <summary>
/// Catalog sync status and catalog-api config sync (pull/push/exists).
/// Moved verbatim out of Program.cs; registered from there at the same
/// point in the pipeline so route order is unchanged.
/// </summary>
internal static class SyncEndpoints
{
    // The renderer sends its login token in X-Catalog-Token. The CLI has no
    // login of its own and GET /api/settings no longer hands out the token,
    // so fall back to the copy the renderer mirrored into settings.
    private static string? CatalogToken(HttpContext ctx, SettingsStore settings) =>
        ctx.Request.Headers["X-Catalog-Token"].FirstOrDefault() is { Length: > 0 } header
            ? header
            : settings.GetString("sync", "accountToken");

    public static void MapSyncEndpoints(this WebApplication app)
    {
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

            var catalogToken = CatalogToken(ctx, settings);
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
            var catalogToken = CatalogToken(ctx, settings);
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

            var catalogToken = CatalogToken(ctx, settings);
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
    }
}
