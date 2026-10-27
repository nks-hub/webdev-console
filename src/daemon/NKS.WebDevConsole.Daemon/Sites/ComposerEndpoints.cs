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
/// Per-site Composer operations and the global composer.phar management routes.
/// Moved verbatim out of Program.cs; registered from there at the same
/// point in the pipeline so route order is unchanged.
/// </summary>
internal static class ComposerEndpoints
{
    /// <summary>
    /// Calls a ComposerInvoker method by reflection. The daemon passes a
    /// per-site PHP path as the argument before the CancellationToken; Composer
    /// plugin 1.0.2+ takes it (exact-arity overload wins). Builds before 1.0.2
    /// have no such parameter, so for a method one parameter short the PHP
    /// override is dropped and composer runs under the plugin's global PHP
    /// instead of failing with "Parameter count mismatch".
    /// </summary>
    internal static async Task<(bool ExitCode, int Code, string Stdout, string Stderr)>
        InvokeComposerAsync(object invoker, string method, object?[] args)
    {
        var candidates = invoker.GetType().GetMethods().Where(x => x.Name == method).ToList();
        var m = candidates.FirstOrDefault(x => x.GetParameters().Length == args.Length);
        if (m is null && args.Length >= 2
            && candidates.FirstOrDefault(x => x.GetParameters().Length == args.Length - 1) is { } shorter)
        {
            m = shorter;
            args = [.. args[..^2], args[^1]];
        }
        if (m is null)
            throw new InvalidOperationException($"Method {method} with {args.Length} parameters not found on ComposerInvoker");
        var task = (Task)m.Invoke(invoker, args)!;
        await task;
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var exitCode = (int)result.GetType().GetProperty("ExitCode")!.GetValue(result)!;
        var stdout   = (string)result.GetType().GetProperty("Stdout")!.GetValue(result)!;
        var stderr   = (string)result.GetType().GetProperty("Stderr")!.GetValue(result)!;
        return (exitCode == 0, exitCode, stdout, stderr);
    }

