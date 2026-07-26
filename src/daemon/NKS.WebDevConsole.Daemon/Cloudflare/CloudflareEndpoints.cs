using NKS.WebDevConsole.Daemon.Plugin;
using NKS.WebDevConsole.Daemon.Sites;

namespace NKS.WebDevConsole.Daemon.Cloudflare;

/// <summary>
/// Route registrations for the /api/cloudflare/* passthrough surface.
///
/// Lifted out of Program.cs verbatim. The plugin lives in its own
/// AssemblyLoadContext, so every handler reaches it by reflection through the
/// shared IServiceProvider rather than a compile-time reference — that is why
/// these endpoints deal in raw JSON blobs instead of typed DTOs.
/// </summary>
internal static class CloudflareEndpoints
{
    /// <summary>
    /// Resolves a type from the Cloudflare plugin's ALC by simple name.
    /// Internal rather than a local function because the Simple Mode
    /// site-create path in Program.cs resolves CloudflareConfig the same way.
    /// </summary>
    internal static object? ResolveServiceOrNull(
        PluginLoader pluginLoader, IServiceProvider sp, string typeName)
    {
        var plugin = pluginLoader.Plugins
            .FirstOrDefault(p => p.Instance.Id == "nks.wdc.cloudflare");
        if (plugin == null) return null;
        var t = plugin.Assembly.GetTypes().FirstOrDefault(x => x.Name == typeName);
        if (t == null) return null;
        return sp.GetService(t);
    }

    public static void MapCloudflareEndpoints(this WebApplication app, PluginLoader pluginLoader)
    {
        object? ResolveCloudflareServiceOrNull(IServiceProvider sp, string typeName)
            => ResolveServiceOrNull(pluginLoader, sp, typeName);

        async Task<IResult> InvokeCfAsync(string apiMethodName, object[] args, IServiceProvider sp)
        {
            var api = ResolveCloudflareServiceOrNull(sp, "CloudflareApi");
            if (api == null) return Results.NotFound(new { error = "Cloudflare plugin not loaded" });
            var method = api.GetType().GetMethod(apiMethodName);
            if (method == null) return Results.NotFound(new { error = $"Method {apiMethodName} not found" });
            try
            {
                var task = (Task)method.Invoke(api, args)!;
                await task.ConfigureAwait(false);
                var resultProp = task.GetType().GetProperty("Result");
                var value = resultProp?.GetValue(task);
                return Results.Ok(value);
            }
            catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException is not null)
            {
                return Results.BadRequest(new { error = tie.InnerException.Message });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }

        // Settings: GET returns redacted config (secrets masked), PUT saves
        app.MapGet("/api/cloudflare/config", (IServiceProvider sp) =>
        {
            var cfg = ResolveCloudflareServiceOrNull(sp, "CloudflareConfig");
            if (cfg == null) return Results.NotFound(new { error = "Cloudflare plugin not loaded" });
            var redactedMethod = cfg.GetType().GetMethod("Redacted");
            var redacted = redactedMethod?.Invoke(cfg, null);
            return Results.Ok(redacted);
        });

        app.MapPut("/api/cloudflare/config", async (HttpContext ctx, IServiceProvider sp) =>
        {
            var cfg = ResolveCloudflareServiceOrNull(sp, "CloudflareConfig");
            if (cfg == null) return Results.NotFound(new { error = "Cloudflare plugin not loaded" });

            // Accept a loose JSON body and copy known properties onto the live
            // config instance. Fields omitted from the body stay untouched.
            System.Text.Json.JsonDocument doc;
            try
            {
                doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body);
            }
            catch (System.Text.Json.JsonException ex)
            {
                // Malformed JSON in the body — return 400 with the parser's
                // line/column hint instead of letting the auth middleware
                // surface a generic 500 stack trace from the unhandled throw.
                return Results.BadRequest(new { error = $"Invalid JSON body: {ex.Message}" });
            }

            using (doc)
            {
                var root = doc.RootElement;
                var t = cfg.GetType();

                void Apply(string jsonKey, string propName)
                {
                    if (!root.TryGetProperty(jsonKey, out var el)) return;
                    if (el.ValueKind != System.Text.Json.JsonValueKind.String &&
                        el.ValueKind != System.Text.Json.JsonValueKind.Null) return;
                    var prop = t.GetProperty(propName);
                    prop?.SetValue(cfg, el.ValueKind == System.Text.Json.JsonValueKind.Null ? null : el.GetString());
                }

                Apply("cloudflaredPath", "CloudflaredPath");
                Apply("tunnelToken", "TunnelToken");
                Apply("tunnelName", "TunnelName");
                Apply("tunnelId", "TunnelId");
                Apply("apiToken", "ApiToken");
                Apply("accountId", "AccountId");
                Apply("defaultZoneId", "DefaultZoneId");
                Apply("subdomainTemplate", "SubdomainTemplate");

                // Save can fail with IOException (disk full, perms) — surface that
                // as a 500 with the actual cause instead of a stack trace.
                try
                {
                    var saveMethod = t.GetMethod("Save");
                    saveMethod?.Invoke(cfg, null);
                }
                catch (Exception ex)
                {
                    return Results.Problem($"Failed to persist Cloudflare config: {ex.InnerException?.Message ?? ex.Message}");
                }

                var redacted = t.GetMethod("Redacted")?.Invoke(cfg, null);
                return Results.Ok(redacted);
            }
        });

