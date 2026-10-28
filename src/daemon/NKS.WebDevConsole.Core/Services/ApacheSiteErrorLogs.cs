using System.Text.RegularExpressions;

namespace NKS.WebDevConsole.Core.Services;

/// <summary>
/// Finds the Apache error log that belongs to one site. The Apache plugin gives every site its own
/// <c>ErrorLog</c> (<c>{domain}-error.log</c>, or <c>{domain}-ssl-error.log</c> for HTTPS); the global
/// <c>error.log</c> holds the lines of all sites and of Apache itself, so it is only a fallback for
/// a vhost that defines no log of its own.
/// </summary>
public static class ApacheSiteErrorLogs
{
    private static readonly Regex ErrorLogDirective = new(
        @"^[ \t]*ErrorLog[ \t]+(?:""(?<path>[^""\r\n]+)""|(?<path>\S+))",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Log files to read for <paramref name="domain"/> in the Apache install at <paramref name="installPath"/>.
    /// <list type="bullet">
    /// <item>The vhost defines a per-site <c>ErrorLog</c>: those files only. None of them existing yet means an empty log.</item>
    /// <item>The vhost does not exist yet: the files the Apache plugin names for the site, never the global log.</item>
    /// <item>The vhost exists without a per-site <c>ErrorLog</c>: the global <c>error.log</c>
    /// (and <paramref name="globalFallbackLog"/>, when given).</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<string> Resolve(string domain, string installPath, string? globalFallbackLog = null)
    {
        var logsDir = Path.Combine(installPath, "logs");
        var vhost = ReadVhost(domain, installPath);

        if (vhost is null)
            return [Path.Combine(logsDir, $"{domain}-ssl-error.log"), Path.Combine(logsDir, $"{domain}-error.log")];

        var perSite = PerSiteLogs(vhost, installPath, logsDir);
        if (perSite.Count > 0) return perSite;

        var global = new List<string> { Path.Combine(logsDir, "error.log") };
        if (!string.IsNullOrWhiteSpace(globalFallbackLog)) global.Add(globalFallbackLog);
        return global;
    }

    /// <summary>The <c>ErrorLog</c> files of a vhost that are not the global <c>error.log</c>, <c>${APACHE_LOG_DIR}</c> resolved.</summary>
    public static IReadOnlyList<string> PerSiteLogs(string vhostText, string installPath, string logsDir)
    {
        var paths = new List<string>();
        foreach (Match m in ErrorLogDirective.Matches(vhostText))
        {
            var raw = m.Groups["path"].Value.Replace("${APACHE_LOG_DIR}", logsDir, StringComparison.OrdinalIgnoreCase);
            // Anything still holding a variable cannot be located; a relative path is relative to the server root.
            if (raw.Contains("${", StringComparison.Ordinal)) continue;
            var path = Path.GetFullPath(Path.IsPathRooted(raw) ? raw : Path.Combine(installPath, raw));
            var name = Path.GetFileName(path);
            if (name.Equals("error.log", StringComparison.OrdinalIgnoreCase)
                || name.Equals("error_log", StringComparison.OrdinalIgnoreCase)) continue;
            if (!paths.Contains(path, StringComparer.OrdinalIgnoreCase)) paths.Add(path);
        }
        return paths;
    }

    /// <summary>
    /// The text of the vhost file of <paramref name="domain"/> in an Apache install, or null when there is none.
    /// The Apache plugin writes <c>{domain}.conf</c> into <c>conf/sites-enabled</c>; localhost has its own file names.
    /// </summary>
    public static string? ReadVhost(string domain, string installPath)
    {
        var dir = Path.Combine(installPath, "conf", "sites-enabled");
        var names = domain.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            ? new[] { "000-localhost.conf", "010-localhost.conf", "localhost.conf" }
            : new[] { $"{domain}.conf" };
        foreach (var name in names)
        {
            try
            {
                var file = Path.Combine(dir, name);
                if (File.Exists(file)) return File.ReadAllText(file);
            }
            catch (IOException) { /* locked or vanished: treat as not there */ }
            catch (UnauthorizedAccessException) { }
        }
        return null;
    }
}
