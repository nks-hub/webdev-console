using System.Reflection;
using System.Text.Json.Serialization;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Daemon.Binaries;

namespace NKS.WebDevConsole.Daemon.Services;

/// <summary>
/// Service status as the API returns it: the module's own <see cref="ServiceStatus"/>
/// plus what the daemon knows around it — whether the binary is installed, which
/// version, the ports the module is configured for and the uptime in seconds.
/// <see cref="ServiceStatus"/> itself stays as is because plugins built against
/// older SDKs construct it positionally.
/// </summary>
internal sealed record ServiceStatusDto(
    string Id,
    string DisplayName,
    ServiceState State,
    int? Pid,
    double CpuPercent,
    long MemoryBytes,
    TimeSpan? Uptime,
    // Left out of the JSON when unknown, so the UI reads them as optional numbers/strings.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? UptimeSeconds,
    bool Installed,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Version,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Port,
    IReadOnlyDictionary<string, int> Ports)
{
    // Service id → app folder under ~/.wdc/binaries. A service missing here
    // (node, services added by third-party plugins) is reported as installed:
    // the daemon cannot tell where its binary lives.
    private static readonly Dictionary<string, string> BinaryApps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["apache"] = "apache",
        ["httpd"] = "apache",
        ["php"] = "php",
        ["mysql"] = "mysql",
        ["mariadb"] = "mariadb",
        ["postgresql"] = "postgresql",
        ["redis"] = "redis",
        ["mailpit"] = "mailpit",
        ["nginx"] = "nginx",
        ["caddy"] = "caddy",
        ["mongodb"] = "mongodb",
        ["memcached"] = "memcached",
        ["cloudflare"] = "cloudflared",
    };

    // Config properties the plugins use for their listen ports; the same names
    // Program.cs hydrates from the ports.* settings at boot.
    private static readonly (string Property, string Key)[] PortProperties =
    [
        ("Port", "port"),
        ("HttpPort", "http"),
        ("HttpsPort", "https"),
        ("SmtpPort", "smtp"),
    ];

    /// <summary>Binary folder name for a service id, or null when the daemon does not manage it.</summary>
    public static string? BinaryAppFor(string serviceId) =>
        BinaryApps.TryGetValue(serviceId, out var app) ? app : null;

    public static ServiceStatusDto From(IServiceModule module, ServiceStatus status, IReadOnlyList<InstalledBinary> installedBinaries)
    {
        var installed = true;
        string? version = null;
        if (BinaryAppFor(status.Id) is { } app)
        {
            // ListInstalled() is newest-first, the same version the modules pick.
            var binary = installedBinaries.FirstOrDefault(b =>
                b.App.Equals(app, StringComparison.OrdinalIgnoreCase) && b.Executable is not null);
            version = binary?.Version;
            // A running service is installed even when its binary lives elsewhere.
            installed = binary is not null || status.State == ServiceState.Running;
        }

        var ports = ReadPorts(module);
        int? port = ports.TryGetValue("port", out var p) ? p
            : ports.TryGetValue("http", out p) ? p
            : ports.TryGetValue("smtp", out p) ? p
            : null;

        return new ServiceStatusDto(
            status.Id, status.DisplayName, status.State, status.Pid,
            status.CpuPercent, status.MemoryBytes, status.Uptime,
            status.Uptime is { } up ? (long)up.TotalSeconds : null,
            installed, version, port, ports);
    }

    /// <summary>
    /// Ports from the module's private <c>_config</c> (the object the ports.*
    /// settings are written into), falling back to properties on the module itself.
    /// </summary>
    internal static IReadOnlyDictionary<string, int> ReadPorts(IServiceModule module)
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
