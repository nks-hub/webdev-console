using System.Text.Json;
using System.Net;
using System.Net.NetworkInformation;
using Dapper;
using NKS.WebDevConsole.Daemon.Plugin;
using NKS.WebDevConsole.Daemon.Services;
using NKS.WebDevConsole.Daemon.Sites;
using NKS.WebDevConsole.Daemon.Binaries;
using NKS.WebDevConsole.Daemon.Backup;
using NKS.WebDevConsole.Daemon.Config;
using NKS.WebDevConsole.Daemon.Data;
using NKS.WebDevConsole.Daemon.Mcp;
using NKS.WebDevConsole.Daemon.Cloudflare;
using NKS.WebDevConsole.Daemon.Ssl;
using NKS.WebDevConsole.Daemon.Hosts;
using NKS.WebDevConsole.Daemon.Deploy;
using CliWrap.Buffered;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Core.Services;

namespace NKS.WebDevConsole.Daemon.Sites;

/// <summary>
/// First-run onboarding state and completion.
/// Moved verbatim out of Program.cs; registered from there at the same
/// point in the pipeline so route order is unchanged.
/// </summary>
internal static class OnboardingEndpoints
{
    public static void MapOnboardingEndpoints(this WebApplication app)
    {
        // Onboarding state — Phase 7 plan item.
        // First-run detection: a simple ~/.wdc/data/onboarding-complete.flag file marks the
        // wizard as finished. Before that file exists the frontend shows the onboarding
        // wizard. The wizard also returns which prerequisites are already satisfied so it
        // can skip ahead past any steps the user has already completed manually.
        app.MapGet("/api/onboarding/state", (BinaryManager bm) =>
        {
            var flagFile = Path.Combine(NKS.WebDevConsole.Core.Services.WdcPaths.DataRoot, "onboarding-complete.flag");

            var installed = bm.ListInstalled();
            var hasApache = installed.Any(b => b.App.Equals("apache", StringComparison.OrdinalIgnoreCase));
            var hasPhp = installed.Any(b => b.App.Equals("php", StringComparison.OrdinalIgnoreCase));
            var hasMysql = installed.Any(b => b.App.Equals("mysql", StringComparison.OrdinalIgnoreCase));
            var hasMkcert = installed.Any(b => b.App.Equals("mkcert", StringComparison.OrdinalIgnoreCase));

            // mkcert CA installed state — check presence of rootCA.pem in the mkcert CAROOT
            var mkcertRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "mkcert");
            var mkcertCaInstalled = File.Exists(Path.Combine(mkcertRoot, "rootCA.pem"));

            // ListInstalled is newest-first per app, so FirstOrDefault is the latest version.
            string? VersionOf(string app) =>
                installed.FirstOrDefault(b => b.App.Equals(app, StringComparison.OrdinalIgnoreCase))?.Version;

            return Results.Ok(new
            {
                completed = File.Exists(flagFile),
                prerequisites = new
                {
                    apacheInstalled = hasApache,
                    phpInstalled = hasPhp,
                    mysqlInstalled = hasMysql,
                    mkcertBinaryInstalled = hasMkcert,
                    mkcertCaInstalled,
                },
                versions = new
                {
                    apache = VersionOf("apache"),
                    php = VersionOf("php"),
                    mysql = VersionOf("mysql"),
                    mkcert = VersionOf("mkcert"),
                }
            });
        });

