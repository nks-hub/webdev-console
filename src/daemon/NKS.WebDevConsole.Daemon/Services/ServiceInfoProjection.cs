using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Serialization;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Daemon.Binaries;

namespace NKS.WebDevConsole.Daemon.Services;

/// <summary>
/// Service status as the UI receives it: the module's own status plus whether
/// the binary behind it is installed, which version, and (when crashed)
/// whether the process ever reached the running state. Uptime also comes as
/// whole seconds, and the ports are the ones the module is configured for.
/// </summary>
public sealed record ServiceInfoDto(
    string Id, string DisplayName,
    ServiceState State, int? Pid,
    double CpuPercent, long MemoryBytes,
    TimeSpan? Uptime,
    bool Installed, string? Version, string? BinaryApp,
    string? CrashKind,
    // Left out of the JSON when unknown, so the UI reads them as optional numbers.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? UptimeSeconds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Port = null,
    IReadOnlyDictionary<string, int>? Ports = null,
    // Why the last start or restart (from the UI, the API or auto-start) threw;
    // kept until the service is seen running or a new start is requested.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? StartError = null);

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
    private readonly ConcurrentDictionary<string, string> _startErrors = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Call right before a start or restart is issued.</summary>
    public void MarkStartRequested(string serviceId)
    {
        _sawRunning[serviceId] = false;
        _startErrors.TryRemove(serviceId, out _);
    }

    /// <summary>Call when a start or restart threw or timed out.</summary>
    public void RecordStartFailure(string serviceId, string message) => _startErrors[serviceId] = message;

    /// <summary>Feed every status that is about to be reported.</summary>
    public void Observe(ServiceStatus status)
    {
        if (status.State != ServiceState.Running) return;
        _sawRunning[status.Id] = true;
        _startErrors.TryRemove(status.Id, out _);
    }

    public string? StartError(string serviceId) => _startErrors.GetValueOrDefault(serviceId);

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

    // Config properties the plugins use for their listen ports; the same names
    // Program.cs hydrates from the ports.* settings at boot. Mailpit calls its
    // web UI port WebPort.
    private static readonly (string Property, string Key)[] PortProperties =
    [
        ("Port", "port"),
        ("HttpPort", "http"),
        ("WebPort", "http"),
        ("HttpsPort", "https"),
        ("SmtpPort", "smtp"),
    ];

    public static ServiceInfoDto Build(
        ServiceStatus status,
        IReadOnlyList<InstalledBinary> installed,
        ServiceRunTracker tracker,
        IServiceModule? module = null)
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

        var ports = module is null ? new Dictionary<string, int>() : ReadPorts(module);
        int? port = ports.TryGetValue("port", out var p) ? p
            : ports.TryGetValue("http", out p) ? p
            : ports.TryGetValue("smtp", out p) ? p
            : null;

        return new ServiceInfoDto(
            status.Id, status.DisplayName, status.State, status.Pid,
            status.CpuPercent, status.MemoryBytes, status.Uptime,
            isInstalled, version, app, tracker.CrashKind(status),
            status.Uptime is { } up ? (long)up.TotalSeconds : null,
            port, ports,
            status.State == ServiceState.Running ? null : tracker.StartError(status.Id));
    }

    /// <param name="modules">Same order as <paramref name="statuses"/>; supplies the ports.</param>
    public static ServiceInfoDto[] BuildAll(
        IEnumerable<ServiceStatus> statuses,
        IReadOnlyList<InstalledBinary> installed,
        ServiceRunTracker tracker,
        IReadOnlyList<IServiceModule>? modules = null)
        => statuses.Select((s, i) => Build(s, installed, tracker, modules?[i])).ToArray();

    /// <summary>
    /// Ports from the module's private <c>_config</c> (the object the ports.*
    /// settings are written into), falling back to properties on the module itself.
    /// </summary>
    internal static Dictionary<string, int> ReadPorts(IServiceModule module)
    {
        var ports = new Dictionary<string, int>();
        try
        {
            var config = module.GetType()
                .GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(module);
            foreach (var source in new[] { config, module })
            {
                if (source is null) continue;
                var type = source.GetType();
                foreach (var (property, key) in PortProperties)
                {
                    if (ports.ContainsKey(key)) continue;
                    if (type.GetProperty(property, BindingFlags.Instance | BindingFlags.Public)?.GetValue(source) is int value && value > 0)
                        ports[key] = value;
                }
            }
        }
        catch (Exception)
        {
            // Reflection into plugin types is best-effort; no ports beats a failed status call.
        }
        return ports;
    }
}
