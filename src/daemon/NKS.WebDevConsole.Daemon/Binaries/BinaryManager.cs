using Microsoft.Extensions.Logging;

using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Core.Services;

namespace NKS.WebDevConsole.Daemon.Binaries;

/// <summary>
/// Information about a binary that is installed locally on disk.
/// </summary>
public sealed record InstalledBinary(
    string App,
    string Version,
    string MajorMinor,
    string InstallPath,    // ~/.wdc/binaries/{app}/{version}/
    string? Executable     // resolved executable path or null if not yet found
);

/// <summary>
/// Manages local binary installations under ~/.wdc/binaries/{app}/{version}/.
/// Coordinates downloads via BinaryDownloader and provides discovery for plugins.
/// </summary>
public sealed class BinaryManager
{
    private readonly BinaryDownloader _downloader;
    private readonly CatalogClient _catalog;
    private readonly ILogger<BinaryManager> _logger;
    private readonly IBinaryInstalledEventBus? _eventBus;
    private readonly string _root;

    // Back-compat constructor keeps older call sites (e.g. tests that
    // new-up a BinaryManager directly) working without plumbing the bus
    // through. Production DI always binds the three-arg overload.
    public BinaryManager(BinaryDownloader downloader, CatalogClient catalog, ILogger<BinaryManager> logger)
        : this(downloader, catalog, logger, eventBus: null) { }

    public BinaryManager(
        BinaryDownloader downloader,
        CatalogClient catalog,
        ILogger<BinaryManager> logger,
        IBinaryInstalledEventBus? eventBus)
    {
        _downloader = downloader;
        _catalog = catalog;
        _logger = logger;
        _eventBus = eventBus;
        _root = WdcPaths.BinariesRoot;
        Directory.CreateDirectory(_root);
    }

    public string Root => _root;

