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
/// Service version validation, logs, config read/write and config validation.
/// Moved verbatim out of Program.cs; registered from there at the same
/// point in the pipeline so route order is unchanged.
/// </summary>
internal static class ServiceConfigEndpoints
{
    // Serialises config/validate runs that swap a draft into a live Apache include.
    private static readonly SemaphoreSlim LiveDraftGate = new(1, 1);

    public static void MapServiceConfigEndpoints(this WebApplication app, PluginLoader pluginLoader)
    {
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
        // An open log viewer renews this every ~15 s so ServiceLogTailer
        // streams that service's new lines (see ServiceLogTailer.Watch).
        app.MapPost("/api/services/{id}/logs/watch", (string id, ServiceLogTailer tailer) =>
        {
            tailer.Watch(id);
            return Results.NoContent();
        });

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

        // MCP intent-gated under kind=service_config_write. The body replaces a
        // service config file (httpd.conf, php.ini, my.cnf, ...) and the
        // service restarts with it, so a write can enable arbitrary handlers or
        // modules. GUI saves without a token header are unaffected.
        app.MapPost("/api/services/{id}/config", async (
            string id,
            HttpContext ctx,
            ServiceConfigManager configManager,
            IServiceProvider sp,
            NKS.WebDevConsole.Core.Interfaces.IDeployIntentValidator intentValidator) =>
        {
            var refused = await McpIntentGate.CheckAsync(
                ctx, intentValidator, "service_config_write", domain: id, host: "*svc*", ctx.RequestAborted);
            if (refused is not null) return refused;

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

                    // One draft at a time, and the original always goes back even
                    // when the client disconnects mid-run.
                    await LiveDraftGate.WaitAsync(ctx.RequestAborted);
                    try
                    {
                        var originalContent = await File.ReadAllTextAsync(normalizedConfigPath, ctx.RequestAborted);
                        await File.WriteAllTextAsync(normalizedConfigPath, body.Content, ctx.RequestAborted);
                        try
                        {
                            return await run(apacheRootConfigPath);
                        }
                        finally
                        {
                            await File.WriteAllTextAsync(normalizedConfigPath, originalContent, CancellationToken.None);
                        }
                    }
                    finally
                    {
                        LiveDraftGate.Release();
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
    }
}
