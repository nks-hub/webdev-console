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
/// Telemetry consent read/update/revoke.
/// Moved verbatim out of Program.cs; registered from there at the same
/// point in the pipeline so route order is unchanged.
/// </summary>
internal static class TelemetryEndpoints
{
    public static void MapTelemetryEndpoints(this WebApplication app)
    {
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
    }
}
