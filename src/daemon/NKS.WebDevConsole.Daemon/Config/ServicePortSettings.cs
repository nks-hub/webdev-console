using System.Globalization;

namespace NKS.WebDevConsole.Daemon.Config;

/// <summary>
/// Which <c>ports.*</c> settings feed which property of a service module's
/// private <c>_config</c>. Boot hydration (Program.cs) and the PUT
/// /api/settings hook share this table, so a value saved in Settings and the
/// same value read at start-up land on the same property.
/// </summary>
internal static class ServicePortSettings
{
    private static readonly (string Key, string Property)[] WebServer =
        [("ports.http", "HttpPort"), ("ports.https", "HttpsPort")];

    /// <summary>Settings key → config property for one service module.</summary>
    public static IReadOnlyList<(string Key, string Property)> For(string moduleId) => moduleId.ToLowerInvariant() switch
    {
        // PHP-FPM listens on base + major*10 + minor per PHP version, and
        // Apache writes that address into every vhost. Only PHP-FPM on macOS
        // and Linux uses it; on Windows sites run through mod_fcgid.
        "apache" => [.. WebServer, ("ports.phpFpmBase", "PhpFpmBasePort")],
        "caddy" or "nginx" => WebServer,
        // php.ini sends mail() to Mailpit's SMTP port.
        "php" => [("ports.phpFpmBase", "PhpFpmBasePort"), ("ports.mailpitSmtp", "MailSmtpPort")],
        "mysql" => [("ports.mysql", "Port")],
        "mariadb" => [("ports.mariadb", "Port")],
        "postgresql" => [("ports.postgresql", "Port")],
        "redis" => [("ports.redis", "Port")],
        "mailpit" => [("ports.mailpitSmtp", "SmtpPort"), ("ports.mailpitHttp", "WebPort")],
        _ => [],
    };

    /// <summary>True for keys whose value is written into each site's vhost file.</summary>
    public static bool ChangesVhosts(string key) =>
        key.Equals("ports.http", StringComparison.OrdinalIgnoreCase)
        || key.Equals("ports.https", StringComparison.OrdinalIgnoreCase)
        || key.Equals("ports.phpFpmBase", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Sets the module config's int properties from <paramref name="read"/>,
    /// which returns null for keys to leave alone. Values that are not a
    /// positive integer and properties an older plugin build lacks are skipped.
    /// Returns the properties that were set.
    /// </summary>
    public static List<string> Apply(object config, string moduleId, Func<string, string?> read)
    {
        var applied = new List<string>();
        foreach (var (key, property) in For(moduleId))
        {
            if (!int.TryParse(read(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) || port <= 0)
                continue;
            var prop = config.GetType().GetProperty(property);
            if (prop?.CanWrite != true || prop.PropertyType != typeof(int)) continue;
            prop.SetValue(config, port);
            applied.Add(property);
        }
        return applied;
    }
}
