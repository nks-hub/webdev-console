using Microsoft.AspNetCore.HostFiltering;

namespace NKS.WebDevConsole.Daemon.Config;

/// <summary>
/// Host headers the daemon answers. Kestrel binds loopback only, but a web
/// page can still reach it through DNS rebinding (a hostname that resolves to
/// 127.0.0.1 after the page loaded). Such requests carry the attacker's host
/// name, so the host-filtering middleware rejects them with 400 before any
/// endpoint runs. Any port is accepted. Every client (Electron, wdc CLI, MCP
/// server, Playwright, CI smoke scripts) uses one of these names.
/// </summary>
public static class DaemonHosts
{
    public static readonly string[] AllowedHosts = ["localhost", "127.0.0.1", "[::1]"];

    /// <summary>
    /// Pins the allow-list in code so it holds even when appsettings.json is not
    /// next to the working directory (the daemon is often started from elsewhere).
    /// </summary>
    public static IServiceCollection AddDaemonHostFiltering(this IServiceCollection services) =>
        services.Configure<HostFilteringOptions>(o =>
        {
            o.AllowedHosts = AllowedHosts.ToList();
            o.IncludeFailureMessage = false;
        });
}
