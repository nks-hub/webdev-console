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

namespace NKS.WebDevConsole.Daemon.Services;

/// <summary>
/// PHP version and extension routes.
/// Moved verbatim out of Program.cs; registered from there at the same
/// point in the pipeline so route order is unchanged.
/// </summary>
internal static class PhpEndpoints
{
    public static void MapPhpEndpoints(this WebApplication app, PluginLoader pluginLoader)
    {
        // PHP versions — delegate to PhpPlugin via reflection
        app.MapGet("/api/php/versions", () =>
        {
            var phpPlugin = pluginLoader.Plugins.FirstOrDefault(p => p.Instance.Id == "nks.wdc.php");
            if (phpPlugin == null) return Results.NotFound();
            var method = phpPlugin.Instance.GetType().GetMethod("GetInstalledVersions");
            if (method == null) return Results.Ok(Array.Empty<object>());
            try
            {
                var versions = method.Invoke(phpPlugin.Instance, null);
                // Null return would serialize as `null`, which the frontend
                // .map()'s over — normalize to empty array.
                return Results.Ok(versions ?? (object)Array.Empty<object>());
            }
            catch (Exception ex)
            {
                app.Logger.LogWarning(ex, "GetInstalledVersions reflection failed on php plugin");
                return Results.Ok(Array.Empty<object>());
            }
        });

        // PHP extensions for a given version
        app.MapGet("/api/php/{version}/extensions", async (string version) =>
        {
            var phpPlugin = pluginLoader.Plugins.FirstOrDefault(p => p.Instance.Id == "nks.wdc.php");
            if (phpPlugin == null) return Results.NotFound();
            var method = phpPlugin.Instance.GetType().GetMethod("GetExtensionsForVersion");
            if (method == null) return Results.Ok(Array.Empty<object>());
            try
            {
                if (method.Invoke(phpPlugin.Instance, new object[] { version }) is Task task)
                {
                    await task;
                    var resultProp = task.GetType().GetProperty("Result");
                    var value = resultProp?.GetValue(task);
                    // Normalize null → [] so the frontend's `.map` never throws.
                    return Results.Ok(value ?? (object)Array.Empty<object>());
                }
            }
            catch (Exception ex)
            {
                app.Logger.LogWarning(ex, "GetExtensionsForVersion reflection failed on php plugin for {Version}", version);
            }
            return Results.Ok(Array.Empty<object>());
        });

        // Toggle a PHP extension on/off for a given version. Writes a persistent
        // override via PhpExtensionOverrides, patches the live php.ini files in-place
        // (comments / uncomments `extension=name` lines so the change survives the
        // next daemon start even before the plugin re-runs its ini generator), and
        // restarts the PHP module so the change takes effect immediately.
        //
        // Body: { "enabled": true|false }
        // Path: /api/php/{version}/extensions/{name}  (version is major.minor, e.g. "8.4")
        app.MapPost("/api/php/{version}/extensions/{name}", async (
            string version,
            string name,
            HttpContext ctx,
            PhpExtensionOverrides overrides,
            IServiceProvider sp,
            ILoggerFactory lf) =>
        {
            // Input validation — extension names are lowercase alnum + underscore.
            if (!System.Text.RegularExpressions.Regex.IsMatch(version, @"^\d+\.\d+$"))
                return Results.BadRequest(new { error = "version must be major.minor, e.g. 8.4" });
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-z0-9_]{1,64}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return Results.BadRequest(new { error = "invalid extension name" });

            var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, object?>>();
            var enabled = body is not null
                && body.TryGetValue("enabled", out var v)
                && v switch
                {
                    bool b => b,
                    JsonElement j when j.ValueKind == JsonValueKind.True => true,
                    _ => false,
                };

            overrides.SetOverride(version, name, enabled);

            // Patch the live php.ini files — one next to the binary (used by
            // mod_fcgid-spawned php-cgi.exe) and one under _config.ConfigBaseDirectory
            // (used by the daemon's own invocations). We comment/uncomment the
            // `extension=name` directive so the change takes effect on next spawn.
            var phpRoot = Path.Combine(NKS.WebDevConsole.Core.Services.WdcPaths.BinariesRoot, "php");
            var patched = new List<string>();
            if (Directory.Exists(phpRoot))
            {
                foreach (var verDir in Directory.GetDirectories(phpRoot))
                {
                    var dirName = Path.GetFileName(verDir);
                    if (!dirName.StartsWith(version)) continue;
                    var iniPath = Path.Combine(verDir, "php.ini");
                    if (!File.Exists(iniPath)) continue;
                    try
                    {
                        PatchExtensionLine(iniPath, name, enabled);
                        patched.Add(iniPath);
                    }
                    catch (Exception ex)
                    {
                        lf.CreateLogger("PhpExt").LogWarning(ex, "Failed to patch {Path}", iniPath);
                    }
                }
            }

            // Restart the PHP module so the new ini is picked up. Non-fatal if it
            // fails — the override is already persisted for the next startup.
            try
            {
                var modules = sp.GetServices<IServiceModule>();
                var phpModule = modules.FirstOrDefault(m => m.ServiceId.Equals("php", StringComparison.OrdinalIgnoreCase));
                if (phpModule is not null)
                {
                    await phpModule.StopAsync(CancellationToken.None);
                    await phpModule.StartAsync(CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                lf.CreateLogger("PhpExt").LogWarning(ex, "Failed to restart PHP module after extension toggle");
            }

            return Results.Ok(new { version, name, enabled, patchedFiles = patched });

            // --- local helper ---
            static void PatchExtensionLine(string iniPath, string extName, bool shouldBeEnabled)
            {
                var lines = File.ReadAllLines(iniPath).ToList();
                var enableLine = $"extension={extName}";
                var disableLine = $";extension={extName}";
                var pattern = new System.Text.RegularExpressions.Regex(
                    @"^\s*;?\s*extension\s*=\s*" + System.Text.RegularExpressions.Regex.Escape(extName) + @"\s*$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                bool found = false;
                for (int i = 0; i < lines.Count; i++)
                {
                    if (!pattern.IsMatch(lines[i])) continue;
                    lines[i] = shouldBeEnabled ? enableLine : disableLine;
                    found = true;
                }
                if (!found)
                {
                    // Extension not yet mentioned — add under [ext] section or EOF.
                    lines.Add("");
                    lines.Add(shouldBeEnabled ? enableLine : disableLine);
                }
                File.WriteAllLines(iniPath, lines);
            }
        });
    }
}