    public static void MapComposerEndpoints(this WebApplication app, PluginLoader pluginLoader)
    {
        // ── Composer endpoints ────────────────────────────────────────────────────────
        // ComposerPlugin registers ComposerInvoker via IWdcPlugin.Initialize into the
        // shared DI container. Because the plugin DLL is loaded in an isolated
        // AssemblyLoadContext we cannot reference its types at compile time. We look up
        // the type name from the loaded plugin assembly and resolve via sp.GetService(type)
        // to avoid a hard project reference while still using the registered singleton.

        static object? ResolveComposerInvoker(IServiceProvider sp, PluginLoader loader)
        {
            var composerPlugin = loader.Plugins.FirstOrDefault(p => p.Instance.Id == "nks.wdc.composer");
            if (composerPlugin is null) return null;
            var invokerType = composerPlugin.Assembly.GetType("NKS.WebDevConsole.Plugin.Composer.ComposerInvoker");
            return invokerType is null ? null : sp.GetService(invokerType);
        }


        /// <summary>
        /// F77: resolve the per-site PHP binary path so composer.phar runs under
        /// the interpreter version the site has actually declared. Returns null
        /// when the site has no phpVersion pinned — the invoker then falls back
        /// to ComposerConfig.PhpPath (the plugin's global scan-first winner).
        ///
        /// Layout: ~/.wdc/binaries/php/&lt;version&gt;/php.exe (matches F33
        /// discovery path in ComposerConfig.ApplyOwnBinaryDefaults).
        /// </summary>
        static string? ResolveSitePhpBinary(NKS.WebDevConsole.Core.Models.SiteConfig site)
        {
            if (string.IsNullOrWhiteSpace(site.PhpVersion)) return null;
            var binariesRoot = NKS.WebDevConsole.Core.Services.WdcPaths.BinariesRoot;
            var candidate = Path.Combine(binariesRoot, "php", site.PhpVersion,
                OperatingSystem.IsWindows() ? "php.exe" : "php");
            return File.Exists(candidate) ? candidate : null;
        }

        static string ResolveComposerRoot(string docroot)
        {
            if (File.Exists(Path.Combine(docroot, "composer.json"))) return docroot;
            var parentDir = Path.GetDirectoryName(docroot.TrimEnd('/', '\\'));
            if (!string.IsNullOrEmpty(parentDir) && File.Exists(Path.Combine(parentDir, "composer.json")))
                return parentDir;
            return docroot;
        }

        app.MapGet("/api/sites/{domain}/composer/status", async (string domain, SiteManager sm, ILoggerFactory lf) =>
        {
            var site = sm.Get(domain);
            if (site is null) return Results.NotFound(new { error = $"Site '{domain}' not found" });

            var root = site.DocumentRoot;
            if (!Directory.Exists(root))
                return Results.NotFound(new { error = $"Document root for '{domain}' does not exist" });
            var hasJson = File.Exists(Path.Combine(root, "composer.json"));
            var composerRoot = root;
            if (!hasJson)
            {
                var parentDir = Path.GetDirectoryName(root.TrimEnd('/', '\\'));
                if (!string.IsNullOrEmpty(parentDir))
                {
                    var parentJson = Path.Combine(parentDir, "composer.json");
                    if (File.Exists(parentJson))
                    {
                        hasJson = true;
                        composerRoot = parentDir;
                    }
                }
            }
            var hasLock = File.Exists(Path.Combine(composerRoot, "composer.lock"));

            var packages = new List<string>();
            string? phpVersion = null;
            if (hasJson)
            {
                try
                {
                    var composerJson = await File.ReadAllTextAsync(Path.Combine(composerRoot, "composer.json"));
                    using var doc = System.Text.Json.JsonDocument.Parse(composerJson);
                    if (doc.RootElement.TryGetProperty("require", out var require))
                    {
                        foreach (var pkg in require.EnumerateObject())
                            if (!pkg.Name.Equals("php", StringComparison.OrdinalIgnoreCase))
                                packages.Add($"{pkg.Name}:{pkg.Value.GetString() ?? "*"}");
                        if (require.TryGetProperty("php", out var phpConstraint))
                            phpVersion = phpConstraint.GetString();
                    }
                }
                catch (Exception ex)
                {
                    lf.CreateLogger("Composer").LogWarning(ex, "Could not parse composer.json for {Domain}", domain);
                }
            }

            // Detect PHP framework — returns lowercase identifier or null for unknown
            string? framework = null;
            try { framework = sm.DetectFramework(root); }
            catch (Exception ex)
            {
                lf.CreateLogger("Composer").LogWarning(ex, "DetectFramework failed for {Domain}", domain);
            }

            // installSuggestion: present only when a PHP framework is detected,
            // composer.json exists, and composer.lock is missing (deps never installed)
            object? installSuggestion = null;
            if (framework is not null && hasJson && !hasLock)
            {
                installSuggestion = new
                {
                    reason = "framework_detected",
                    framework,
                    action = "composer_install",
                };
            }

            return Results.Ok(new { hasComposerJson = hasJson, hasLock, packages, phpVersion, framework = framework ?? "none", installSuggestion, composerRoot });
        });

        app.MapPost("/api/sites/{domain}/composer/install", async (string domain, SiteManager sm, IServiceProvider sp, ILoggerFactory lf, CancellationToken ct) =>
        {
            var site = sm.Get(domain);
            if (site is null) return Results.NotFound(new { error = $"Site '{domain}' not found" });

            var invoker = ResolveComposerInvoker(sp, pluginLoader);
            if (invoker is null)
                return Results.Problem(title: "Composer plugin not loaded",
                    detail: "Install the Composer plugin and restart the daemon.", statusCode: 503);

            var logger = lf.CreateLogger("Composer");
            var installRoot = ResolveComposerRoot(site.DocumentRoot);
            logger.LogInformation("composer install for {Domain} in {Root}", domain, installRoot);
            try
            {
                var phpOverride = ResolveSitePhpBinary(site);
                var (_, exitCode, stdout, stderr) = await InvokeComposerAsync(invoker, "InstallAsync",
                    [installRoot, phpOverride, ct]);
                logger.LogInformation("composer install exit={Code} for {Domain}", exitCode, domain);
                return Results.Ok(new { exitCode, stdout, stderr });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "composer install failed for {Domain}", domain);
                return Results.Problem(title: "composer install failed", detail: ex.InnerException?.Message ?? ex.Message, statusCode: 500);
            }
        });