        app.MapPost("/api/onboarding/complete", async (SiteManager siteManager, SiteOrchestrator siteOrch) =>
        {
            var flagFile = Path.Combine(NKS.WebDevConsole.Core.Services.WdcPaths.DataRoot, "onboarding-complete.flag");
            Directory.CreateDirectory(Path.GetDirectoryName(flagFile)!);
            File.WriteAllText(flagFile, DateTime.UtcNow.ToString("O"));

            // Seed a `localhost` site with an About page so the user gets a
            // working http://localhost/ out of the box — dashboard link that
            // doesn't 404. Skipped if they already have one (don't clobber).
            try
            {
                var docRoot = Path.Combine(NKS.WebDevConsole.Core.Services.WdcPaths.DataRoot, "default-site");
                Directory.CreateDirectory(docRoot);
                var indexPath = Path.Combine(docRoot, "index.php");
                if (!File.Exists(indexPath))
                {
                    await File.WriteAllTextAsync(indexPath,
                        "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">" +
                        "<title>NKS WebDev Console — about</title>" +
                        "<style>body{font-family:-apple-system,Segoe UI,Inter,sans-serif;max-width:780px;margin:4rem auto;padding:0 1.5rem;color:#1f2937;line-height:1.55}" +
                        "h1{font-weight:600;letter-spacing:-0.02em}code,pre{background:#f3f4f6;padding:2px 6px;border-radius:4px;font-size:.9em}" +
                        "pre{padding:12px 16px;overflow-x:auto}.pill{display:inline-block;padding:2px 10px;border-radius:999px;background:#dbeafe;color:#1e40af;font-size:.8em;margin-right:6px}</style>" +
                        "</head><body><h1>NKS WebDev Console</h1>" +
                        "<p><span class=\"pill\">localhost</span><span class=\"pill\">Apache + PHP-FPM</span>It works!</p>" +
                        "<p>This is the default site seeded by NKS WDC at first-run. You're hitting it through Apache, which proxied the request to PHP-FPM and executed <code>index.php</code>.</p>" +
                        "<h3>Runtime</h3><pre><?php\n" +
                        "  echo 'PHP:         ' . PHP_VERSION . ' (' . PHP_OS . \", \" . php_sapi_name() . \"\\n\";\n" +
                        "  echo 'Server:      ' . ($_SERVER['SERVER_SOFTWARE'] ?? 'n/a') . \"\\n\";\n" +
                        "  echo 'Host:        ' . ($_SERVER['HTTP_HOST'] ?? 'n/a') . \"\\n\";\n" +
                        "  echo 'INI:         ' . (php_ini_loaded_file() ?: '(none)') . \"\\n\";\n" +
                        "?></pre>" +
                        "<h3>Next steps</h3><ul>" +
                        "<li>Create your own site under <strong>Sites → New</strong>.</li>" +
                        "<li>Add <code>127.0.0.1 myapp.loc</code> to <code>/etc/hosts</code> (WDC prompts for admin the first time).</li>" +
                        "<li>Manage PHP versions, Apache config, MariaDB, Redis from the sidebar.</li></ul>" +
                        "<p style=\"margin-top:3rem;color:#6b7280;font-size:.85em;\">Generated by NKS WDC — remove this file or change <code>DocumentRoot</code> to point elsewhere.</p>" +
                        "</body></html>");
                }
                if (!siteManager.Sites.Keys.Any(d => string.Equals(d, "localhost", StringComparison.OrdinalIgnoreCase)))
                {
                    // CreateAsync persists the vhost TOML + registers the site in the
                    // SiteManager dictionary. ApplyAsync alone would generate the Apache
                    // vhost file but leave SiteManager unaware of the site, so on next
                    // daemon restart the site would vanish. This is the same order the
                    // /api/sites POST handler uses.
                    // PhpVersion empty lets ApacheModule pick the daemon's active
                    // version at orchestration time instead of pinning to a
                    // possibly-not-installed "8.5". SslEnabled: true so the seed
                    // site exposes https://localhost/ out of the gate — mkcert
                    // issues the cert during ApplyAsync, the vhost config gets
                    // both :80 and :443 blocks, and the user can click the first
                    // link they see and have a working HTTPS loopback.
                    var created = await siteManager.CreateAsync(new NKS.WebDevConsole.Core.Models.SiteConfig
                    {
                        Domain = "localhost",
                        DocumentRoot = docRoot,
                        PhpVersion = "",
                        SslEnabled = true,
                        Enabled = true,
                        Aliases = new[] { "127.0.0.1" },
                    });
                    await siteOrch.ApplyAsync(created, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                app.Logger.LogWarning(ex, "Failed to seed default localhost site");
            }
            return Results.Ok(new { completed = true });
        });
    }
}
