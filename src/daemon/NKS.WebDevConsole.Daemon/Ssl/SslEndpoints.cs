using System.Text.Json;
using NKS.WebDevConsole.Daemon.Plugin;
using NKS.WebDevConsole.Daemon.Sites;

namespace NKS.WebDevConsole.Daemon.Ssl;

/// <summary>
/// Route registrations for the /api/ssl certificate surface. The SSL
/// plugin sits in its own ALC, so certs are managed by reflection.
///
/// Lifted verbatim out of Program.cs, which carried all 191 endpoints inline.
/// </summary>
internal static class SslEndpoints
{
    /// <summary>mkcert's default CAROOT for this OS (or the CAROOT override).</summary>
    internal static string MkcertCaRoot()
    {
        var env = Environment.GetEnvironmentVariable("CAROOT");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mkcert");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
            return Path.Combine(home, "Library", "Application Support", "mkcert");
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return Path.Combine(string.IsNullOrWhiteSpace(xdg) ? Path.Combine(home, ".local", "share") : xdg, "mkcert");
    }

    /// <summary>
    /// True when the mkcert root CA in <paramref name="caRoot"/> sits in a
    /// trusted root store. mkcert creates rootCA.pem on first use, so the file
    /// alone does not mean browsers trust it.
    /// </summary>
    internal static bool IsCaTrusted(string caRoot)
    {
        var pem = Path.Combine(caRoot, "rootCA.pem");
        if (!File.Exists(pem)) return false;
        try
        {
            using var ca = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificateFromFile(pem);
            foreach (var location in new[] { System.Security.Cryptography.X509Certificates.StoreLocation.CurrentUser, System.Security.Cryptography.X509Certificates.StoreLocation.LocalMachine })
            {
                using var store = new System.Security.Cryptography.X509Certificates.X509Store(
                    System.Security.Cryptography.X509Certificates.StoreName.Root, location);
                try { store.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadOnly | System.Security.Cryptography.X509Certificates.OpenFlags.OpenExistingOnly); }
                catch (System.Security.Cryptography.CryptographicException) { continue; }
                var found = store.Certificates.Find(
                    System.Security.Cryptography.X509Certificates.X509FindType.FindByThumbprint, ca.Thumbprint, validOnly: false);
                var hit = found.Count > 0;
                foreach (var c in found) c.Dispose();
                if (hit) return true;
            }
        }
        catch (System.Security.Cryptography.CryptographicException) { /* unreadable rootCA.pem */ }
        return false;
    }

    /// <summary>
    /// The SSL plugin looks for mkcert only when it starts. After mkcert is
    /// installed from Binaries, ask it to look again so generating works
    /// without a daemon restart. Returns whether the plugin now has mkcert.
    /// </summary>
    private static async Task<bool> EnsurePluginSeesMkcertAsync(object plugin, bool binaryInstalled)
    {
        var manager = plugin.GetType()
            .GetField("_mkcert", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.GetValue(plugin);
        if (manager is null) return binaryInstalled;
        bool Ready() => manager.GetType().GetProperty("IsInstalled")?.GetValue(manager) as bool? ?? binaryInstalled;
        if (Ready() || !binaryInstalled) return Ready();
        try
        {
            if (manager.GetType().GetMethod("DetectAsync")?.Invoke(manager, null) is Task<bool> detect && await detect)
                plugin.GetType()
                    .GetMethod("ScanExistingCerts", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                    ?.Invoke(plugin, null);
        }
        catch (Exception) { /* plugin shape changed; report what it says below */ }
        return Ready();
    }

    public static void MapSslEndpoints(this WebApplication app, PluginLoader pluginLoader)
    {
        // SSL certificates. mkcertInstalled/caInstalled come from the machine,
        // not from the plugin merely being loaded.
        app.MapGet("/api/ssl/certs", async (SiteManager sm, NKS.WebDevConsole.Daemon.Binaries.BinaryManager bm) =>
        {
            var binaryInstalled = bm.ListInstalled("mkcert").Count > 0;
            var caInstalled = IsCaTrusted(MkcertCaRoot());
            var sslPlugin = pluginLoader.Plugins.FirstOrDefault(p => p.Instance.Id == "nks.wdc.ssl");
            if (sslPlugin == null)
                return Results.Ok(new { certs = Array.Empty<object>(), pluginLoaded = false, mkcertInstalled = binaryInstalled, caInstalled });
            var mkcertInstalled = await EnsurePluginSeesMkcertAsync(sslPlugin.Instance, binaryInstalled);
            var method = sslPlugin.Instance.GetType().GetMethod("GetCerts");
            if (method == null)
                return Results.Ok(new { certs = Array.Empty<object>(), pluginLoaded = true, mkcertInstalled, caInstalled });

            var rawResult = method.Invoke(sslPlugin.Instance, null);
            var certValues = new List<object>();
            if (rawResult is System.Collections.IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    var kvp = item.GetType();
                    var valProp = kvp.GetProperty("Value");
                    certValues.Add(valProp != null ? valProp.GetValue(item)! : item);
                }
            }

            // F81: enrich each CertInfo with live X.509 metadata (NotAfterUtc,
            // Issuer, Fingerprint) parsed from disk + orphan flag (site with the
            // cert's domain no longer exists) + expiring flag (<=14 days to
            // expiry). We build a serialization-friendly dict so we don't need
            // a shared DTO type across the plugin ALC boundary.
            var knownDomains = sm.Sites.Values
                .SelectMany(s => new[] { s.Domain }.Concat(s.Aliases ?? []))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var enriched = new List<object>();
            foreach (var cert in certValues)
            {
                var t = cert.GetType();
                string domain = t.GetProperty("Domain")?.GetValue(cert) as string ?? "";
                string certPath = t.GetProperty("CertPath")?.GetValue(cert) as string ?? "";
                string keyPath = t.GetProperty("KeyPath")?.GetValue(cert) as string ?? "";
                DateTime createdUtc = (DateTime)(t.GetProperty("CreatedUtc")?.GetValue(cert) ?? DateTime.UtcNow);
                var aliases = (t.GetProperty("Aliases")?.GetValue(cert) as string[]) ?? Array.Empty<string>();

                DateTime? notAfterUtc = t.GetProperty("NotAfterUtc")?.GetValue(cert) as DateTime?;
                string? issuer = t.GetProperty("Issuer")?.GetValue(cert) as string;
                string? fingerprint = t.GetProperty("Fingerprint")?.GetValue(cert) as string;

                if (notAfterUtc is null && File.Exists(certPath))
                {
                    try
                    {
                        // X509Certificate2 is IDisposable — the loader returns one
                        // with a CAPI/ncrypt handle attached. Without `using` the
                        // handle was kept alive until GC, which on Windows retains
                        // a kernel certificate context per SSL list call.
                        using var x509 = System.Security.Cryptography.X509Certificates.X509CertificateLoader
                            .LoadCertificateFromFile(certPath);
                        notAfterUtc = x509.NotAfter.ToUniversalTime();
                        issuer = x509.Issuer;
                        fingerprint = x509.Thumbprint;
                    }
                    catch { /* cert unreadable — surface without metadata */ }
                }

                int? daysToExpiry = notAfterUtc.HasValue
                    ? (int)Math.Floor((notAfterUtc.Value - DateTime.UtcNow).TotalDays)
                    : (int?)null;
                bool expiring = daysToExpiry.HasValue && daysToExpiry.Value <= 14;
                bool expired = daysToExpiry.HasValue && daysToExpiry.Value < 0;
                bool orphan = !knownDomains.Contains(domain);

                enriched.Add(new
                {
                    domain,
                    certPath,
                    keyPath,
                    createdUtc,
                    aliases,
                    notAfterUtc,
                    issuer,
                    fingerprint,
                    daysToExpiry,
                    expiring,
                    expired,
                    orphan,
                });
            }
            return Results.Ok(new { certs = enriched, pluginLoaded = true, mkcertInstalled, caInstalled });
        });

        app.MapPost("/api/ssl/install-ca", async () =>
        {
            var sslPlugin = pluginLoader.Plugins.FirstOrDefault(p => p.Instance.Id == "nks.wdc.ssl");
            if (sslPlugin == null) return Results.BadRequest(new { ok = false, code = "plugin_not_loaded", message = "SSL plugin not loaded" });
            var method = sslPlugin.Instance.GetType().GetMethod("InstallCA");
            if (method == null) return Results.BadRequest(new { ok = false, message = "InstallCA method not found" });
            try
            {
                if (method.Invoke(sslPlugin.Instance, null) is not Task<bool> task)
                    return Results.BadRequest(new { ok = false, message = "InstallCA returned unexpected type" });
                var success = await task;
                return success
                    ? Results.Ok(new { ok = true, message = "CA installed" })
                    : Results.BadRequest(new { ok = false, code = "ca_install_failed", message = "Failed to install CA" });
            }
            catch (Exception ex)
            {
                app.Logger.LogWarning(ex, "InstallCA reflection failed");
                return Results.BadRequest(new { ok = false, code = "ca_install_failed", message = $"InstallCA failed: {ex.InnerException?.Message ?? ex.Message}" });
            }
        });

        app.MapPost("/api/ssl/generate", async (HttpContext ctx) =>
        {
            Dictionary<string, object>? body;
            try
            {
                body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, object>>();
            }
            catch (System.Text.Json.JsonException ex)
            {
                return Results.BadRequest(new { ok = false, message = $"Invalid JSON body: {ex.Message}" });
            }
            if (body == null || !body.ContainsKey("domain"))
                return Results.BadRequest(new { ok = false, message = "domain required" });

            var domain = body["domain"]?.ToString() ?? "";
            var aliases = Array.Empty<string>();
            if (body.TryGetValue("aliases", out var aliasesObj) && aliasesObj is JsonElement aliasArr && aliasArr.ValueKind == JsonValueKind.Array)
                aliases = aliasArr.EnumerateArray().Select(a => a.GetString() ?? "").Where(s => s.Length > 0).ToArray();

            var sslPlugin = pluginLoader.Plugins.FirstOrDefault(p => p.Instance.Id == "nks.wdc.ssl");
            if (sslPlugin == null) return Results.BadRequest(new { ok = false, code = "plugin_not_loaded", message = "SSL plugin not loaded" });

            var method = sslPlugin.Instance.GetType().GetMethod("GenerateCert");
            if (method == null) return Results.BadRequest(new { ok = false, message = "GenerateCert method not found" });

            try
            {
                var task = (Task)method.Invoke(sslPlugin.Instance, new object[] { domain, aliases })!;
                await task;
                var resultProp = task.GetType().GetProperty("Result");
                var result = resultProp?.GetValue(task);

                if (result == null)
                    return Results.BadRequest(new { ok = false, code = "generate_failed", message = "mkcert not installed or failed" });

                return Results.Ok(new { ok = true, domain, message = $"Certificate generated for {domain}" });
            }
            catch (Exception ex)
            {
                // mkcert exec failure, missing binary, perms — surface the inner
                // cause from reflection's TargetInvocationException wrapper.
                return Results.Json(new { ok = false, code = "generate_failed",
                    message = $"Certificate generation failed: {ex.InnerException?.Message ?? ex.Message}" }, statusCode: 500);
            }
        });

        // MCP intent-gated under kind=ssl_cert_delete. Removing a cert without
        // re-provisioning breaks HTTPS for the whole domain — same risk class
        // as DNS deletion. Header-driven, GUI unaffected.
        app.MapDelete("/api/ssl/certs/{domain}", async (
            string domain,
            NKS.WebDevConsole.Core.Interfaces.IDeployIntentValidator intentValidator,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var sslIntentToken = ctx.Request.Headers["X-Intent-Token"].FirstOrDefault();
            if (!string.IsNullOrEmpty(sslIntentToken) || NKS.WebDevConsole.Daemon.Mcp.McpCallerContext.IsMcpToken)
            {
                var sslAllowUnconfirmed = string.Equals(
                    ctx.Request.Headers["X-Allow-Unconfirmed"].FirstOrDefault(), "true",
                    StringComparison.OrdinalIgnoreCase);
                var sslVerdict = await intentValidator.ValidateAndConsumeAsync(
                    sslIntentToken ?? "", "ssl_cert_delete", domain, host: "*ssl*", sslAllowUnconfirmed, ct);
                if (!sslVerdict.Ok)
                    return Results.Json(
                        new { error = "intent_rejected", reason = sslVerdict.Reason, detail = sslVerdict.Detail },
                        statusCode: sslVerdict.Reason == "pending_confirmation" ? 425 : 403);
            }

            var sslPlugin = pluginLoader.Plugins.FirstOrDefault(p => p.Instance.Id == "nks.wdc.ssl");
            if (sslPlugin == null) return Results.BadRequest(new { ok = false, code = "plugin_not_loaded", message = "SSL plugin not loaded" });
            var method = sslPlugin.Instance.GetType().GetMethod("RevokeCert");
            if (method == null) return Results.BadRequest(new { ok = false, message = "RevokeCert not found" });
            try
            {
                var success = (bool)method.Invoke(sslPlugin.Instance, new object[] { domain })!;
                return success
                    ? Results.Ok(new { ok = true, message = $"Certificate for {domain} revoked" })
                    : Results.NotFound(new { ok = false, code = "not_found", message = $"No certificate found for {domain}" });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, code = "revoke_failed",
                    message = $"Certificate revoke failed: {ex.InnerException?.Message ?? ex.Message}" }, statusCode: 500);
            }
        });
    }
}
