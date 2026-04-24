using NKS.WebDevConsole.Daemon.Config;

namespace NKS.WebDevConsole.Daemon.Services;

public sealed record ServiceConfigFile(string Name, string Path, string Content);

public sealed class ServiceConfigManager
{
    private readonly AtomicWriter _writer;
    private readonly string _wdcRoot;

    public ServiceConfigManager(AtomicWriter writer, string? wdcRoot = null)
    {
        _writer = writer;
        _wdcRoot = Path.GetFullPath(wdcRoot ?? NKS.WebDevConsole.Core.Services.WdcPaths.Root);
    }

    public async Task<IReadOnlyList<ServiceConfigFile>> GetFilesAsync(string serviceId)
    {
        serviceId = CanonicalizeServiceId(serviceId);
        var files = new List<ServiceConfigFile>();

        if (serviceId.Equals("apache", StringComparison.OrdinalIgnoreCase))
        {
            var apacheRoot = FindLatestVersionDir(Path.Combine(_wdcRoot, "binaries", "apache"));
            if (apacheRoot is null) return files;

            var httpdConf = Path.Combine(apacheRoot, "conf", "httpd.conf");
            await AddIfExistsAsync(files, "httpd.conf", httpdConf);

            var vhostsDir = Path.Combine(apacheRoot, "conf", "sites-enabled");
            if (Directory.Exists(vhostsDir))
            {
                foreach (var path in Directory.GetFiles(vhostsDir, "*.conf").OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                    await AddIfExistsAsync(files, Path.GetFileName(path), path);
            }

            return files;
        }

        if (serviceId.Equals("php", StringComparison.OrdinalIgnoreCase))
        {
            var phpRoot = Path.Combine(_wdcRoot, "binaries", "php");
            if (!Directory.Exists(phpRoot)) return files;

            foreach (var versionDir in Directory.GetDirectories(phpRoot)
                         .Where(d => !Path.GetFileName(d).StartsWith('.'))
                         .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                // PhpIniManager writes the file next to the executable
                // (<version>/bin/php.ini on FHS source builds,
                // <version>/php.ini on Windows flat extracts). Check
                // both — whichever exists wins. Falls back to the
                // profile-based copy under ~/.wdc/data/php/<mm>/php.ini
                // so the user at least sees *a* config even if neither
                // binary-side copy has landed yet.
                foreach (var iniPath in new[]
                {
                    Path.Combine(versionDir, "bin", "php.ini"),
                    Path.Combine(versionDir, "php.ini"),
                })
                {
                    if (File.Exists(iniPath))
                    {
                        await AddIfExistsAsync(files, $"php.ini ({Path.GetFileName(versionDir)})", iniPath);
                        break;
                    }
                }
            }

            // Expose profile-based php.ini variants too — these are the
            // files PhpIniManager rewrites when the user saves profile
            // changes and are the authoritative copy for FPM workers.
            var profileRoot = Path.Combine(_wdcRoot, "data", "php");
            if (Directory.Exists(profileRoot))
            {
                foreach (var mmDir in Directory.GetDirectories(profileRoot))
                {
                    foreach (var name in new[] { "php.ini", "php-cli.ini" })
                    {
                        var p = Path.Combine(mmDir, name);
                        await AddIfExistsAsync(files, $"{name} ({Path.GetFileName(mmDir)} profile)", p);
                    }
                }
            }

            return files;
        }

        // MySQL and MariaDB each keep their own `data/<engine>/my.ini`
        // plus optional binary-adjacent `my.ini`/`my.cnf`. Historically the
        // two shared the same path because MariaDB is a drop-in MySQL
        // replacement, but with the v1.0.6 MySQL plugin defaulting to port
        // 3307 (MariaDB stays on 3306) they run side-by-side with DIFFERENT
        // configs, so each needs its own engine-specific directory.
        if (serviceId.Equals("mysql", StringComparison.OrdinalIgnoreCase)
            || serviceId.Equals("mariadb", StringComparison.OrdinalIgnoreCase))
        {
            var engineDir = serviceId.ToLowerInvariant();
            var candidates = new List<string>
            {
                Path.Combine(_wdcRoot, "data", engineDir, "my.ini"),
                Path.Combine(_wdcRoot, "data", engineDir, "my.cnf"),
            };
            var binRoot = Path.Combine(_wdcRoot, "binaries", engineDir);
            if (Directory.Exists(binRoot))
            {
                foreach (var versionDir in Directory.GetDirectories(binRoot)
                             .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                {
                    candidates.Add(Path.Combine(versionDir, "my.ini"));
                    candidates.Add(Path.Combine(versionDir, "my.cnf"));
                }
            }
            foreach (var candidate in candidates)
                await AddIfExistsAsync(files, Path.GetFileName(candidate), candidate);

            return files;
        }

        if (serviceId.Equals("redis", StringComparison.OrdinalIgnoreCase))
        {
            var redisRoot = FindLatestVersionDir(Path.Combine(_wdcRoot, "binaries", "redis"));
            if (redisRoot is null) return files;
            foreach (var name in new[] { "redis.conf", "sentinel.conf" })
                await AddIfExistsAsync(files, name, Path.Combine(redisRoot, name));
            return files;
        }

        if (serviceId.Equals("caddy", StringComparison.OrdinalIgnoreCase))
        {
            // Caddy reads a Caddyfile at the daemon-managed location when the
            // plugin is installed; falls back to one next to the binary.
            var caddyfileCandidates = new List<string>
            {
                Path.Combine(_wdcRoot, "caddy", "Caddyfile"),
            };
            var caddyBinRoot = Path.Combine(_wdcRoot, "binaries", "caddy");
            if (Directory.Exists(caddyBinRoot))
            {
                foreach (var versionDir in Directory.GetDirectories(caddyBinRoot)
                             .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                {
                    caddyfileCandidates.Add(Path.Combine(versionDir, "Caddyfile"));
                }
            }
            foreach (var candidate in caddyfileCandidates)
                await AddIfExistsAsync(files, Path.GetFileName(candidate), candidate);
            return files;
        }

        // Mailpit, Node.js, Cloudflared: these services configure themselves
        // via CLI args, per-site TOML, or environment variables — there is
        // no single config file to edit. Return an empty list; the frontend
        // shows a friendly "This service uses CLI-only configuration" hint.

        return files;
    }

    public bool TryNormalizeManagedPath(string serviceId, string configPath, out string normalizedPath, out string error)
    {
        serviceId = CanonicalizeServiceId(serviceId);
        normalizedPath = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(configPath))
        {
            error = "configPath is required";
            return false;
        }

        string candidate;
        try
        {
            candidate = Path.GetFullPath(configPath);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        foreach (var allowedPath in EnumerateAllowedPaths(serviceId))
        {
            if (string.Equals(candidate, allowedPath, StringComparison.OrdinalIgnoreCase))
            {
                normalizedPath = candidate;
                return true;
            }
        }

        error = $"Config path '{configPath}' is not a managed {serviceId} config file.";
        return false;
    }

    public async Task SaveAsync(string serviceId, string configPath, string content, CancellationToken ct = default)
    {
        serviceId = CanonicalizeServiceId(serviceId);
        if (!TryNormalizeManagedPath(serviceId, configPath, out var normalizedPath, out var error))
            throw new InvalidOperationException(error);

        ct.ThrowIfCancellationRequested();
        await _writer.WriteAsync(normalizedPath, content);
    }

    public async Task<string> WriteDraftAsync(string serviceId, string configPath, string content, CancellationToken ct = default)
    {
        serviceId = CanonicalizeServiceId(serviceId);
        if (!TryNormalizeManagedPath(serviceId, configPath, out var normalizedPath, out var error))
            throw new InvalidOperationException(error);

        var tempPath = normalizedPath + $".validate-{Guid.NewGuid():N}.tmp";
        ct.ThrowIfCancellationRequested();
        await File.WriteAllTextAsync(tempPath, content, ct);
        return tempPath;
    }

    private IEnumerable<string> EnumerateAllowedPaths(string serviceId)
    {
        serviceId = CanonicalizeServiceId(serviceId);
        if (serviceId.Equals("apache", StringComparison.OrdinalIgnoreCase))
        {
            var apacheRoot = FindLatestVersionDir(Path.Combine(_wdcRoot, "binaries", "apache"));
            if (apacheRoot is null) yield break;

            var httpdConf = Path.Combine(apacheRoot, "conf", "httpd.conf");
            if (File.Exists(httpdConf))
                yield return Path.GetFullPath(httpdConf);

            var vhostsDir = Path.Combine(apacheRoot, "conf", "sites-enabled");
            if (Directory.Exists(vhostsDir))
            {
                foreach (var path in Directory.GetFiles(vhostsDir, "*.conf"))
                    yield return Path.GetFullPath(path);
            }

            yield break;
        }

        if (serviceId.Equals("php", StringComparison.OrdinalIgnoreCase))
        {
            var phpRoot = Path.Combine(_wdcRoot, "binaries", "php");
            if (!Directory.Exists(phpRoot)) yield break;

            foreach (var versionDir in Directory.GetDirectories(phpRoot).Where(d => !Path.GetFileName(d).StartsWith('.')))
            {
                var iniPath = Path.Combine(versionDir, "php.ini");
                if (File.Exists(iniPath))
                    yield return Path.GetFullPath(iniPath);
            }

            yield break;
        }

        if (serviceId.Equals("mysql", StringComparison.OrdinalIgnoreCase)
            || serviceId.Equals("mariadb", StringComparison.OrdinalIgnoreCase))
        {
            var engineDir = serviceId.ToLowerInvariant();
            var dataMyIni = Path.Combine(_wdcRoot, "data", engineDir, "my.ini");
            if (File.Exists(dataMyIni))
                yield return Path.GetFullPath(dataMyIni);
            var dataMyCnf = Path.Combine(_wdcRoot, "data", engineDir, "my.cnf");
            if (File.Exists(dataMyCnf))
                yield return Path.GetFullPath(dataMyCnf);
            var binRoot = Path.Combine(_wdcRoot, "binaries", engineDir);
            if (Directory.Exists(binRoot))
            {
                foreach (var versionDir in Directory.GetDirectories(binRoot))
                {
                    foreach (var name in new[] { "my.ini", "my.cnf" })
                    {
                        var path = Path.Combine(versionDir, name);
                        if (File.Exists(path)) yield return Path.GetFullPath(path);
                    }
                }
            }
            yield break;
        }

        if (serviceId.Equals("redis", StringComparison.OrdinalIgnoreCase))
        {
            var redisRoot = Path.Combine(_wdcRoot, "binaries", "redis");
            if (!Directory.Exists(redisRoot)) yield break;
            foreach (var versionDir in Directory.GetDirectories(redisRoot))
            {
                foreach (var name in new[] { "redis.conf", "sentinel.conf" })
                {
                    var path = Path.Combine(versionDir, name);
                    if (File.Exists(path)) yield return Path.GetFullPath(path);
                }
            }
            yield break;
        }

        if (serviceId.Equals("caddy", StringComparison.OrdinalIgnoreCase))
        {
            var managed = Path.Combine(_wdcRoot, "caddy", "Caddyfile");
            if (File.Exists(managed)) yield return Path.GetFullPath(managed);
            var caddyBinRoot = Path.Combine(_wdcRoot, "binaries", "caddy");
            if (Directory.Exists(caddyBinRoot))
            {
                foreach (var versionDir in Directory.GetDirectories(caddyBinRoot))
                {
                    var path = Path.Combine(versionDir, "Caddyfile");
                    if (File.Exists(path)) yield return Path.GetFullPath(path);
                }
            }
        }
    }

    private static string? FindLatestVersionDir(string root)
    {
        if (!Directory.Exists(root)) return null;

        return Directory.GetDirectories(root)
            .Where(d => !Path.GetFileName(d).StartsWith('.'))
            .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static async Task AddIfExistsAsync(ICollection<ServiceConfigFile> files, string name, string path)
    {
        if (!File.Exists(path)) return;
        files.Add(new ServiceConfigFile(name, path, await File.ReadAllTextAsync(path)));
    }

    private static string CanonicalizeServiceId(string serviceId) =>
        serviceId.ToLowerInvariant() switch
        {
            "httpd" => "apache",
            // NOTE: mariadb was previously aliased to mysql here because the
            // two shared the same my.ini. Removed when the MySQL plugin
            // started defaulting to port 3307 — each now keeps a separate
            // data/<engine>/my.ini, so aliasing made /api/services/mariadb/
            // config return MySQL's config in the UI.
            _ => serviceId,
        };
}
