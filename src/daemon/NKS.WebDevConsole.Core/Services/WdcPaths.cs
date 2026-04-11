namespace NKS.WebDevConsole.Core.Services;

/// <summary>
/// Centralised resolution of the NKS WDC data root. All services (daemon,
/// plugins, backup, telemetry) MUST go through this helper instead of
/// hardcoding <c>Environment.GetFolderPath(SpecialFolder.UserProfile) + ".wdc"</c>
/// so that portable-mode installs can redirect the entire tree via the
/// <c>WDC_DATA_DIR</c> environment variable.
///
/// Portable mode flow:
///   1. Electron main detects a <c>portable.txt</c> file next to the app
///      executable and sets <c>WDC_DATA_DIR</c> before spawning the daemon.
///   2. This class reads the env var once at process start — first non-empty
///      write wins, subsequent env-var changes are ignored for stability.
///   3. If the env var is unset, falls back to <c>~/.wdc</c> (standard install).
///
/// Because the Core assembly is in <c>PluginLoadContext.SharedAssemblies</c>,
/// plugins loaded into isolated ALCs resolve the same WdcPaths type and
/// observe the same Root value — no cross-ALC duplication.
/// </summary>
public static class WdcPaths
{
    private static readonly Lazy<string> _root = new(ResolveRoot, isThreadSafe: true);

    /// <summary>Absolute path of the NKS WDC data root directory.</summary>
    public static string Root => _root.Value;

    /// <summary><c>{Root}/binaries</c> — managed service binaries.</summary>
    public static string BinariesRoot => Path.Combine(Root, "binaries");

    /// <summary><c>{Root}/data</c> — runtime state, service data files.</summary>
    public static string DataRoot => Path.Combine(Root, "data");

    /// <summary><c>{Root}/sites</c> — per-site TOML configuration files.</summary>
    public static string SitesRoot => Path.Combine(Root, "sites");

    /// <summary><c>{Root}/generated</c> — SiteManager-owned vhost copies + history.</summary>
    public static string GeneratedRoot => Path.Combine(Root, "generated");

    /// <summary><c>{Root}/ssl</c> — mkcert CA and per-site certs.</summary>
    public static string SslRoot => Path.Combine(Root, "ssl");

    /// <summary><c>{Root}/logs</c> — aggregated service logs.</summary>
    public static string LogsRoot => Path.Combine(Root, "logs");

    /// <summary><c>{Root}/cache</c> — download caches, plugin install staging.</summary>
    public static string CacheRoot => Path.Combine(Root, "cache");

    /// <summary><c>{Root}/backups</c> — default target for <c>wdc backup</c>.</summary>
    public static string BackupsRoot => Path.Combine(Root, "backups");

    /// <summary><c>{Root}/caddy</c> — Caddyfile fragments for the Caddy plugin.</summary>
    public static string CaddyRoot => Path.Combine(Root, "caddy");

    /// <summary><c>{Root}/cloudflare</c> — Cloudflare plugin config + cloudflared state.</summary>
    public static string CloudflareRoot => Path.Combine(Root, "cloudflare");

    /// <summary>
    /// Returns true when the data root was resolved from <c>WDC_DATA_DIR</c>
    /// (portable mode), false when it falls back to <c>~/.wdc</c>.
    /// </summary>
    public static bool IsPortableMode { get; private set; }

    private static string ResolveRoot()
    {
        var envOverride = Environment.GetEnvironmentVariable("WDC_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(envOverride))
        {
            try
            {
                var full = Path.GetFullPath(envOverride);
                Directory.CreateDirectory(full);
                IsPortableMode = true;
                return full;
            }
            catch
            {
                // Fall through to default if the env-var path is unwritable.
            }
        }

        IsPortableMode = false;
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".wdc");
    }
}
