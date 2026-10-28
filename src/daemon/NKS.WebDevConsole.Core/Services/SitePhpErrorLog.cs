using System.Text.RegularExpressions;

namespace NKS.WebDevConsole.Core.Services;

/// <summary>
/// The PHP error log of one site. Without anything else PHP writes to the log of its version
/// (<c>php8.5-errors.log</c>), which every site on that version shares. A site gets its own
/// <c>{domain}-errors.log</c> when the PHP process serving it is pointed at an ini that sets
/// <c>error_log</c>: with mod_fcgid through <c>FcgidInitialEnv PHPRC</c> (the per-site php.ini) or
/// <c>FcgidInitialEnv PHP_INI_SCAN_DIR</c> (a directory of extra ini files next to it).
/// </summary>
public static class SitePhpErrorLog
{
    /// <summary>Name of the extra ini file in the scan directory.</summary>
    public const string ScanFileName = "10-wdc-site-log.ini";

    private static readonly Regex FcgidEnv = new(
        @"^[ \t]*FcgidInitialEnv[ \t]+(?<name>PHPRC|PHP_INI_SCAN_DIR)[ \t]+(?:""(?<value>[^""\r\n]+)""|(?<value>\S+))",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ErrorLogDirective = new(
        @"^[ \t]*error_log[ \t]*=[ \t]*(?:""(?<value>[^""\r\n]*)""|(?<value>[^;\r\n]*?))[ \t]*(?:;.*)?$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The directory of the PHP logs, <c>{logs}/php</c>: the PHP plugin writes the version logs there too.</summary>
    public static string LogDir => Path.Combine(WdcPaths.LogsRoot, "php");

    /// <summary>The site's own PHP log: <c>{logs}/php/{domain}-errors.log</c>.</summary>
    public static string LogPath(string domain) => Path.Combine(LogDir, $"{domain}-errors.log");

    /// <summary>Where the daemon keeps the per-site php.ini and the scan directory: <c>{root}/sites-php/{domain}</c>.</summary>
    public static string IniDir(string domain) => Path.Combine(WdcPaths.Root, "sites-php", domain);

    /// <summary>The ini lines that give a site its own PHP log (forward slashes, PHP reads them on every platform).</summary>
    public static string IniBlock(string logPath) =>
        $"error_log = \"{logPath.Replace('\\', '/')}\"\nlog_errors = 1\n";

    /// <summary>
    /// The site's own PHP log when the site's PHP really writes there, otherwise null (the version log applies).
    /// "Really" means a vhost in one of the Apache installs points PHP at the site's ini through
    /// <c>FcgidInitialEnv</c> and that ini sets <c>error_log</c> to the site's log, as its last word on it.
    /// An ini nobody points PHP at, or one written before the log was added, does not count.
    /// </summary>
    public static string? Resolve(string domain, IEnumerable<string> apacheInstallPaths) =>
        Resolve(domain, apacheInstallPaths, IniDir(domain), LogPath(domain));

    public static string? Resolve(string domain, IEnumerable<string> apacheInstallPaths, string iniDir, string logPath)
    {
        foreach (var install in apacheInstallPaths)
        {
            var vhost = ApacheSiteErrorLogs.ReadVhost(domain, install);
            if (vhost is null) continue;

            foreach (Match m in FcgidEnv.Matches(vhost))
            {
                var value = Normalize(m.Groups["value"].Value);
                var files = new List<string>();
                if (m.Groups["name"].Value.Equals("PHPRC", StringComparison.OrdinalIgnoreCase))
                {
                    // PHPRC is a php.ini or the directory that holds one; PHP accepts both.
                    if (value == Normalize(Path.Combine(iniDir, "php.ini")) || value == Normalize(iniDir))
                        files.Add(Path.Combine(iniDir, "php.ini"));
                }
                else if (value == Normalize(Path.Combine(iniDir, "conf.d")) && Directory.Exists(Path.Combine(iniDir, "conf.d")))
                {
                    files.AddRange(Directory.GetFiles(Path.Combine(iniDir, "conf.d"), "*.ini").Order(StringComparer.OrdinalIgnoreCase));
                }

                if (files.Any(f => SetsErrorLog(f, logPath))) return logPath;
            }
        }
        return null;
    }

    private static bool SetsErrorLog(string iniFile, string logPath)
    {
        string text;
        try { text = File.ReadAllText(iniFile); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }

        // PHP reads an ini from top to bottom and the last assignment wins.
        var last = ErrorLogDirective.Matches(text).LastOrDefault();
        return last is not null && Normalize(last.Groups["value"].Value) == Normalize(logPath);
    }

    private static string Normalize(string path) => path.Trim().Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
}
