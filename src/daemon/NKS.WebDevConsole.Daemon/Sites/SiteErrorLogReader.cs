using NKS.WebDevConsole.Core.Services;

namespace NKS.WebDevConsole.Daemon.Sites;

/// <summary>
/// Reads the error log entries shown for one site: its own Apache error log in every installed Apache
/// version, plus the PHP logs in the PHP plugin's log directory.
/// </summary>
internal static class SiteErrorLogReader
{
    /// <summary>
    /// A site whose vhost writes to <c>{domain}-error.log</c> / <c>{domain}-ssl-error.log</c> gets an empty
    /// list until that file exists, never the global <c>error.log</c> (it holds the lines of every other
    /// site). The global log and <paramref name="globalApacheLog"/> are only for a vhost that defines no
    /// log of its own. Newest first, at most <paramref name="limit"/> entries.
    /// </summary>
    public static IReadOnlyList<ErrorLogInspector.LogEntry> Read(
        string domain,
        IEnumerable<string> apacheInstallPaths,
        string globalApacheLog,
        string phpLogDir,
        int limit,
        DateTimeOffset? since)
    {
        var apacheLogs = apacheInstallPaths
            .SelectMany(install => ApacheSiteErrorLogs.Resolve(domain, install, globalApacheLog))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        // PHP-FPM's own log (php{versionTag}-fpm-error.log) and the per-version web error log
        // (php{majorMinor}-errors.log): every version directory is scanned rather than listing versions.
        var phpFpm = new List<string>();
        var phpWeb = new List<string>();
        if (Directory.Exists(phpLogDir))
        {
            phpFpm.AddRange(Directory.EnumerateFiles(phpLogDir, "*-fpm-error.log"));
            phpWeb.AddRange(Directory.EnumerateFiles(phpLogDir, "php*-errors.log"));
        }

        // One source per file: Tail reads only the first existing path of a source.
        var sources = apacheLogs
            .Select(path => (Paths: (IEnumerable<string>)[path], Source: "apache-error"))
            .ToList();
        sources.Add((phpFpm, "php-fpm-error"));
        sources.Add((phpWeb, "php-error"));
        return ErrorLogInspector.TailMultiple(sources, limit, since);
    }
}