        app.MapGet("/api/cloudflare/verify", (IServiceProvider sp) =>
            InvokeCfAsync("VerifyTokenAsync", new object[] { CancellationToken.None }, sp));

        // Compute a suggested public subdomain for a local domain. The Cloudflare
        // plugin owns the template + install-salt hash logic, so SiteEdit can show
        // a stable auto-filled value like "myapp-bffa44" that doesn't drift.
        app.MapGet("/api/cloudflare/suggest-subdomain", (string domain, IServiceProvider sp) =>
        {
            if (string.IsNullOrWhiteSpace(domain))
                return Results.BadRequest(new { error = "domain query param required" });
            var cfg = ResolveCloudflareServiceOrNull(sp, "CloudflareConfig");
            if (cfg == null)
                return Results.NotFound(new { error = "Cloudflare plugin not loaded" });
            var renderMethod = cfg.GetType().GetMethod("RenderSubdomain");
            if (renderMethod == null)
                return Results.Problem("RenderSubdomain not available");
            var suggestion = renderMethod.Invoke(cfg, new object[] { domain }) as string;
            return Results.Ok(new { suggestion, domain });
        });

        // One-token auto-setup: user pastes an API token → we verify it, list
        // accounts, pick the first one, find or create a tunnel named
        // NKS-WDC-Tunnel-{md5[..12]}, fetch its JWT, and persist everything.
        // After this the user never has to enter an account/tunnel/jwt manually —
        // zones + per-site configuration are the only remaining inputs.
        app.MapPost("/api/cloudflare/auto-setup", async (HttpContext ctx, IServiceProvider sp) =>
        {
            var cfg = ResolveCloudflareServiceOrNull(sp, "CloudflareConfig");
            var api = ResolveCloudflareServiceOrNull(sp, "CloudflareApi");
            if (cfg == null || api == null)
                return Results.NotFound(new { error = "Cloudflare plugin not loaded" });

            System.Text.Json.JsonDocument doc;
            try
            {
                doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body);
            }
            catch (System.Text.Json.JsonException ex)
            {
                return Results.BadRequest(new { error = $"Invalid JSON body: {ex.Message}" });
            }
            using var _doc = doc;
            if (!doc.RootElement.TryGetProperty("apiToken", out var tokenEl) ||
                tokenEl.ValueKind != System.Text.Json.JsonValueKind.String ||
                string.IsNullOrWhiteSpace(tokenEl.GetString()))
            {
                return Results.BadRequest(new { error = "apiToken is required and must be a non-empty string" });
            }
            var token = tokenEl.GetString()!;

            // 1. Stage the token onto the live config so the API wrapper picks it up.
            var tCfg = cfg.GetType();
            tCfg.GetProperty("ApiToken")?.SetValue(cfg, token);

            try
            {
                // 2. Verify — fail fast with a readable error if the token is wrong
                var verifyMethod = api.GetType().GetMethod("VerifyTokenAsync");
                var verifyTask = (Task)verifyMethod!.Invoke(api, new object[] { CancellationToken.None })!;
                await verifyTask;

                // 3. Pick account — use first returned one, or fall back to whatever
                //    the user already had saved (lets power-users override)
                var listAccounts = api.GetType().GetMethod("ListAccountsAsync");
                var accountsTask = (Task)listAccounts!.Invoke(api, new object[] { CancellationToken.None })!;
                await accountsTask;
                var accountsJson = (System.Text.Json.JsonElement)accountsTask.GetType().GetProperty("Result")!.GetValue(accountsTask)!;
                string? accountId = null;
                string? accountName = null;
                if (accountsJson.TryGetProperty("result", out var arr) &&
                    arr.ValueKind == System.Text.Json.JsonValueKind.Array &&
                    arr.GetArrayLength() > 0)
                {
                    accountId = arr[0].GetProperty("id").GetString();
                    accountName = arr[0].GetProperty("name").GetString();
                }
                if (string.IsNullOrEmpty(accountId))
                    return Results.BadRequest(new { error = "Token has no associated accounts — add Account read scope" });

                tCfg.GetProperty("AccountId")?.SetValue(cfg, accountId);

                // 4. Deterministic tunnel name — same token always resolves to the
                //    same tunnel so repeated auto-setup runs don't create dupes.
                var md5 = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(token));
                var md5Hex = Convert.ToHexString(md5).ToLowerInvariant()[..12];
                var tunnelName = $"NKS-WDC-Tunnel-{md5Hex}";

                var findOrCreate = api.GetType().GetMethod("FindOrCreateTunnelAsync");
                var tunnelTask = (Task)findOrCreate!.Invoke(api, new object[] { tunnelName, CancellationToken.None })!;
                await tunnelTask;
                var tunnelJson = (System.Text.Json.JsonElement)tunnelTask.GetType().GetProperty("Result")!.GetValue(tunnelTask)!;
                var tunnelId = tunnelJson.GetProperty("id").GetString();

                tCfg.GetProperty("TunnelId")?.SetValue(cfg, tunnelId);
                tCfg.GetProperty("TunnelName")?.SetValue(cfg, tunnelName);

                // 5. Fetch JWT — cloudflared needs this to run the tunnel locally
                var getToken = api.GetType().GetMethod("GetTunnelTokenAsync");
                var jwtTask = (Task)getToken!.Invoke(api, new object[] { tunnelId!, CancellationToken.None })!;
                await jwtTask;
                var jwt = (string?)jwtTask.GetType().GetProperty("Result")!.GetValue(jwtTask);
                if (!string.IsNullOrEmpty(jwt))
                    tCfg.GetProperty("TunnelToken")?.SetValue(cfg, jwt);

                // 6. Persist everything
                tCfg.GetMethod("Save")?.Invoke(cfg, null);

                return Results.Ok(new
                {
                    ok = true,
                    account = new { id = accountId, name = accountName },
                    tunnel = new { id = tunnelId, name = tunnelName },
                    tokenFetched = !string.IsNullOrEmpty(jwt),
                });
            }
            catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException is not null)
            {
                return Results.BadRequest(new { error = tie.InnerException.Message });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // Sync all sites that have Cloudflare.Enabled=true:
        //   - Upserts a proxied CNAME per site → tunnelId.cfargotunnel.com
        //   - Rebuilds the tunnel ingress config with one rule per site + 404
        //   - Each ingress rule carries httpHostHeader = site.Domain so Apache
        //     matches the LOCAL vhost rather than the public hostname
        app.MapPost("/api/cloudflare/sync", async (IServiceProvider sp, SiteManager sm) =>
        {
            var cfg = ResolveCloudflareServiceOrNull(sp, "CloudflareConfig");
            var api = ResolveCloudflareServiceOrNull(sp, "CloudflareApi");
            if (cfg == null || api == null)
                return Results.NotFound(new { error = "Cloudflare plugin not loaded" });

            var tCfg = cfg.GetType();
            var tunnelId = tCfg.GetProperty("TunnelId")?.GetValue(cfg) as string;
            if (string.IsNullOrWhiteSpace(tunnelId))
                return Results.BadRequest(new { error = "Tunnel not configured. Run auto-setup first." });

            var sitesWithCf = sm.Sites.Values
                .Where(s => s.Cloudflare is { Enabled: true }
                         && !string.IsNullOrWhiteSpace(s.Cloudflare.ZoneId)
                         && !string.IsNullOrWhiteSpace(s.Cloudflare.Subdomain))
                .ToList();
            var dormantCf = sm.Sites.Values
                .Where(s => s.Cloudflare is { Enabled: false }
                         && !string.IsNullOrWhiteSpace(s.Cloudflare.ZoneId)
                         && !string.IsNullOrWhiteSpace(s.Cloudflare.Subdomain)
                         && !string.IsNullOrWhiteSpace(s.Cloudflare.ZoneName))
                .ToList();

            var upserted = new List<object>();
            var deleted = new List<object>();
            try
            {
                // DNS: one CNAME per enabled site
                var upsertMethod = api.GetType().GetMethod("UpsertCnameToTunnelAsync");
                foreach (var s in sitesWithCf)
                {
                    var cf = s.Cloudflare!;
                    var fullName = $"{cf.Subdomain}.{cf.ZoneName}";
                    var task = (Task)upsertMethod!.Invoke(api,
                        new object[] { cf.ZoneId, fullName, tunnelId, CancellationToken.None })!;
                    await task;
                    upserted.Add(new { domain = s.Domain, cname = fullName });
                }

                // DNS: delete CNAME for dormant (disabled-but-configured) sites so
                // toggling off in SiteEdit actually takes the public hostname down.
                var deleteMethod = api.GetType().GetMethod("DeleteCnameByNameAsync");
                foreach (var s in dormantCf)
                {
                    var cf = s.Cloudflare!;
                    var fullName = $"{cf.Subdomain}.{cf.ZoneName}";
                    try
                    {
                        var task = (Task)deleteMethod!.Invoke(api,
                            new object[] { cf.ZoneId, fullName, CancellationToken.None })!;
                        await task;
                        deleted.Add(new { domain = s.Domain, cname = fullName });
                    }
                    catch { /* best-effort per-site */ }
                }

                // Ingress: one rule per site with httpHostHeader override
                var ruleType = api.GetType().Assembly.GetType(
                    "NKS.WebDevConsole.Plugin.Cloudflare.TunnelIngressRule")!;
                var ruleListType = typeof(List<>).MakeGenericType(ruleType);
                var rules = (System.Collections.IList)Activator.CreateInstance(ruleListType)!;
                var ruleCtor = ruleType.GetConstructors().First();

                foreach (var s in sitesWithCf)
                {
                    var cf = s.Cloudflare!;
                    var hostname = $"{cf.Subdomain}.{cf.ZoneName}";

                    // Mirror SiteOrchestrator.SyncCloudflareIfConfiguredAsync — pick HTTPS
                    // when the local vhost has SSL so cloudflared bypasses the Apache
                    // HTTP→HTTPS redirect. See that method for the full rationale.
                    string service;
                    string? originServerName = null;
                    bool noTLSVerify = false;
                    if (s.SslEnabled)
                    {
                        var httpsPort = s.HttpsPort > 0 ? s.HttpsPort : 443;
                        service = $"https://localhost:{httpsPort}";
                        originServerName = s.Domain;
                        noTLSVerify = true;
                    }
                    else
                    {
                        var httpPort = s.HttpPort > 0 ? s.HttpPort : 80;
                        service = $"http://localhost:{httpPort}";
                    }

                    rules.Add(ruleCtor.Invoke(new object?[]
                    {
                        hostname, service, s.Domain, originServerName, noTLSVerify,
                    }));
                }

                var ingressMethod = api.GetType().GetMethod("UpdateTunnelIngressAsync");
                var ingressTask = (Task)ingressMethod!.Invoke(api,
                    new object[] { tunnelId, rules, CancellationToken.None })!;
                await ingressTask;

                return Results.Ok(new
                {
                    ok = true,
                    synced = upserted.Count,
                    sites = upserted,
                    deleted = deleted.Count,
                    dormant = deleted,
                });
            }
            catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException is not null)
            {
                return Results.BadRequest(new { error = tie.InnerException.Message });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapGet("/api/cloudflare/zones", (IServiceProvider sp) =>
            InvokeCfAsync("ListZonesAsync", new object[] { CancellationToken.None }, sp));

        app.MapGet("/api/cloudflare/zones/{zoneId}", (string zoneId, IServiceProvider sp) =>
            InvokeCfAsync("GetZoneAsync", new object[] { zoneId, CancellationToken.None }, sp));

        app.MapGet("/api/cloudflare/zones/{zoneId}/dns", (string zoneId, IServiceProvider sp) =>
            InvokeCfAsync("ListDnsRecordsAsync", new object[] { zoneId, CancellationToken.None }, sp));

        app.MapPost("/api/cloudflare/zones/{zoneId}/dns", async (string zoneId, HttpContext ctx, IServiceProvider sp) =>
        {
            System.Text.Json.JsonDocument doc;
            try
            {
                doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body);
            }
            catch (System.Text.Json.JsonException ex)
            {
                return Results.BadRequest(new { error = $"Invalid JSON body: {ex.Message}" });
            }
            using (doc)
            {
                var root = doc.RootElement;
                try
                {
                    // Each TryGetProperty + typed getter pair can throw InvalidOperationException
                    // if the value is the wrong shape (e.g. proxied="yes" instead of bool true).
                    // Wrap the whole shape coercion so type mismatches surface as 400 with a
                    // useful hint instead of bubbling to the auth middleware as 500.
                    var type = root.TryGetProperty("type", out var tEl) && tEl.ValueKind == System.Text.Json.JsonValueKind.String
                        ? tEl.GetString() ?? "CNAME" : "CNAME";
                    var name = root.TryGetProperty("name", out var nEl) && nEl.ValueKind == System.Text.Json.JsonValueKind.String
                        ? nEl.GetString() ?? "" : "";
                    var content = root.TryGetProperty("content", out var cEl) && cEl.ValueKind == System.Text.Json.JsonValueKind.String
                        ? cEl.GetString() ?? "" : "";
                    var proxied = !root.TryGetProperty("proxied", out var pEl) ||
                                  (pEl.ValueKind == System.Text.Json.JsonValueKind.True);
                    var ttl = root.TryGetProperty("ttl", out var tEl2) && tEl2.ValueKind == System.Text.Json.JsonValueKind.Number
                        ? tEl2.GetInt32() : 1;
                    return await InvokeCfAsync("CreateDnsRecordAsync",
                        new object[] { zoneId, type, name, content, proxied, ttl, CancellationToken.None }, sp);
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { error = $"Invalid DNS record shape: {ex.Message}" });
                }
            }
        });

        // MCP intent-gated under kind=dns_record_delete. Removing a DNS record
        // breaks the public-facing route to a site; an AI with a wildcard grant
        // could orphan every site by chaining this. Header-driven so the GUI
        // delete button keeps working unchanged.
        app.MapDelete("/api/cloudflare/zones/{zoneId}/dns/{recordId}", async (
            string zoneId,
            string recordId,
            IServiceProvider sp,
            NKS.WebDevConsole.Core.Interfaces.IDeployIntentValidator intentValidator,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var dnsIntentToken = ctx.Request.Headers["X-Intent-Token"].FirstOrDefault();
            if (!string.IsNullOrEmpty(dnsIntentToken))
            {
                var dnsAllowUnconfirmed = string.Equals(
                    ctx.Request.Headers["X-Allow-Unconfirmed"].FirstOrDefault(), "true",
                    StringComparison.OrdinalIgnoreCase);
                var dnsVerdict = await intentValidator.ValidateAndConsumeAsync(
                    dnsIntentToken, "dns_record_delete", domain: zoneId, host: "*dns*", dnsAllowUnconfirmed, ct);
                if (!dnsVerdict.Ok)
                    return Results.Json(
                        new { error = "intent_rejected", reason = dnsVerdict.Reason, detail = dnsVerdict.Detail },
                        statusCode: dnsVerdict.Reason == "pending_confirmation" ? 425 : 403);
            }
            return await InvokeCfAsync("DeleteDnsRecordAsync", new object[] { zoneId, recordId, CancellationToken.None }, sp);
        });

        app.MapGet("/api/cloudflare/tunnels", (IServiceProvider sp) =>
            InvokeCfAsync("ListTunnelsAsync", new object[] { CancellationToken.None }, sp));

        app.MapGet("/api/cloudflare/tunnels/{tunnelId}/configuration", (string tunnelId, IServiceProvider sp) =>
            InvokeCfAsync("GetTunnelConfigurationAsync", new object[] { tunnelId, CancellationToken.None }, sp));

        // Replace the tunnel's ingress rules. Body shape:
        //   { "rules": [ { "hostname": "blog.nks-dev.cz", "service": "http://localhost:80" }, ... ] }
        // CloudflareApi.UpdateTunnelIngressAsync appends the mandatory catch-all
        // 404 rule automatically so callers don't have to know the protocol detail.
        app.MapPut("/api/cloudflare/tunnels/{tunnelId}/configuration",
            async (string tunnelId, HttpContext ctx, IServiceProvider sp) =>
        {
            var api = ResolveCloudflareServiceOrNull(sp, "CloudflareApi");
            if (api == null) return Results.NotFound(new { error = "Cloudflare plugin not loaded" });

            System.Text.Json.JsonDocument doc;
            try
            {
                doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body);
            }
            catch (System.Text.Json.JsonException ex)
            {
                return Results.BadRequest(new { error = $"Invalid JSON body: {ex.Message}" });
            }
            using var _doc = doc;
            if (!doc.RootElement.TryGetProperty("rules", out var rulesEl) ||
                rulesEl.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return Results.BadRequest(new { error = "Missing 'rules' array" });
            }

            // Build the plugin's TunnelIngressRule record via reflection so we don't
            // need a direct type reference into the plugin's ALC.
            var ruleType = api.GetType().Assembly.GetType(
                "NKS.WebDevConsole.Plugin.Cloudflare.TunnelIngressRule");
            if (ruleType == null)
                return Results.Problem("TunnelIngressRule type not found in plugin assembly");

            var ruleListType = typeof(List<>).MakeGenericType(ruleType);
            var ruleList = (System.Collections.IList)Activator.CreateInstance(ruleListType)!;
            var ruleCtor = ruleType.GetConstructors().First();

            foreach (var el in rulesEl.EnumerateArray())
            {
                var hostname = el.TryGetProperty("hostname", out var h) ? h.GetString() ?? "" : "";
                var service = el.TryGetProperty("service", out var s) ? s.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(hostname) || string.IsNullOrEmpty(service)) continue;
                ruleList.Add(ruleCtor.Invoke(new object[] { hostname, service }));
            }

            var method = api.GetType().GetMethod("UpdateTunnelIngressAsync");
            if (method == null) return Results.Problem("UpdateTunnelIngressAsync not found");

            try
            {
                var task = (Task)method.Invoke(api, new object[] { tunnelId, ruleList, CancellationToken.None })!;
                await task.ConfigureAwait(false);
                var resultProp = task.GetType().GetProperty("Result");
                return Results.Ok(resultProp?.GetValue(task));
            }
            catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException is not null)
            {
                return Results.BadRequest(new { error = tie.InnerException.Message });
            }
        });
    }
}
