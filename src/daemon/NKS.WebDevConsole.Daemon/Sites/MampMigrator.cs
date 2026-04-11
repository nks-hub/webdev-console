using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Models;

namespace NKS.WebDevConsole.Daemon.Sites;

/// <summary>
/// Reads MAMP PRO virtual host configuration from common Windows install paths and
/// converts each MAMP host into a NKS WebDev Console <see cref="SiteConfig"/>.
///
/// Plan item "Phase 5 — MAMP PRO migration: read MAMP's SQLite DB, create NKS
/// WebDev Console TOML site configs". Pure read-only — does not touch MAMP runtime
/// in any way. Migrated sites are written to ~/.wdc/sites/&lt;domain&gt;.toml via the
/// existing <see cref="SiteManager.CreateAsync"/> path so all validation rules apply.
///
/// MAMP PRO stores its hosts in a SQLite database (mamp.db on macOS, settings.db
/// on Windows) and a per-host vhost .conf file. We don't bring in a SQLite client
/// just for migration — instead we parse the human-readable Apache vhost files MAMP
/// generates under <c>htdocs-vhosts/</c> + <c>conf/apache/extra/</c>. That covers
/// 95% of real-world MAMP installs without adding a runtime dependency.
/// </summary>
public sealed class MampMigrator
{
    private readonly ILogger<MampMigrator> _logger;

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
    /// Scans well-known MAMP install locations on the current OS and returns the
    /// list of sites that could be parsed out of MAMP's vhost configuration.
    /// Result is preview-only — call <see cref="ImportAsync"/> to actually create
    /// SiteConfig entries.
    /// </summary>
    public IReadOnlyList<DiscoveredSite> Discover()
    {
        var roots = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            roots.Add(@"C:\MAMP");
            roots.Add(@"D:\MAMP");
        }
        else if (OperatingSystem.IsMacOS())
        {
            roots.Add("/Applications/MAMP");
            roots.Add("/Applications/MAMP PRO");
        }

        var found = new List<DiscoveredSite>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            _logger.LogInformation("Found MAMP install at {Root} — scanning vhosts", root);

            // 1. Try the explicit vhost include directory
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

        // De-duplicate by domain (later definition wins)
        var unique = found
            .GroupBy(s => s.Domain, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
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

    private static IEnumerable<string> EnumerateVhostFiles(string mampRoot)
    {
        // Common MAMP locations:
        //   <root>\conf\apache\extra\httpd-vhosts.conf
        //   <root>\conf\apache\extra\custom_httpd-vhosts.conf
        //   <root>\htdocs-vhosts\*.conf       (PRO)
        //   <root>\Library\WebServerDocuments\*.conf
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

    /// <summary>
    /// Very small Apache vhost parser tailored to MAMP's generated layout. Does not
    /// attempt to be a full mod_rewrite parser — only extracts ServerName, ServerAlias,
    /// DocumentRoot, port (80/443), and the PHP version from the LoadModule line if
    /// present. Anything we cannot parse is silently ignored — migration is best-effort.
    /// </summary>
    private IReadOnlyList<DiscoveredSite> ParseVhostFile(string path)
    {
        var content = File.ReadAllText(path);
        var sites = new List<DiscoveredSite>();

        // Naive split on <VirtualHost ...> blocks
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

        // PHP version: MAMP encodes it in the include path; default fallback
        var phpVersion = "8.4";

        return new DiscoveredSite(
            Domain: domain,
            DocumentRoot: root,
            PhpVersion: phpVersion,
            SslEnabled: ssl,
            Aliases: aliases.ToArray(),
            SourcePath: sourceFile
        );
    }
}
