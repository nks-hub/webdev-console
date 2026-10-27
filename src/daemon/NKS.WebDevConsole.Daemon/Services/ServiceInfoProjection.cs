using System.Collections.Concurrent;
using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Daemon.Binaries;

namespace NKS.WebDevConsole.Daemon.Services;

/// <summary>
/// Service status as the UI receives it: the module's own status plus whether
/// the binary behind it is installed, which version, and (when crashed)
/// whether the process ever reached the running state.
/// </summary>
public sealed record ServiceInfoDto(
    string Id, string DisplayName,
    ServiceState State, int? Pid,
    double CpuPercent, long MemoryBytes,
    TimeSpan? Uptime,
    bool Installed, string? Version, string? BinaryApp,
    string? CrashKind);

/// <summary>
/// Remembers per service whether it was seen running since the last start
/// request. Plugin modules run their own processes, so the daemon cannot read
/// exit codes; "was it ever Running" is the part it can observe reliably.
/// </summary>
public sealed class ServiceRunTracker
{
    public const string StartFailed = "startFailed";
    public const string Exited = "exited";

    private readonly ConcurrentDictionary<string, bool> _sawRunning = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Call right before a start or restart is issued.</summary>
    public void MarkStartRequested(string serviceId) => _sawRunning[serviceId] = false;

    /// <summary>Feed every status that is about to be reported.</summary>
    public void Observe(ServiceStatus status)
    {
        if (status.State == ServiceState.Running) _sawRunning[status.Id] = true;
    }

    /// <summary>Null unless the service is crashed.</summary>
    public string? CrashKind(ServiceStatus status)
        => status.State != ServiceState.Crashed
            ? null
            : _sawRunning.GetValueOrDefault(status.Id) ? Exited : StartFailed;
}

/// <summary>Builds <see cref="ServiceInfoDto"/> rows from module statuses and the binaries on disk.</summary>
public static class ServiceInfoProjection
{
    // Service ids whose binary lives under ~/.wdc/binaries/{app}. Alternatives
    // are tried in order; the first one that is installed supplies the version.
    // Ids missing here (node, hosts, ...) have no managed binary and count as installed.
    private static readonly Dictionary<string, string[]> BinaryApps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["apache"] = ["apache"],
        ["httpd"] = ["apache"],
        ["nginx"] = ["nginx"],
        ["caddy"] = ["caddy"],
        ["php"] = ["php"],
        ["mysql"] = ["mysql", "mariadb"],
        ["mariadb"] = ["mariadb", "mysql"],
        ["postgresql"] = ["postgresql"],
        ["redis"] = ["redis"],
        ["mailpit"] = ["mailpit"],
        ["cloudflare"] = ["cloudflared"],
        ["cloudflared"] = ["cloudflared"],
    };

    public static ServiceInfoDto Build(
        ServiceStatus status,
        IReadOnlyList<InstalledBinary> installed,
        ServiceRunTracker tracker)
    {
        tracker.Observe(status);

        string? app = null;
        string? version = null;
        var isInstalled = true;
        if (BinaryApps.TryGetValue(status.Id, out var candidates))
        {
            foreach (var candidate in candidates)
            {
                var hit = installed.FirstOrDefault(b => b.App.Equals(candidate, StringComparison.OrdinalIgnoreCase));
                if (hit is null) continue;
                app = candidate;
                version = hit.Version;
                break;
            }
            isInstalled = app is not null;
            app ??= candidates[0];
        }

        // A module that is up is installed, whatever the folder scan says.
        if (!isInstalled && status.State == ServiceState.Running) isInstalled = true;

        return new ServiceInfoDto(
            status.Id, status.DisplayName, status.State, status.Pid,
            status.CpuPercent, status.MemoryBytes, status.Uptime,
            isInstalled, version, app, tracker.CrashKind(status));
    }

    public static ServiceInfoDto[] BuildAll(
        IEnumerable<ServiceStatus> statuses,
        IReadOnlyList<InstalledBinary> installed,
        ServiceRunTracker tracker)
        => statuses.Select(s => Build(s, installed, tracker)).ToArray();
}