        app.MapPost("/api/sites/{domain}/composer/require", async (string domain, HttpContext ctx, SiteManager sm, IServiceProvider sp, ILoggerFactory lf, CancellationToken ct) =>
        {
            var site = sm.Get(domain);
            if (site is null) return Results.NotFound(new { error = $"Site '{domain}' not found" });

            var invoker = ResolveComposerInvoker(sp, pluginLoader);
            if (invoker is null)
                return Results.Problem(title: "Composer plugin not loaded",
                    detail: "Install the Composer plugin and restart the daemon.", statusCode: 503);

            Dictionary<string, object?>? body;
            try { body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, object?>>(); }
            catch (System.Text.Json.JsonException ex)
            { return Results.BadRequest(new { error = $"Invalid JSON: {ex.Message}" }); }

            if (body is null || !body.TryGetValue("package", out var pkgObj) || pkgObj?.ToString() is not { Length: > 0 } package)
                return Results.BadRequest(new { error = "Body must contain { \"package\": \"vendor/name\" }" });

            if (!System.Text.RegularExpressions.Regex.IsMatch(package, @"^[A-Za-z0-9/_.:\-\^~*@]+$"))
                return Results.BadRequest(new { error = "Invalid package name" });

            // Guard against path traversal sequences even when individual chars are valid
            if (package.Contains(".."))
                return Results.BadRequest(new { error = "Invalid package name" });

            var logger = lf.CreateLogger("Composer");
            logger.LogInformation("composer require {Package} for {Domain}", package, domain);
            try
            {
                var phpOverride = ResolveSitePhpBinary(site);
                var (_, exitCode, stdout, stderr) = await InvokeComposerAsync(invoker, "RequireAsync",
                    [site.DocumentRoot, package, phpOverride, ct]);
                logger.LogInformation("composer require {Package} exit={Code} for {Domain}", package, exitCode, domain);
                return Results.Ok(new { exitCode, stdout, stderr });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "composer require {Package} failed for {Domain}", package, domain);
                return Results.Problem(title: "composer require failed", detail: ex.InnerException?.Message ?? ex.Message, statusCode: 500);
            }
        });

        app.MapPost("/api/sites/{domain}/composer/remove", async (string domain, HttpContext ctx, SiteManager sm, IServiceProvider sp, ILoggerFactory lf, CancellationToken ct) =>
        {
            var site = sm.Get(domain);
            if (site is null) return Results.NotFound(new { error = $"Site '{domain}' not found" });

            var invoker = ResolveComposerInvoker(sp, pluginLoader);
            if (invoker is null)
                return Results.Problem(title: "Composer plugin not loaded",
                    detail: "Install the Composer plugin and restart the daemon.", statusCode: 503);

            Dictionary<string, object?>? body;
            try { body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, object?>>(); }
            catch (System.Text.Json.JsonException ex)
            { return Results.BadRequest(new { error = $"Invalid JSON: {ex.Message}" }); }

            if (body is null || !body.TryGetValue("package", out var pkgObj) || pkgObj?.ToString() is not { Length: > 0 } package)
                return Results.BadRequest(new { error = "Body must contain { \"package\": \"vendor/name\" }" });

            if (!System.Text.RegularExpressions.Regex.IsMatch(package, @"^[A-Za-z0-9/_.\-]+$"))
                return Results.BadRequest(new { error = "Invalid package name" });

            if (package.Contains(".."))
                return Results.BadRequest(new { error = "Invalid package name" });

            var composerRoot = ResolveComposerRoot(site.DocumentRoot);
            var logger = lf.CreateLogger("Composer");
            logger.LogInformation("composer remove {Package} for {Domain}", package, domain);
            try
            {
                var phpOverride = ResolveSitePhpBinary(site);
                var (_, exitCode, stdout, stderr) = await InvokeComposerAsync(invoker, "RunAsync",
                    [composerRoot, new[] { "remove", package }, phpOverride, ct]);
                logger.LogInformation("composer remove {Package} exit={Code} for {Domain}", package, exitCode, domain);
                return Results.Ok(new { exitCode, stdout, stderr });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "composer remove {Package} failed for {Domain}", package, domain);
                return Results.Problem(title: "composer remove failed", detail: ex.InnerException?.Message ?? ex.Message, statusCode: 500);
            }
        });

        app.MapPost("/api/sites/{domain}/composer/init", async (string domain, HttpContext ctx, SiteManager sm, IServiceProvider sp, ILoggerFactory lf, CancellationToken ct) =>
        {
            var site = sm.Get(domain);
            if (site is null) return Results.NotFound(new { error = $"Site '{domain}' not found" });
            var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>(cancellationToken: ct);
            var invoker = ResolveComposerInvoker(sp, pluginLoader);
            if (invoker is null) return Results.Problem(title: "Composer plugin not loaded", statusCode: 503);
            var composerRoot = ResolveComposerRoot(site.DocumentRoot);
            if (!File.Exists(Path.Combine(composerRoot, "composer.json")))
                composerRoot = site.DocumentRoot;
            var name = body?.GetValueOrDefault("name") ?? $"local/{domain.Replace('.', '-')}";
            var description = body?.GetValueOrDefault("description") ?? "";
            var type = body?.GetValueOrDefault("type") ?? "project";
            var stability = body?.GetValueOrDefault("stability") ?? "stable";
            var args = new List<string> { "init", "--no-interaction", $"--name={name}", $"--type={type}", $"--stability={stability}" };
            if (!string.IsNullOrWhiteSpace(description)) args.Add($"--description={description}");
            try
            {
                var phpOverride = ResolveSitePhpBinary(site);
                var (_, exitCode, stdout, stderr) = await InvokeComposerAsync(invoker, "RunAsync", [composerRoot, args.ToArray(), phpOverride, ct]);
                return Results.Ok(new { exitCode, stdout, stderr, composerRoot });
            }
            catch (Exception ex) { return Results.Problem(title: "composer init failed", detail: ex.Message, statusCode: 500); }
        });

        app.MapGet("/api/sites/{domain}/composer/diagnose", async (string domain, SiteManager sm, IServiceProvider sp, ILoggerFactory lf, CancellationToken ct) =>
        {
            var site = sm.Get(domain);
            if (site is null) return Results.NotFound(new { error = $"Site '{domain}' not found" });
            var invoker = ResolveComposerInvoker(sp, pluginLoader);
            if (invoker is null) return Results.Problem(title: "Composer plugin not loaded", statusCode: 503);
            var composerRoot = ResolveComposerRoot(site.DocumentRoot);
            var logger = lf.CreateLogger("Composer");
            try
            {
                var phpOverride = ResolveSitePhpBinary(site);
                var (_, _, stdout, _) = await InvokeComposerAsync(invoker, "RunAsync",
                    [composerRoot, new[] { "diagnose", "--no-ansi" }, phpOverride, ct]);
                var warnings = new List<string>();
                var errors = new List<string>();
                foreach (var line in (stdout ?? "").Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("WARNING", StringComparison.OrdinalIgnoreCase)) warnings.Add(trimmed);
                    else if (trimmed.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)) errors.Add(trimmed);
                }
                return Results.Ok(new { warnings, errors });
            }
            catch (Exception ex) { return Results.Problem(title: "composer diagnose failed", detail: ex.Message, statusCode: 500); }
        });

        app.MapGet("/api/sites/{domain}/composer/outdated", async (string domain, SiteManager sm, IServiceProvider sp, ILoggerFactory lf, CancellationToken ct) =>
        {
            var site = sm.Get(domain);
            if (site is null) return Results.NotFound(new { error = $"Site '{domain}' not found" });
            var invoker = ResolveComposerInvoker(sp, pluginLoader);
            if (invoker is null) return Results.Problem(title: "Composer plugin not loaded", statusCode: 503);
            var composerRoot = ResolveComposerRoot(site.DocumentRoot);
            var logger = lf.CreateLogger("Composer");
            try
            {
                var phpOverride = ResolveSitePhpBinary(site);
                var (_, exitCode, stdout, _) = await InvokeComposerAsync(invoker, "RunAsync",
                    [composerRoot, new[] { "outdated", "--direct", "--format=json", "--no-ansi" }, phpOverride, ct]);
                System.Collections.Generic.List<object> installed = new();
                if (!string.IsNullOrWhiteSpace(stdout))
                {
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(stdout);
                        if (doc.RootElement.TryGetProperty("installed", out var arr))
                        {
                            foreach (var pkg in arr.EnumerateArray())
                            {
                                installed.Add(new {
                                    name = pkg.GetProperty("name").GetString(),
                                    version = pkg.TryGetProperty("version", out var v) ? v.GetString() : null,
                                    latest = pkg.TryGetProperty("latest", out var l) ? l.GetString() : null,
                                    latestStatus = pkg.TryGetProperty("latest-status", out var s) ? s.GetString() : null,
                                });
                            }
                        }
                    }
                    catch (Exception ex) { logger.LogWarning(ex, "composer outdated parse failed"); }
                }
                return Results.Ok(new { installed });
            }
            catch (Exception ex) { return Results.Problem(title: "composer outdated failed", detail: ex.Message, statusCode: 500); }
        });

        app.MapGet("/api/composer/version", async (IServiceProvider sp, PluginLoader pluginLoader, CancellationToken ct) =>
        {
            var invoker = ResolveComposerInvoker(sp, pluginLoader);
            if (invoker is null)
                return Results.Ok(new { version = (string?)null, path = (string?)null, managed = false });

            string? version = null;
            string? path = null;
            string? phpPath = null;
            string? lastError = null;
            bool managed = false;

            // Read config paths FIRST so we can include them in diagnostics even if the
            // composer invocation throws (e.g. PHP not on PATH, phar missing).
            try
            {
                var configField = invoker.GetType().GetField("_config",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var config = configField?.GetValue(invoker);
                if (config is not null)
                {
                    path = config.GetType().GetProperty("ExecutablePath")?.GetValue(config) as string;
                    phpPath = config.GetType().GetProperty("PhpPath")?.GetValue(config) as string;
                    if (path is not null)
                        managed = path.StartsWith(NKS.WebDevConsole.Core.Services.WdcPaths.BinariesRoot,
                            StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex) { lastError = $"reflection: {ex.Message}"; }

            try
            {
                var tempDir = Path.GetTempPath();
                var (ok, exitCode, stdout, stderr) = await InvokeComposerAsync(invoker, "RunAsync",
                    [tempDir, new[] { "--version", "--no-ansi" }, (string?)null, ct]);
                // Strip any residual ANSI escape sequences (some composer builds emit them
                // even with --no-ansi when run via CliWrap because ConEmu detection still
                // thinks it's a TTY). Pattern: ESC [ ... m
                var clean = System.Text.RegularExpressions.Regex.Replace(stdout, @"\u001B\[[0-9;]*[mK]", "");
                if (ok)
                {
                    var m = System.Text.RegularExpressions.Regex.Match(clean, @"Composer(?:\s+version)?\s+(\S+)");
                    if (m.Success) version = m.Groups[1].Value;
                    else lastError = $"regex: stdout={clean.Substring(0, Math.Min(clean.Length, 120))}";
                }
                else
                {
                    lastError = $"exit={exitCode} stderr={stderr.Substring(0, Math.Min(stderr.Length, 200))}";
                }
            }
            catch (Exception ex) { lastError = $"invoke: {ex.InnerException?.Message ?? ex.Message}"; }

            // Without a managed phar the plugin falls back to a bare "composer.bat" /
            // "composer" PATH lookup. That is not an install unless it actually ran.
            if (version is null && path is not null && !Path.IsPathRooted(path))
                path = null;

            return Results.Ok(new { version, path, phpPath, managed, lastError });
        });

        app.MapPost("/api/composer/self-install", async (IHttpClientFactory httpFactory, IServiceProvider sp, PluginLoader pluginLoader, ILoggerFactory lf, CancellationToken ct) =>
        {
            var logger = lf.CreateLogger("Composer");
            try
            {
                var composerBinRoot = Path.Combine(NKS.WebDevConsole.Core.Services.WdcPaths.BinariesRoot, "composer");
                var versionDir = Path.Combine(composerBinRoot, "latest");
                Directory.CreateDirectory(versionDir);
                var pharPath = Path.Combine(versionDir, "composer.phar");

                var http = httpFactory.CreateClient();
                http.Timeout = TimeSpan.FromSeconds(60);
                using var resp = await http.GetAsync("https://getcomposer.org/composer.phar", ct);
                if (!resp.IsSuccessStatusCode)
                    return Results.Problem(title: "Download failed", detail: $"HTTP {(int)resp.StatusCode} from getcomposer.org", statusCode: 502);
                await using (var fs = File.Create(pharPath))
                    await resp.Content.CopyToAsync(fs, ct);

                // F27: defense-in-depth SHA-256 verification. getcomposer.org publishes
                // composer.phar.sha256sum alongside the phar; cross-check so a MITM or
                // compromised mirror can't substitute a trojaned installer even if TLS
                // is downgraded by a hostile cert. Missing sidecar is logged but NOT
                // fatal — upstream occasionally lags.
                try
                {
                    using var sumResp = await http.GetAsync("https://getcomposer.org/composer.phar.sha256sum", ct);
                    if (sumResp.IsSuccessStatusCode)
                    {
                        var raw = (await sumResp.Content.ReadAsStringAsync(ct)).Trim();
                        // Format: "<hex>  composer.phar" — take the first whitespace-separated token.
                        var expected = raw.Split([' ', '\t', '\n'], 2, StringSplitOptions.RemoveEmptyEntries)
                            .FirstOrDefault() ?? "";
                        if (expected.Length == 64)
                        {
                            await using var vs = File.OpenRead(pharPath);
                            var hash = await System.Security.Cryptography.SHA256.HashDataAsync(vs, ct);
                            var actual = Convert.ToHexString(hash).ToLowerInvariant();
                            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                            {
                                try { File.Delete(pharPath); } catch { /* ignore */ }
                                return Results.Problem(
                                    title: "Integrity check failed",
                                    detail: $"composer.phar SHA-256 mismatch: upstream {expected}, downloaded {actual}. Aborted and removed the bad artifact.",
                                    statusCode: 502);
                            }
                            logger.LogInformation("composer.phar sha-256 verified against upstream sum");
                        }
                    }
                    else
                    {
                        logger.LogWarning("composer.phar.sha256sum returned {Status} — TLS is the only remaining integrity guarantee", (int)sumResp.StatusCode);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "composer.phar sha-256 verification skipped");
                }

                // Re-scan binaries so ComposerInvoker picks up the new phar without a daemon restart.
                var composerPlugin = pluginLoader.Plugins.FirstOrDefault(p => p.Instance.Id == "nks.wdc.composer");
                if (composerPlugin is not null)
                {
                    var invokerType = composerPlugin.Assembly.GetType("NKS.WebDevConsole.Plugin.Composer.ComposerInvoker");
                    var configType  = composerPlugin.Assembly.GetType("NKS.WebDevConsole.Plugin.Composer.ComposerConfig");
                    if (invokerType is not null && configType is not null)
                    {
                        var invoker = sp.GetService(invokerType);
                        if (invoker is not null)
                        {
                            var configField = invokerType.GetField("_config", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            var config = configField?.GetValue(invoker);
                            var applyMethod = configType.GetMethod("ApplyOwnBinaryDefaults");
                            applyMethod?.Invoke(config, null);
                        }
                    }
                }
                logger.LogInformation("composer self-install ok -> {Path}", pharPath);
                return Results.Ok(new { path = pharPath, version = "latest" });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "composer self-install failed");
                return Results.Problem(title: "self-install failed", detail: ex.Message, statusCode: 500);
            }
        });
    }
}
