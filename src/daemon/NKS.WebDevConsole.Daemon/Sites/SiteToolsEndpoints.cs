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
/// Framework detection and the folder browser backend.
/// Moved verbatim out of Program.cs; registered from there at the same
/// point in the pipeline so route order is unchanged.
/// </summary>
internal static class SiteToolsEndpoints
{
    public static void MapSiteToolsEndpoints(this WebApplication app)
    {
        app.MapPost("/api/sites/{domain}/detect-framework", async (string domain, SiteManager sm) =>
        {
            var site = sm.Get(domain);
            if (site is null) return Results.NotFound();
            try
            {
                // DetectFramework reads files from documentRoot — can throw on
                // missing dir, permission denied, or path traversal in domain.
                // UpdateAsync writes the TOML config — can throw on disk full.
                var framework = sm.DetectFramework(site.DocumentRoot);
                if (framework is not null && framework != site.Framework)
                {
                    site.Framework = framework;
                    await sm.UpdateAsync(site);
                }
                return Results.Ok(new { domain, framework });
            }
            catch (Exception ex)
            {
                return Results.Problem($"Detect framework failed: {ex.Message}");
            }
        });

        // Filesystem browse — backs the FolderBrowser dialog in the site-edit UI
        // so users can pick a document root via a tree instead of typing a raw
        // Windows path. No sandboxing: the GUI runs with the same permissions as
        // the user who launched it anyway, and the alternative (restricting to
        // %USERPROFILE%) would block legitimate picks like C:\xampp\htdocs.
        app.MapGet("/api/fs/browse", (string? path) =>
        {
            try
            {
                string target;
                if (string.IsNullOrWhiteSpace(path))
                {
                    // Default landing: drives on Windows, $HOME otherwise
                    if (OperatingSystem.IsWindows())
                    {
                        var drives = DriveInfo.GetDrives()
                            .Where(d => d.IsReady)
                            .Select(d => new
                            {
                                name = d.Name,
                                path = d.Name,
                                isDir = true,
                                isFile = false,
                                size = 0L,
                            })
                            .ToArray();
                        return Results.Ok(new
                        {
                            path = "",
                            parent = (string?)null,
                            entries = drives,
                        });
                    }
                    target = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                }
                else
                {
                    target = Path.GetFullPath(path);
                }

                if (!Directory.Exists(target)) return Results.NotFound(new { error = $"Not a directory: {target}" });

                var parent = Directory.GetParent(target)?.FullName;

                var dirs = Directory.EnumerateDirectories(target)
                    .Select(d => new DirectoryInfo(d))
                    .Where(di => (di.Attributes & FileAttributes.Hidden) == 0
                             && (di.Attributes & FileAttributes.System) == 0)
                    .Select(di => new
                    {
                        name = di.Name,
                        path = di.FullName,
                        isDir = true,
                        isFile = false,
                        size = 0L,
                    });

                var files = Directory.EnumerateFiles(target)
                    .Select(f => new FileInfo(f))
                    .Where(fi => (fi.Attributes & FileAttributes.Hidden) == 0
                             && (fi.Attributes & FileAttributes.System) == 0)
                    .Select(fi => new
                    {
                        name = fi.Name,
                        path = fi.FullName,
                        isDir = false,
                        isFile = true,
                        size = fi.Length,
                    });

                return Results.Ok(new
                {
                    path = target,
                    parent,
                    entries = dirs.OrderBy(e => e.name).Concat(files.OrderBy(e => e.name)).ToArray(),
                });
            }
            catch (UnauthorizedAccessException)
            {
                return Results.StatusCode(403);
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });
    }
}
