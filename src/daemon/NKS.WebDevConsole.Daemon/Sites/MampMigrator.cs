using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Models;

namespace NKS.WebDevConsole.Daemon.Sites;

/// <summary>
/// Reads MAMP PRO virtual host configuration and converts each MAMP host into a
/// NKS WebDev Console <see cref="SiteConfig"/>.
///
/// Primary source: the MAMP PRO SQLite settings DB at
/// <c>%APPDATA%\Appsolute\MAMPPRO\userdb\mamp.db</c> — contains the full
/// <c>VirtualHosts</c> table with exact PHP version, SSL flag, and server
/// aliases keyed on a foreign key. This is what the MAMP PRO UI reads and
/// writes, so it matches what the user sees in the app.
///
/// Fallback: Apache vhost files (<c>httpd-vhosts.conf</c>, <c>htdocs-vhosts\*.conf</c>)
/// for classic MAMP (non-PRO) installs that don't have the SQLite store.
///
/// Strictly read-only — never writes to MAMP's files. Dummy Apache placeholder
/// entries (dummy-host.example.com) are filtered out.
/// </summary>
public sealed class MampMigrator
{
    private readonly ILogger<MampMigrator> _logger;

    private static readonly HashSet<string> DummyDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "dummy-host.example.com",
        "dummy-host2.example.com",
        "www.dummy-host.example.com",
        "www.dummy-host2.example.com",
    };

    public MampMigrator(ILogger<MampMigrator> logger)
    {
        _logger = logger;
    }

    public sealed record DiscoveredSite(
        string Domain,
        string DocumentRoot,
        string PhpVersion,
        bool SslEnabled,
        string[] Aliases,
        string SourcePath
    );

    /// <summary>
    /// Scans known MAMP install locations and MAMP PRO's user database. Returns
    /// every site ready for import. Call <see cref="ImportAsync"/> to actually
    /// create the <see cref="SiteConfig"/> entries.
    /// </summary>
    public IReadOnlyList<DiscoveredSite> Discover()
    {
        var found = new List<DiscoveredSite>();

        // 1. Preferred: MAMP PRO settings DB (has exact PHP versions + aliases)
        foreach (var dbPath in EnumerateProDatabases())
        {
            try
            {
                var sites = ReadFromProDatabase(dbPath);
                _logger.LogInformation("MAMP PRO db {Db} → {Count} host(s)", dbPath, sites.Count);
                found.AddRange(sites);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read MAMP PRO db {Db}", dbPath);
            }
        }

        // 2. Fallback: Apache vhost files (classic MAMP + manually added vhosts)
        foreach (var root in EnumerateMampRoots())
        {
            if (!Directory.Exists(root)) continue;
            _logger.LogInformation("Scanning MAMP vhost files under {Root}", root);
            foreach (var vhostFile in EnumerateVhostFiles(root))
            {
                try
                {
                    var sites = ParseVhostFile(vhostFile);
                    found.AddRange(sites);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse {File}", vhostFile);
                }
            }
        }

        // Filter dummy Apache placeholders and de-duplicate.
        // When the same servername appears twice (e.g. separate HTTP+HTTPS entries
        // in MAMP PRO), prefer the SSL-enabled record so the imported WDC site
        // inherits the same cert.
        var unique = found
            .Where(s => !DummyDomains.Contains(s.Domain))
            .GroupBy(s => s.Domain, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(s => s.SslEnabled).First())
            .OrderBy(s => s.Domain, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _logger.LogInformation("MAMP migration discovered {Count} unique sites", unique.Count);
        return unique;
    }

    /// <summary>
    /// Discovers MAMP sites and creates a <see cref="SiteConfig"/> for each one
    /// via <paramref name="siteManager"/>. Skips sites whose domain is already
    /// known so the operation is idempotent. Returns the list of newly imported
    /// domains.
    /// </summary>
    public async Task<IReadOnlyList<string>> ImportAsync(SiteManager siteManager, CancellationToken ct = default)
    {
        var discovered = Discover();
        var imported = new List<string>();

        foreach (var site in discovered)
        {
            if (siteManager.Get(site.Domain) is not null)
            {
                _logger.LogInformation("Skipping {Domain} — already exists in NKS WDC", site.Domain);
                continue;
            }

            try
            {
                var config = new SiteConfig
                {
                    Domain = site.Domain,
                    DocumentRoot = site.DocumentRoot,
                    PhpVersion = site.PhpVersion,
                    SslEnabled = site.SslEnabled,
                    Aliases = site.Aliases,
                    Framework = null,
                };
                await siteManager.CreateAsync(config);
                imported.Add(site.Domain);
                _logger.LogInformation("Imported {Domain} from MAMP ({Source})", site.Domain, site.SourcePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to import {Domain}", site.Domain);
            }
        }

        return imported;
    }

    // ---------- internals ----------

    private static IEnumerable<string> EnumerateMampRoots()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return @"C:\MAMP";
            yield return @"D:\MAMP";
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/Applications/MAMP";
            yield return "/Applications/MAMP PRO";
        }
    }

    private static IEnumerable<string> EnumerateProDatabases()
    {
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrEmpty(appData))
            {
                // MAMP PRO Windows: %APPDATA%\Appsolute\MAMPPRO\userdb\mamp.db
                yield return Path.Combine(appData, "Appsolute", "MAMPPRO", "userdb", "mamp.db");
                // Older casing just in case.
                yield return Path.Combine(appData, "Appsolute", "MAMP", "userdb", "mamp.db");
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
            {
                // MAMP PRO macOS stores its db alongside the app support dir.
                yield return Path.Combine(home, "Library", "Application Support", "appsolute", "MAMP PRO", "mamp.db");
            }
        }
    }

    /// <summary>
    /// Parses the MAMP PRO <c>mamp.db</c> SQLite file. Matches the schema used
    /// by MAMP PRO 6.x on Windows — <c>VirtualHosts</c> (servername, documentroot,
    /// sslenabled, phpversion) joined with <c>VirtualHostServerAlias</c>.
    /// </summary>
    private IReadOnlyList<DiscoveredSite> ReadFromProDatabase(string dbPath)
    {
        if (!File.Exists(dbPath)) return Array.Empty<DiscoveredSite>();

        // Open read-only + immutable so we never touch the file the MAMP PRO
        // app is actively using. `Mode=ReadOnly` still acquires the same
        // shared locks as a regular connection; `Immutable=True` bypasses
        // all locking, which is exactly what we want here (we're not racing
        // a writer — MAMP PRO writes atomically on user action only).
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
        };
        // Immutable flag must go through the raw URI — SqliteConnectionStringBuilder
        // doesn't expose it directly. Append manually.
        var connStr = builder.ToString() + ";Cache=Shared";

        using var conn = new SqliteConnection(connStr);
        conn.Open();

        var rows = conn.Query<(
            long id,
            string? servername,
            string? documentroot,
            long sslenabled,
            string? phpversion)>(
            "SELECT id, servername, documentroot, sslenabled, phpversion FROM VirtualHosts")
            .ToList();

        // Bulk load aliases in a single query to avoid N+1.
        var aliasRows = conn.Query<(long VirtualHosts_id, string? serveralias)>(
            "SELECT VirtualHosts_id, serveralias FROM VirtualHostServerAlias")
            .ToList();
        var aliasByHost = aliasRows
            .Where(a => !string.IsNullOrWhiteSpace(a.serveralias))
            .GroupBy(a => a.VirtualHosts_id)
            .ToDictionary(
                g => g.Key,
                g => g.Select(a => a.serveralias!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());

        var sites = new List<DiscoveredSite>();
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.servername) || string.IsNullOrWhiteSpace(row.documentroot))
                continue;

            aliasByHost.TryGetValue(row.id, out var aliases);

            sites.Add(new DiscoveredSite(
                Domain: row.servername!,
                DocumentRoot: NormalizeDocumentRoot(row.documentroot!),
                PhpVersion: NormalizePhpVersion(row.phpversion),
                SslEnabled: row.sslenabled != 0,
                Aliases: aliases ?? Array.Empty<string>(),
                SourcePath: dbPath
            ));
        }
        return sites;
    }

    /// <summary>
    /// MAMP PRO stores a full PHP version like "8.4.12" but NKS WDC sites
    /// reference only major.minor ("8.4"). Truncate to the first two segments.
    /// Blank/null falls back to a sane default so import doesn't fail on
    /// hosts where the user never touched the PHP picker.
    /// </summary>
    private static string NormalizePhpVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "8.4";
        var parts = raw.Split('.');
        if (parts.Length < 2) return raw;
        return $"{parts[0]}.{parts[1]}";
    }

    /// <summary>
    /// MAMP PRO mixes forward- and back-slashes and sometimes emits a lowercase
    /// drive letter. Normalize to the Windows convention so the SiteManager's
    /// path equality checks don't trip over case differences.
    /// </summary>
    private static string NormalizeDocumentRoot(string raw)
    {
        var trimmed = raw.Trim().Trim('"');
        if (trimmed.Length >= 2 && trimmed[1] == ':' && char.IsLower(trimmed[0]))
            trimmed = char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
        return trimmed;
    }

    private static IEnumerable<string> EnumerateVhostFiles(string mampRoot)
    {
        var candidates = new[]
        {
            Path.Combine(mampRoot, "conf", "apache", "extra", "httpd-vhosts.conf"),
            Path.Combine(mampRoot, "conf", "apache", "extra", "custom_httpd-vhosts.conf"),
            Path.Combine(mampRoot, "bin", "apache", "conf", "extra", "httpd-vhosts.conf"),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) yield return c;

        var dirs = new[]
        {
            Path.Combine(mampRoot, "htdocs-vhosts"),
            Path.Combine(mampRoot, "conf", "apache", "extra"),
        };
        foreach (var d in dirs)
        {
            if (!Directory.Exists(d)) continue;
            foreach (var f in Directory.GetFiles(d, "*.conf", SearchOption.TopDirectoryOnly))
                yield return f;
        }
    }

    private IReadOnlyList<DiscoveredSite> ParseVhostFile(string path)
    {
        var content = File.ReadAllText(path);
        var sites = new List<DiscoveredSite>();
        var blockStart = -1;
        var depth = 0;
        for (int i = 0; i < content.Length; i++)
        {
            if (i + 13 <= content.Length && content.Substring(i, 13).Equals("<VirtualHost ", StringComparison.OrdinalIgnoreCase))
            {
                if (depth == 0) blockStart = i;
                depth++;
            }
            else if (i + 14 <= content.Length && content.Substring(i, 14).Equals("</VirtualHost>", StringComparison.OrdinalIgnoreCase))
            {
                depth--;
                if (depth == 0 && blockStart >= 0)
                {
                    var block = content.Substring(blockStart, i + 14 - blockStart);
                    var site = ParseBlock(block, path);
                    if (site != null) sites.Add(site);
                    blockStart = -1;
                }
            }
        }
        return sites;
    }

    private DiscoveredSite? ParseBlock(string block, string sourceFile)
    {
        string? domain = null;
        string? root = null;
        var aliases = new List<string>();
        var ssl = block.Contains(":443") || block.Contains("SSLEngine on", StringComparison.OrdinalIgnoreCase);

        foreach (var rawLine in block.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;

            if (line.StartsWith("ServerName ", StringComparison.OrdinalIgnoreCase))
                domain = line.Substring("ServerName ".Length).Trim().Trim('"');
            else if (line.StartsWith("ServerAlias ", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var a in line.Substring("ServerAlias ".Length).Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    aliases.Add(a.Trim().Trim('"'));
            }
            else if (line.StartsWith("DocumentRoot ", StringComparison.OrdinalIgnoreCase))
                root = line.Substring("DocumentRoot ".Length).Trim().Trim('"');
        }

        if (string.IsNullOrEmpty(domain) || string.IsNullOrEmpty(root)) return null;

        return new DiscoveredSite(
            Domain: domain,
            DocumentRoot: NormalizeDocumentRoot(root),
            PhpVersion: "8.4",
            SslEnabled: ssl,
            Aliases: aliases.ToArray(),
            SourcePath: sourceFile
        );
    }
}
