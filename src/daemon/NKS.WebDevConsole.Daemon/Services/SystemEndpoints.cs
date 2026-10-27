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
/// Daemon status and system information routes.
/// Moved verbatim out of Program.cs; registered from there at the same
/// point in the pipeline so route order is unchanged.
/// </summary>
internal static class SystemEndpoints
{
    public static void MapSystemEndpoints(this WebApplication app, PluginLoader pluginLoader, string daemonVersion, Func<long> DaemonUptimeSeconds)
    {
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
    }
}
