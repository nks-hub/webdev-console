using Microsoft.Extensions.Logging;

using NKS.WebDevConsole.Core.Models;

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
    private readonly ILogger<BinaryManager> _logger;
    private readonly string _root;

    public BinaryManager(BinaryDownloader downloader, ILogger<BinaryManager> logger)
    {
        _downloader = downloader;
        _logger = logger;
        _root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".wdc", "binaries");
        Directory.CreateDirectory(_root);
    }

    public string Root => _root;

    /// <summary>Path to the install directory for a given app/version (may not exist yet).</summary>
    public string GetInstallPath(string app, string version)
        => Path.Combine(_root, app.ToLowerInvariant(), version);

    /// <summary>Returns true if the binary for app/version is already extracted on disk.</summary>
    public bool IsInstalled(string app, string version)
    {
        var dir = GetInstallPath(app, version);
        return Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any();
    }

    /// <summary>
    /// List everything we have under ~/.wdc/binaries/.
    /// Scans the directory layout — does not require the catalog.
    /// </summary>
    public IReadOnlyList<InstalledBinary> ListInstalled()
    {
        var result = new List<InstalledBinary>();
        if (!Directory.Exists(_root)) return result;

        foreach (var appDir in Directory.GetDirectories(_root))
        {
            var app = Path.GetFileName(appDir);
            foreach (var versionDir in Directory.GetDirectories(appDir))
            {
                var version = Path.GetFileName(versionDir);
                var majorMinor = string.Join('.', version.Split('.').Take(2));
                result.Add(new InstalledBinary(app, version, majorMinor, versionDir, ResolveExecutable(app, versionDir)));
            }
        }
        return result;
    }

    /// <summary>List installed versions for a specific app.</summary>
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

        var release = BinaryCatalog.Find(app, version)
            ?? throw new InvalidOperationException(
                $"No catalog entry for {app} {version}. Available: {string.Join(", ", BinaryCatalog.ForApp(app).Select(r => r.Version))}");

        var cacheDir = Path.Combine(_root, ".cache");
        Directory.CreateDirectory(cacheDir);

        var archive = await _downloader.DownloadAsync(release, cacheDir, progress, ct);

        // Extract to a temp dir then move into place — avoids partial extracts
        var tempExtract = installPath + ".tmp";
        if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, recursive: true);
        await _downloader.ExtractAsync(archive, tempExtract, ct);

        // Some archives wrap their content in a single top-level directory (e.g. mysql-8.4.8-winx64/...);
        // flatten if so.
        var entries = Directory.GetFileSystemEntries(tempExtract);
        if (entries.Length == 1 && Directory.Exists(entries[0]))
        {
            var inner = entries[0];
            if (Directory.Exists(installPath)) Directory.Delete(installPath, recursive: true);
            Directory.Move(inner, installPath);
            Directory.Delete(tempExtract, recursive: true);
        }
        else
        {
            if (Directory.Exists(installPath)) Directory.Delete(installPath, recursive: true);
            Directory.Move(tempExtract, installPath);
        }

        _logger.LogInformation("Installed {App} {Version} at {Path}", app, version, installPath);
        return ToInstalled(app, version, installPath);
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
