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
/// Service status and start/stop/restart routes.
/// Moved verbatim out of Program.cs; registered from there at the same
/// point in the pipeline so route order is unchanged.
/// </summary>
internal static class ServiceEndpoints
{
    public static void MapServiceEndpoints(this WebApplication app)
    {
        // Service management endpoints — query real plugin modules
        app.MapGet("/api/services", async (IServiceProvider sp, BinaryManager bm, ServiceRunTracker tracker) =>
        {
            // Fan out status probes concurrently — same rationale as /api/system
            // (commit 9cf5d53). This endpoint is hit by every AppSidebar poll (5s
            // cadence) so the sequential foreach was the per-poll floor for the
            // whole services pane.
            var modules = sp.GetServices<IServiceModule>().ToArray();
            var statuses = await Task.WhenAll(
                modules.Select(m => m.GetStatusAsync(CancellationToken.None)));
            return Results.Ok(ServiceInfoProjection.BuildAll(statuses, bm.ListInstalled(), tracker, modules));
        });

        app.MapGet("/api/services/{id}", async (string id, IServiceProvider sp, BinaryManager bm, ServiceRunTracker tracker) =>
        {
            var modules = sp.GetServices<IServiceModule>();
            var module = modules.FirstOrDefault(m => m.ServiceId.Equals(id, StringComparison.OrdinalIgnoreCase)
                || m.Type.ToString().Equals(id, StringComparison.OrdinalIgnoreCase));
            if (module == null) return Results.NotFound();
            var status = await module.GetStatusAsync(CancellationToken.None);
            return Results.Ok(ServiceInfoProjection.Build(status, bm.ListInstalled(), tracker, module));
        });

        app.MapPost("/api/services/{id}/start", async (string id, IServiceProvider sp, ILoggerFactory lf, BinaryManager bm, ServiceRunTracker tracker) =>
        {
            var modules = sp.GetServices<IServiceModule>();
            var module = modules.FirstOrDefault(m => m.ServiceId.Equals(id, StringComparison.OrdinalIgnoreCase)
                || m.Type.ToString().Equals(id, StringComparison.OrdinalIgnoreCase));
            if (module == null) return Results.NotFound(new { error = $"No service module for '{id}'" });

            try
            {
                tracker.MarkStartRequested(module.ServiceId);
                await module.StartAsync(CancellationToken.None);
                var status = await module.GetStatusAsync(CancellationToken.None);
                return Results.Ok(ServiceInfoProjection.Build(status, bm.ListInstalled(), tracker, module));
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

        // MCP intent-gated under kind=service_restart. Service stop = downtime
        // for the dependent surface (sites going through Apache/Nginx, queries
        // hitting MySQL/Redis). An AI loop spamming stop = effective DoS;
        // header-conditional gate means GUI service rows keep working without
        // a token.
        app.MapPost("/api/services/{id}/stop", async (
            string id,
            IServiceProvider sp,
            ILoggerFactory lf,
            NKS.WebDevConsole.Core.Interfaces.IDeployIntentValidator intentValidator,
            HttpContext ctx,
            BinaryManager bm,
            ServiceRunTracker tracker,
            CancellationToken ct) =>
        {
            var stopIntentToken = ctx.Request.Headers["X-Intent-Token"].FirstOrDefault();
            if (!string.IsNullOrEmpty(stopIntentToken) || NKS.WebDevConsole.Daemon.Mcp.McpCallerContext.IsMcpToken)
            {
                var stopAllowUnconfirmed = string.Equals(
                    ctx.Request.Headers["X-Allow-Unconfirmed"].FirstOrDefault(), "true",
                    StringComparison.OrdinalIgnoreCase);
                var stopVerdict = await intentValidator.ValidateAndConsumeAsync(
                    stopIntentToken ?? "", "service_restart", domain: id, host: "*svc*", stopAllowUnconfirmed, ct);
                if (!stopVerdict.Ok)
                    return Results.Json(
                        new { error = "intent_rejected", reason = stopVerdict.Reason, detail = stopVerdict.Detail },
                        statusCode: stopVerdict.Reason == "pending_confirmation" ? 425 : 403);
            }

            var modules = sp.GetServices<IServiceModule>();
            var module = modules.FirstOrDefault(m => m.ServiceId.Equals(id, StringComparison.OrdinalIgnoreCase)
                || m.Type.ToString().Equals(id, StringComparison.OrdinalIgnoreCase));
            if (module == null) return Results.NotFound(new { error = $"No service module for '{id}'" });

            try
            {
                await module.StopAsync(CancellationToken.None);
                var status = await module.GetStatusAsync(CancellationToken.None);
                return Results.Ok(ServiceInfoProjection.Build(status, bm.ListInstalled(), tracker, module));
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

        // MCP intent-gated under kind=service_restart. Same risk profile as
        // stop (downtime during the stop-then-start window).
        app.MapPost("/api/services/{id}/restart", async (
            string id,
            IServiceProvider sp,
            ILoggerFactory lf,
            NKS.WebDevConsole.Core.Interfaces.IDeployIntentValidator intentValidator,
            HttpContext ctx,
            BinaryManager bm,
            ServiceRunTracker tracker,
            CancellationToken ct) =>
        {
            var rstIntentToken = ctx.Request.Headers["X-Intent-Token"].FirstOrDefault();
            if (!string.IsNullOrEmpty(rstIntentToken) || NKS.WebDevConsole.Daemon.Mcp.McpCallerContext.IsMcpToken)
            {
                var rstAllowUnconfirmed = string.Equals(
                    ctx.Request.Headers["X-Allow-Unconfirmed"].FirstOrDefault(), "true",
                    StringComparison.OrdinalIgnoreCase);
                var rstVerdict = await intentValidator.ValidateAndConsumeAsync(
                    rstIntentToken ?? "", "service_restart", domain: id, host: "*svc*", rstAllowUnconfirmed, ct);
                if (!rstVerdict.Ok)
                    return Results.Json(
                        new { error = "intent_rejected", reason = rstVerdict.Reason, detail = rstVerdict.Detail },
                        statusCode: rstVerdict.Reason == "pending_confirmation" ? 425 : 403);
            }

            var modules = sp.GetServices<IServiceModule>();
            var module = modules.FirstOrDefault(m => m.ServiceId.Equals(id, StringComparison.OrdinalIgnoreCase)
                || m.Type.ToString().Equals(id, StringComparison.OrdinalIgnoreCase));
            if (module == null) return Results.NotFound(new { error = $"No service module for '{id}'" });

            try
            {
                await module.StopAsync(CancellationToken.None);
                tracker.MarkStartRequested(module.ServiceId);
                await module.StartAsync(CancellationToken.None);
                var status = await module.GetStatusAsync(CancellationToken.None);
                return Results.Ok(ServiceInfoProjection.Build(status, bm.ListInstalled(), tracker, module));
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
    }
}