    /// <summary>
    /// Validates app/version strings so that they cannot traverse out of the binaries
    /// root. Without this, a crafted POST /api/binaries/install body with
    /// <c>app="../.."</c> could cause extraction into unexpected locations.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex _safeIdent =
        new(@"^[a-zA-Z0-9][a-zA-Z0-9_.\-]{0,63}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static void ValidateAppVersion(string app, string version)
    {
        if (string.IsNullOrWhiteSpace(app) || !_safeIdent.IsMatch(app))
            throw new ArgumentException($"Invalid app identifier: '{app}'");
        if (string.IsNullOrWhiteSpace(version) || !_safeIdent.IsMatch(version))
            throw new ArgumentException($"Invalid version identifier: '{version}'");
    }

    /// <summary>Path to the install directory for a given app/version (may not exist yet).</summary>
    public string GetInstallPath(string app, string version)
    {
        ValidateAppVersion(app, version);
        var combined = Path.GetFullPath(Path.Combine(_root, app.ToLowerInvariant(), version));
        var rootFull = Path.GetFullPath(_root);
        if (!combined.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Resolved install path escapes binaries root");
        return combined;
    }

    /// <summary>Returns true if the binary for app/version is already extracted on disk.</summary>
    public bool IsInstalled(string app, string version)
    {
        var dir = GetInstallPath(app, version);
        return Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any();
    }

    /// <summary>
    /// List everything we have under ~/.wdc/binaries/.
    /// Scans the directory layout — does not require the catalog.
    /// Skips internal-state directories (anything starting with '.', and
    /// "downloads" / ".cache" caches).
    /// </summary>
    public IReadOnlyList<InstalledBinary> ListInstalled()
    {
        var result = new List<InstalledBinary>();
        if (!Directory.Exists(_root)) return result;

        foreach (var appDir in Directory.GetDirectories(_root))
        {
            var app = Path.GetFileName(appDir);
            if (app.StartsWith('.') || app.Equals("downloads", StringComparison.OrdinalIgnoreCase))
                continue;

            // Sort per-app version directories newest-first so FirstOrDefault()
            // callers (Program.cs auto-detection paths for MySQL/PHP/Redis/etc.)
            // get the latest installed version instead of whatever filesystem
            // order alphabetises to. Without this, a user with both
            // mysql/5.7.44 and mysql/8.4.0 installed would have the config
            // defaulted to the ancient 5.7 every time.
            var versionDirs = Directory.GetDirectories(appDir)
                .Where(d =>
                {
                    var v = Path.GetFileName(d);
                    return !v.StartsWith('.') && !v.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
                })
                .OrderByDescending(d => Path.GetFileName(d), SemverVersionComparer.Instance);

            foreach (var versionDir in versionDirs)
            {
                var version = Path.GetFileName(versionDir);
                var majorMinor = string.Join('.', version.Split('.').Take(2));
                result.Add(new InstalledBinary(app, version, majorMinor, versionDir, ResolveExecutable(app, versionDir)));
            }
        }
        return result;
    }

    /// <summary>
    /// List installed versions for a specific app. Returns versions in
    /// newest-first order so <c>FirstOrDefault()</c> picks the latest
    /// semver-ordered install (see <see cref="ListInstalled()"/>).
    /// </summary>
    public IReadOnlyList<InstalledBinary> ListInstalled(string app)
        => ListInstalled().Where(b => b.App.Equals(app, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>
    /// Ensure the requested binary is downloaded and extracted. Returns the install path.
    /// Idempotent: if already installed, just returns the path.
    /// </summary>
    public async Task<InstalledBinary> EnsureInstalledAsync(
        string app,
        string version,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var installPath = GetInstallPath(app, version);
        if (IsInstalled(app, version))
        {
            _logger.LogInformation("{App} {Version} already installed at {Path}", app, version, installPath);
            return ToInstalled(app, version, installPath);
        }

        var os = OperatingSystem.IsWindows() ? "windows"
               : OperatingSystem.IsLinux() ? "linux"
               : OperatingSystem.IsMacOS() ? "macos"
               : "windows";
        var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            System.Runtime.InteropServices.Architecture.X64 => "x64",
            System.Runtime.InteropServices.Architecture.X86 => "x86",
            _ => "x64",
        };
        var release = _catalog.Find(app, version, os, arch)
            ?? throw new InvalidOperationException(
                $"No catalog entry for {app} {version} ({os}/{arch}). Available: {string.Join(", ", _catalog.ForApp(app).Select(r => r.Version + " " + r.Os + "/" + r.Arch))}");

        var cacheDir = Path.Combine(_root, ".cache");
        Directory.CreateDirectory(cacheDir);

        var archive = await _downloader.DownloadAsync(release, cacheDir, progress, ct);

        // Extract to a temp dir then move into place — avoids partial extracts
        var tempExtract = installPath + ".tmp";
        if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, recursive: true);
        await _downloader.ExtractAsync(archive, tempExtract, ct, release.ArchiveType);

        // Some archives wrap their content in a single top-level directory (e.g. mysql-8.4.8-winx64/...,
        // Apache24/...). Flatten if there's exactly one *significant* top-level directory — ignoring
        // readme/info files that ship alongside (e.g. Apache Lounge zips contain "-- Win64 VS18 --" and "ReadMe.txt").
        var allEntries = Directory.GetFileSystemEntries(tempExtract);
        var dirEntries = allEntries.Where(Directory.Exists).ToList();
        if (dirEntries.Count == 1)
        {
            var inner = dirEntries[0];
            if (Directory.Exists(installPath)) Directory.Delete(installPath, recursive: true);
            Directory.Move(inner, installPath);
            // Move any leftover top-level files (readmes, info markers) into the install dir for reference
            foreach (var leftover in allEntries.Where(File.Exists))
            {
                try { File.Move(leftover, Path.Combine(installPath, Path.GetFileName(leftover)), overwrite: true); }
                catch { /* ignore */ }
            }
            Directory.Delete(tempExtract, recursive: true);
        }
        else
        {
            if (Directory.Exists(installPath)) Directory.Delete(installPath, recursive: true);
            Directory.Move(tempExtract, installPath);
        }

        _logger.LogInformation("Installed {App} {Version} at {Path}", app, version, installPath);

        // Post-install steps for specific apps
        await PostInstallAsync(app, version, installPath, cacheDir, ct);

        // Notify subscribed plugin modules (Apache/MySQL/MariaDB/PHP/Redis)
        // so they can re-run their detection pass now that the binary exists
        // on disk. Replaces the lazy-init snippet each plugin used to have
        // inside StartAsync (task #9). Fire-and-forget — the bus dispatches
        // handlers on a background thread so /api/binaries/install still
        // returns promptly.
        _eventBus?.Publish(new BinaryInstalledEvent(
            App: app.ToLowerInvariant(),
            Version: version,
            InstallPath: installPath));

        return ToInstalled(app, version, installPath);
    }

    /// <summary>
    /// App-specific actions that have to run AFTER the base archive is extracted.
    /// For Apache on Windows we fetch mod_fcgid (an external Apache Lounge module) into modules/.
    /// </summary>
    private async Task PostInstallAsync(string app, string version, string installPath, string cacheDir, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return;

        if (app.Equals("apache", StringComparison.OrdinalIgnoreCase))
        {
            await EnsureModFcgidAsync(installPath, cacheDir, ct);
        }
    }

    private async Task EnsureModFcgidAsync(string apacheInstallPath, string cacheDir, CancellationToken ct)
    {
        var modulesDir = Path.Combine(apacheInstallPath, "modules");
        var modFcgid = Path.Combine(modulesDir, "mod_fcgid.so");
        if (File.Exists(modFcgid))
        {
            _logger.LogDebug("mod_fcgid already present at {Path}", modFcgid);
            return;
        }

        // mod_fcgid is an external Apache Lounge module — single .so/.dll bundled in a zip.
        // Hardcoded URL because the catalog API only tracks "primary" releases.
        var release = new BinaryRelease(
            App: "mod_fcgid",
            Version: "2.3.10",
            MajorMinor: "2.3",
            Url: "https://www.apachelounge.com/download/VS18/modules/mod_fcgid-2.3.10-win64-VS18.zip",
            Os: "windows",
            Arch: "x64",
            ArchiveType: "zip",
            Source: "apachelounge",
            UserAgent: "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36"
        );

        try
        {
            _logger.LogInformation("Fetching mod_fcgid (external Apache module) for Apache at {Path}", apacheInstallPath);
            var archive = await _downloader.DownloadAsync(release, cacheDir, progress: null, ct: ct);
            var tempExtract = Path.Combine(cacheDir, "mod_fcgid_extract");
            if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, recursive: true);
            await _downloader.ExtractAsync(archive, tempExtract, ct);

            // Find mod_fcgid.so anywhere inside the extracted tree and copy into modules/
            var so = Directory.GetFiles(tempExtract, "mod_fcgid.so", SearchOption.AllDirectories).FirstOrDefault();
            if (so is null)
            {
                _logger.LogWarning("mod_fcgid.so not found in extracted archive");
            }
            else
            {
                Directory.CreateDirectory(modulesDir);
                File.Copy(so, modFcgid, overwrite: true);
                _logger.LogInformation("mod_fcgid installed at {Path}", modFcgid);
            }
            Directory.Delete(tempExtract, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("mod_fcgid install failed (Apache will fall back to mod_proxy_fcgi): {Error}", ex.Message);
        }
    }

    /// <summary>Remove an installed binary from disk.</summary>
    public void Uninstall(string app, string version)
    {
        var dir = GetInstallPath(app, version);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
            _logger.LogInformation("Uninstalled {App} {Version}", app, version);
        }
    }

    private InstalledBinary ToInstalled(string app, string version, string installPath)
    {
        var majorMinor = string.Join('.', version.Split('.').Take(2));
        return new InstalledBinary(app, version, majorMinor, installPath, ResolveExecutable(app, installPath));
    }

    /// <summary>Best-effort resolution of the primary executable for each known app.</summary>
    private static string? ResolveExecutable(string app, string installDir)
    {
        var (subdir, exe) = app.ToLowerInvariant() switch
        {
            "apache" => ("bin", "httpd.exe"),
            "php" => ("", "php.exe"),
            "mysql" => ("bin", "mysqld.exe"),
            "mariadb" => ("bin", "mysqld.exe"),
            "nginx" => ("", "nginx.exe"),
            "redis" => ("", "redis-server.exe"),
            "postgresql" => ("bin", "postgres.exe"),
            "mongodb" => ("bin", "mongod.exe"),
            "memcached" => ("", "memcached.exe"),
            "mailpit" => ("", "mailpit.exe"),
            "caddy" => ("", "caddy.exe"),
            "cloudflared" => ("", "cloudflared.exe"),
            _ => ("", "")
        };

        if (string.IsNullOrEmpty(exe)) return null;
        var path = Path.Combine(installDir, subdir, exe);
        if (File.Exists(path)) return path;

        // Some apps use a top-level subdirectory we couldn't flatten (e.g. nginx-1.29.8/nginx.exe)
        // — best-effort search 1 level deep.
        if (Directory.Exists(installDir))
        {
            foreach (var sub in Directory.GetDirectories(installDir))
            {
                var alt = Path.Combine(sub, subdir, exe);
                if (File.Exists(alt)) return alt;
                var alt2 = Path.Combine(sub, exe);
                if (File.Exists(alt2)) return alt2;
            }
        }

        return null;
    }
}
