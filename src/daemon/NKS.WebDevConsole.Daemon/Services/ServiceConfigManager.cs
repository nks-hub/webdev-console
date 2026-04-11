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
                var iniPath = Path.Combine(versionDir, "php.ini");
                await AddIfExistsAsync(files, $"php.ini ({Path.GetFileName(versionDir)})", iniPath);
            }

            return files;
        }

        if (serviceId.Equals("mysql", StringComparison.OrdinalIgnoreCase))
        {
            var myIni = Path.Combine(_wdcRoot, "data", "mysql", "my.ini");
            await AddIfExistsAsync(files, "my.ini", myIni);
        }

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

        if (serviceId.Equals("mysql", StringComparison.OrdinalIgnoreCase))
        {
            var myIni = Path.Combine(_wdcRoot, "data", "mysql", "my.ini");
            if (File.Exists(myIni))
                yield return Path.GetFullPath(myIni);
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
            "mariadb" => "mysql",
            _ => serviceId,
        };
}
