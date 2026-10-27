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
/// Per-site Node.js process routes.
/// Moved verbatim out of Program.cs; registered from there at the same
/// point in the pipeline so route order is unchanged.
/// </summary>
internal static class NodeSiteEndpoints
{
    public static void MapNodeSiteEndpoints(this WebApplication app)
    {
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
    }
}
