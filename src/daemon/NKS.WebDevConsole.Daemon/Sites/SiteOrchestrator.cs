using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Daemon.Plugin;
using NKS.WebDevConsole.Daemon.Services;
using System.Text;

namespace NKS.WebDevConsole.Daemon.Sites;

/// <summary>
/// Coordinates per-site setup across loaded plugins: generates the Apache vhost,
/// ensures PHP is running for the requested version, and reloads Apache.
/// Calls into plugin types via reflection because plugins are loaded into
/// isolated <see cref="System.Runtime.Loader.AssemblyLoadContext"/> instances.
/// </summary>
public sealed class SiteOrchestrator
{
    internal const string HostsBlockBegin = "# BEGIN NKS WebDev Console";
    internal const string HostsBlockEnd = "# END NKS WebDev Console";
    // UTF-8 without BOM: ASCII rewrote non-ASCII text in the user's own hosts
    // lines (comments in Czech, IDN notes) as '?'.
    private static readonly Encoding HostsFileEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly ILogger<SiteOrchestrator> _logger;
    private readonly IServiceProvider _sp;

    // One gate per domain so two applies of the same site (PUT save, the
    // explicit /apply endpoint, rollback, reapply-all) cannot interleave their
    // vhost/reload/hosts steps. Different domains still apply in parallel.
    // Domains are case-insensitive, matching SiteManager's lookup.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _applyLocks =
        new(StringComparer.OrdinalIgnoreCase);

    // Latest apply outcome per site, kept for the lifetime of the daemon.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SiteApplyResult> _lastApply =
        new(StringComparer.OrdinalIgnoreCase);

    public SiteOrchestrator(ILogger<SiteOrchestrator> logger, IServiceProvider sp)
    {
        _logger = logger;
        _sp = sp;
    }

    /// <summary>The most recent apply result of every site that has been applied since the daemon started.</summary>
    public IReadOnlyCollection<SiteApplyResult> LastApplyResults => _lastApply.Values.ToList();

    /// <summary>
    /// Reloads Apache once if it is running; used after applying a batch of
    /// sites with <c>reloadWebServer: false</c>. Returns false when the reload failed.
    /// </summary>
    public async Task<bool> ReloadWebServerAsync()
    {
        var apache = _sp.GetServices<IServiceModule>()
            .FirstOrDefault(m => m.ServiceId.Equals("apache", StringComparison.OrdinalIgnoreCase));
        if (apache is null) return true;
        try
        {
            if ((await apache.GetStatusAsync(CancellationToken.None)).State != ServiceState.Running) return true;
            await apache.ReloadAsync(CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reload Apache after applying a batch of sites");
            return false;
        }
    }

    /// <summary>
    /// Applies a site config: generate Apache vhost, ensure PHP is running, reload Apache.
    /// When <see cref="SiteConfig.Enabled"/> is false, removes all active vhosts for the
    /// site instead of generating new ones, then reloads the web server. SSL cert and
    /// php.ini writing still run (no harm, they are not routed to).
    /// With <paramref name="reloadWebServer"/> false the Apache reload is left to
    /// the caller (<see cref="ReloadWebServerAsync"/>), so applying many sites
    /// restarts Apache once instead of once per site.
    /// </summary>
    public async Task<SiteApplyResult> ApplyAsync(SiteConfig site, CancellationToken ct = default, bool reloadWebServer = true)
    {
        var gate = _applyLocks.GetOrAdd(site.Domain, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return await ApplyCoreAsync(site, ct, reloadWebServer);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<SiteApplyResult> ApplyCoreAsync(SiteConfig site, CancellationToken ct, bool reloadWebServer)
    {
        _logger.LogInformation("Orchestrating site {Domain}...", site.Domain);
        var result = new SiteApplyResult(site.Domain);

        if (!site.Enabled)
        {
            var failures = await RemoveVhostsForDisabledSiteAsync(site.Domain, ct);
            foreach (var (step, detail) in failures)
                result.Fail(step, detail);
            if (!failures.Any(f => f.Step == "vhost"))
                result.Ok("vhost", "site disabled — vhosts removed");
            _logger.LogInformation("Site {Domain} is disabled — vhost removal finished ({Failed} failed step(s))",
                site.Domain, failures.Count);
            await PublishApplyResultAsync(result);
            return result;
        }

        // 0. Auto-detect PHP version from .php-version file in docroot
        // (Herd/Valet zero-config model). Only overrides when the user
        // hasn't explicitly set a version AND a .php-version file exists.
        if ((string.IsNullOrEmpty(site.PhpVersion) || site.PhpVersion == "none")
            && site.NodeUpstreamPort == 0)
        {
            var detected = SiteManager.DetectPhpVersion(site.DocumentRoot);
            if (detected is not null)
            {
                _logger.LogInformation("Auto-detected PHP {Version} from .php-version for {Domain}",
                    detected, site.Domain);
                site.PhpVersion = detected;
                // Persist so the site config reflects the auto-detected version
                var sm = _sp.GetRequiredService<SiteManager>();
                await sm.UpdateAsync(site);
            }
        }

        var modules = _sp.GetServices<IServiceModule>().ToList();

        // 1. SSL — generate certificate FIRST so the Apache vhost generator
        // can embed the cert/key paths in the rendered HTTPS VirtualHost block.
        // Previously this step ran AFTER vhost generation, which meant the
        // first apply always produced an HTTP-only vhost (cert didn't exist
        // yet) and a second reapply was required to pick up the SSL block.
        if (site.SslEnabled)
        {
            try
            {
                var pluginLoader = _sp.GetService<PluginLoader>();
                var sslPlugin = pluginLoader?.Plugins.FirstOrDefault(p => p.Instance.Id == "nks.wdc.ssl");
                if (sslPlugin != null)
                {
                    var genMethod = sslPlugin.Instance.GetType().GetMethod("GenerateCert");
                    if (genMethod != null)
                    {
                        var aliases = site.Aliases ?? Array.Empty<string>();
                        var task = (Task)genMethod.Invoke(sslPlugin.Instance, new object[] { site.Domain, aliases })!;
                        await task;
                        var resultProp = task.GetType().GetProperty("Result");
                        var certResult = resultProp?.GetValue(task);
                        if (certResult != null)
                        {
                            _logger.LogInformation("SSL certificate generated for {Domain}", site.Domain);
                            result.Ok("ssl");
                        }
                        else
                        {
                            _logger.LogWarning("SSL certificate generation returned null for {Domain} — mkcert may not be installed", site.Domain);
                            result.Fail("ssl", "certificate generation returned nothing — is mkcert installed?");
                        }
                    }
                }
                else
                {
                    _logger.LogWarning("SSL plugin not loaded — skipping cert generation for {Domain}", site.Domain);
                    result.Fail("ssl", "SSL plugin not loaded");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate SSL certificate for {Domain}", site.Domain);
                result.Fail("ssl", ex.Message);
            }
        }

        // 1c. Per-site PHP overrides — write ~/.wdc/sites-php/{domain}/php.ini
        // before the vhost is generated so the template can reference its
        // path via FcgidInitialEnv PHPRC. If PhpSettings is null the writer
        // returns null and no vhost directive is emitted. Every PHP site also
        // gets conf.d/10-wdc-site-log.ini, its own PHP error log for PHP_INI_SCAN_DIR.
        try
        {
            var phpIniWriter = _sp.GetService<SitePhpIniWriter>();
            var wroteIni = phpIniWriter?.Write(site) is not null;
            phpIniWriter?.WriteSiteLog(site);
            if (wroteIni) result.Ok("phpIni");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write per-site php.ini for {Domain}", site.Domain);
            result.Fail("phpIni", ex.Message);
        }

        // 2. Apache — generate vhost file via reflection (cross-ALC boundary).
        // Runs AFTER SSL so the template can pick up the freshly-written
        // cert/key paths and render the HTTPS VirtualHost block correctly.
        var apacheModule = modules.FirstOrDefault(m =>
            m.ServiceId.Equals("apache", StringComparison.OrdinalIgnoreCase));
        if (apacheModule is not null)
        {
            try
            {
                await InvokeAsync(apacheModule, "GenerateVhostAsync", new object[] { site, ct });
                result.Ok("vhost");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate vhost for {Domain}", site.Domain);
                result.Fail("vhost", ex.Message);
            }
        }
        else
        {
            _logger.LogWarning("Apache module not registered — skipping vhost generation");
            result.Fail("vhost", "Apache module not registered");
        }

        // 3. PHP — ensure module is running (it manages all installed versions internally)
        var phpModule = modules.FirstOrDefault(m =>
            m.ServiceId.Equals("php", StringComparison.OrdinalIgnoreCase)
            || m.ServiceId.Equals("php-cgi", StringComparison.OrdinalIgnoreCase));
        if (phpModule is not null
            && !string.IsNullOrEmpty(site.PhpVersion)
            && site.PhpVersion != "none")
        {
            try
            {
                var status = await phpModule.GetStatusAsync(ct);
                if (status.State != ServiceState.Running)
                {
                    _logger.LogInformation("Starting PHP module for site {Domain}", site.Domain);
                    await phpModule.StartAsync(ct);
                }
                result.Ok("php");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to ensure PHP module running for {Domain}", site.Domain);
                result.Fail("php", ex.Message);
            }
        }

        // 3. Reload Apache if it is running so the new vhost takes effect
        if (apacheModule is not null && !reloadWebServer)
        {
            result.Skip("reload", "Apache is reloaded once after all sites are applied");
        }
        else if (apacheModule is not null)
        {
            try
            {
                var apacheStatus = await apacheModule.GetStatusAsync(ct);
                if (apacheStatus.State == ServiceState.Running)
                {
                    // A reload is a stop + start; cancelling between them leaves Apache down.
                    await apacheModule.ReloadAsync(CancellationToken.None);
                    result.Ok("reload");
                }
                else
                {
                    result.Skip("reload", "Apache is not running");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reload Apache after applying {Domain}", site.Domain);
                result.Fail("reload", ex.Message);
            }
        }

        // 3b. Node.js — start/restart the site's Node process if NodeUpstreamPort is set
        if (site.NodeUpstreamPort > 0)
        {
            try
            {
                var nodeModule = modules.FirstOrDefault(m =>
                    m.ServiceId.Equals("node", StringComparison.OrdinalIgnoreCase));
                if (nodeModule is not null)
                {
                    // Cross-ALC: invoke StartSiteAsync(domain, documentRoot, port, startCommand, ct)
                    await InvokeAsync(nodeModule, "StartSiteAsync",
                        new object[] { site.Domain, site.DocumentRoot, site.NodeUpstreamPort, site.NodeStartCommand ?? "", ct });
                    _logger.LogInformation("Node.js process started for {Domain} on port {Port}", site.Domain, site.NodeUpstreamPort);
                    result.Ok("node");
                }
                else
                {
                    result.Fail("node", "Node.js module not registered");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start Node.js process for {Domain}", site.Domain);
                result.Fail("node", ex.Message);
            }
        }
        else
        {
            // If site was previously a Node site but switched to Static/PHP, stop the orphaned process
            var nodeModule2 = modules.FirstOrDefault(m =>
                m.ServiceId.Equals("node", StringComparison.OrdinalIgnoreCase));
            if (nodeModule2 is not null)
            {
                try
                {
                    await InvokeAsync(nodeModule2, "StopSiteAsync", new object[] { site.Domain, ct });
                }
                catch { /* best-effort cleanup */ }
            }
        }

        // 4. Hosts file — add domain + aliases to system hosts (requires elevation on Windows/macOS/Linux)
        try
        {
            var domains = new List<string> { site.Domain };
            if (site.Aliases is { Length: > 0 })
                domains.AddRange(site.Aliases);

            _logger.LogInformation("[hosts] site apply requesting {Count} entries for {Domain}: {List}",
                domains.Count, site.Domain, string.Join(", ", domains));
            switch (await UpdateHostsFileAsync(domains, ct))
            {
                case HostsUpdateOutcome.Unchanged: result.Ok("hosts", "already up to date"); break;
                case HostsUpdateOutcome.Written: result.Ok("hosts"); break;
                case HostsUpdateOutcome.Skipped: result.Skip("hosts", "NKS_WDC_SKIP_HOSTS_UAC is set"); break;
                default: result.Fail("hosts", "hosts file not updated — elevation declined or failed"); break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[hosts] site apply failed for {Domain} (may need admin elevation): {Error}",
                site.Domain, ex.Message);
            result.Fail("hosts", ex.Message);
        }

        // 5. Cloudflare Tunnel — auto-sync DNS + ingress for every site that has
        // cloudflare.enabled, so saving a site in the UI immediately pushes the
        // public hostname live without a manual "Sync all sites" click. Best-effort:
        // logs and continues if the plugin isn't loaded or the tunnel isn't set up.
        try
        {
            await SyncCloudflareIfConfiguredAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Cloudflare sync failed for {Domain}: {Error}", site.Domain, ex.Message);
            result.Fail("cloudflare", ex.Message);
        }

        _logger.LogInformation("Site {Domain} applied ({Failed} failed step(s))",
            site.Domain, result.Steps.Count(st => st.Status == SiteApplyStep.Failed));
        await PublishApplyResultAsync(result);
        return result;
    }

    /// <summary>
    /// Broadcasts the per-step outcome as <c>site:applied</c> so the UI can show
    /// which part of an apply failed (every step used to log and carry on, and
    /// the API reported success regardless).
    /// </summary>
    private async Task PublishApplyResultAsync(SiteApplyResult result)
    {
        // Remembered before the broadcast: a window that is not connected yet
        // (startup re-apply, page reload) reads it back via the apply-status list.
        _lastApply[result.Domain] = result;
        try
        {
            var sse = _sp.GetService<NKS.WebDevConsole.Daemon.Services.SseService>();
            if (sse is not null)
                await sse.BroadcastAsync("site:applied", result.ToPayload());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "site:applied broadcast failed");
        }
    }

    /// <summary>
    /// Invokes the Cloudflare plugin's sync flow via reflection when the
    /// plugin is loaded AND a tunnel is configured. Reads every site with
    /// cloudflare.enabled and rebuilds the tunnel ingress in one call so
    /// rules for OTHER sites are preserved. Called from ApplyAsync so
    /// saving any site (even one without cloudflare set) refreshes the
    /// full ingress — handles the edge case where the user disables a
    /// site's tunnel and the rule must be removed from the tunnel config.
    /// </summary>
    private async Task SyncCloudflareIfConfiguredAsync(CancellationToken ct)
    {
        var pluginLoader = _sp.GetService<PluginLoader>();
        var cfPlugin = pluginLoader?.Plugins.FirstOrDefault(p => p.Instance.Id == "nks.wdc.cloudflare");
        if (cfPlugin == null)
        {
            _logger.LogDebug("Cloudflare plugin not loaded — skipping auto-sync");
            return;
        }

        // Resolve CloudflareApi + CloudflareConfig from the plugin's DI container.
        // Both live in the plugin's AssemblyLoadContext, so we must look them up
        // by type through the plugin assembly rather than with a direct reference.
        var apiType = cfPlugin.Assembly.GetType("NKS.WebDevConsole.Plugin.Cloudflare.CloudflareApi");
        var cfgType = cfPlugin.Assembly.GetType("NKS.WebDevConsole.Plugin.Cloudflare.CloudflareConfig");
        if (apiType == null || cfgType == null)
        {
            _logger.LogDebug("Cloudflare plugin types not found");
            return;
        }

        var api = _sp.GetService(apiType);
        var cfg = _sp.GetService(cfgType);
        if (api == null || cfg == null)
        {
            _logger.LogDebug("Cloudflare services not registered in DI");
            return;
        }

        var tunnelId = cfgType.GetProperty("TunnelId")?.GetValue(cfg) as string;
        var accountId = cfgType.GetProperty("AccountId")?.GetValue(cfg) as string;
        if (string.IsNullOrWhiteSpace(tunnelId) || string.IsNullOrWhiteSpace(accountId))
        {
            _logger.LogDebug("Cloudflare tunnel not set up — skipping sync");
            return;
        }

        // Partition sites into:
        //   enabled — have a live ingress rule + a proxied CNAME
        //   dormant — previously enabled but the user just turned the toggle
        //             off. We keep the sub-config around so re-enabling is
        //             one click, but we must delete the CNAME from the zone
        //             (user asked: "deaktivace by mela zrusit i dns zaznam")
        //             so the public hostname stops resolving.
        var sm = _sp.GetRequiredService<SiteManager>();
        var enabledSites = sm.Sites.Values
            .Where(s => s.Cloudflare is { Enabled: true }
                     && !string.IsNullOrWhiteSpace(s.Cloudflare.ZoneId)
                     && !string.IsNullOrWhiteSpace(s.Cloudflare.Subdomain))
            .ToList();
        var dormantSites = sm.Sites.Values
            .Where(s => s.Cloudflare is { Enabled: false }
                     && !string.IsNullOrWhiteSpace(s.Cloudflare.ZoneId)
                     && !string.IsNullOrWhiteSpace(s.Cloudflare.Subdomain)
                     && !string.IsNullOrWhiteSpace(s.Cloudflare.ZoneName))
            .ToList();

        // Upsert CNAMEs for the currently enabled set.
        var upsertMethod = apiType.GetMethod("UpsertCnameToTunnelAsync");
        foreach (var s in enabledSites)
        {
            var cf = s.Cloudflare!;
            var fullName = $"{cf.Subdomain}.{cf.ZoneName}";
            try
            {
                var t = (Task)upsertMethod!.Invoke(api,
                    new object[] { cf.ZoneId, fullName, tunnelId, ct })!;
                await t;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("DNS upsert failed for {Domain}: {Error}", s.Domain, ex.Message);
            }
        }

        // Delete CNAMEs for the dormant set so disabling a site actually
        // takes its public hostname offline at the DNS layer, not just in
        // the tunnel routing table.
        var deleteMethod = apiType.GetMethod("DeleteCnameByNameAsync");
        foreach (var s in dormantSites)
        {
            var cf = s.Cloudflare!;
            var fullName = $"{cf.Subdomain}.{cf.ZoneName}";
            try
            {
                var t = (Task)deleteMethod!.Invoke(api,
                    new object[] { cf.ZoneId, fullName, ct })!;
                await t;
                _logger.LogInformation("Deleted CNAME {Fqdn} for deactivated site {Domain}", fullName, s.Domain);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("DNS delete failed for dormant {Domain}: {Error}", s.Domain, ex.Message);
            }
        }

        // Rebuild the ingress from the current enabled set so disabling a site
        // actually strips its rule — we can't PATCH a single rule, the whole
        // ingress array is replaced.
        var ruleType = apiType.Assembly.GetType("NKS.WebDevConsole.Plugin.Cloudflare.TunnelIngressRule")!;
        var ruleListType = typeof(List<>).MakeGenericType(ruleType);
        var ruleList = (System.Collections.IList)Activator.CreateInstance(ruleListType)!;
        var ruleCtor = ruleType.GetConstructors().First();
        foreach (var s in enabledSites)
        {
            var cf = s.Cloudflare!;
            var hostname = $"{cf.Subdomain}.{cf.ZoneName}";

            // Choose between http://localhost:{httpPort} and https://localhost:{httpsPort}
            // based on whether the local vhost has SSL enabled. Critical: sites with
            // sslEnabled have an Apache HTTP→HTTPS redirect (`RewriteRule ^(.*)$
            // https://%{HTTP_HOST}$1 [R=301,L]`). If cloudflared forwards to plain HTTP,
            // Apache bounces every request to https://{local-domain} which is not
            // publicly reachable — visitor gets a redirect loop + DNS error.
            // Going straight to local HTTPS with noTLSVerify + originServerName sends
            // mkcert-signed traffic directly to the HTTPS vhost, skipping the redirect.
            string service;
            string? originServerName = null;
            bool noTLSVerify = false;

            if (s.SslEnabled)
            {
                var httpsPort = s.HttpsPort > 0 ? s.HttpsPort : 443;
                service = $"https://localhost:{httpsPort}";
                originServerName = s.Domain;
                noTLSVerify = true; // mkcert-signed certs aren't publicly trusted
            }
            else
            {
                var httpPort = s.HttpPort > 0 ? s.HttpPort : 80;
                service = $"http://localhost:{httpPort}";
            }

            // Ctor signature: (Hostname, Service, HttpHostHeader?, OriginServerName?, NoTLSVerify)
            ruleList.Add(ruleCtor.Invoke(new object?[]
            {
                hostname, service, s.Domain, originServerName, noTLSVerify,
            }));
        }

        var ingressMethod = apiType.GetMethod("UpdateTunnelIngressAsync");
        var ingressTask = (Task)ingressMethod!.Invoke(api, new object[] { tunnelId, ruleList, ct })!;
        await ingressTask;

        _logger.LogInformation("Cloudflare sync complete: {Count} rule(s) pushed", enabledSites.Count);
    }

    /// <summary>
    /// Removes generated vhosts for a disabled site across all registered httpd modules
    /// (Apache, Nginx) and reloads each running web server so the config takes effect.
    /// Does NOT delete the TOML file — the site remains in the sites list.
    /// Returns the failed steps (<c>vhost</c> or <c>reload</c>, with a detail
    /// naming the module) so the caller can report them instead of claiming
    /// the vhosts were removed.
    /// </summary>
    private async Task<List<(string Step, string Detail)>> RemoveVhostsForDisabledSiteAsync(string domain, CancellationToken ct)
    {
        var failures = new List<(string Step, string Detail)>();
        var modules = _sp.GetServices<IServiceModule>().ToList();

        var httpdIds = new[] { "apache", "nginx" };
        foreach (var id in httpdIds)
        {
            var mod = modules.FirstOrDefault(m =>
                m.ServiceId.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (mod is null) continue;

            try
            {
                await InvokeAsync(mod, "RemoveVhostAsync", new object[] { domain, ct });
            }
            catch (MissingMethodException)
            {
                _logger.LogDebug("{Module} has no RemoveVhostAsync — skipping vhost removal", id);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to remove vhost for disabled site {Domain} ({Module})", domain, id);
                failures.Add(("vhost", $"{id}: {ex.Message}"));
            }

            try
            {
                var status = await mod.GetStatusAsync(ct);
                if (status.State == ServiceState.Running)
                    await mod.ReloadAsync(CancellationToken.None);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reload {Module} after disabling {Domain}", id, domain);
                failures.Add(("reload", $"{id}: {ex.Message}"));
            }
        }

        return failures;
    }

    /// <summary>
    /// Removes a site: delete Apache vhost and reload Apache.
    /// </summary>
    public async Task RemoveAsync(string domain, CancellationToken ct = default)
    {
        _logger.LogInformation("Orchestrating removal of site {Domain}...", domain);
        _lastApply.TryRemove(domain, out _);

        var modules = _sp.GetServices<IServiceModule>().ToList();
        var apacheModule = modules.FirstOrDefault(m =>
            m.ServiceId.Equals("apache", StringComparison.OrdinalIgnoreCase));

        if (apacheModule is not null)
        {
            try
            {
                await InvokeAsync(apacheModule, "RemoveVhostAsync", new object[] { domain, ct });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to remove vhost for {Domain}", domain);
            }

            try
            {
                var apacheStatus = await apacheModule.GetStatusAsync(ct);
                if (apacheStatus.State == ServiceState.Running)
                {
                    await apacheModule.ReloadAsync(CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reload Apache after removing {Domain}", domain);
            }
        }

        // Stop any Node.js process for the removed site
        var nodeModule = modules.FirstOrDefault(m =>
            m.ServiceId.Equals("node", StringComparison.OrdinalIgnoreCase));
        if (nodeModule is not null)
        {
            try
            {
                await InvokeAsync(nodeModule, "RemoveSiteAsync", new object[] { domain, ct });
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Node process cleanup for removed site {Domain}", domain);
            }
        }

        // Strip the removed site's rule from the tunnel ingress so a stale
        // CNAME no longer resolves to a 502.
        try
        {
            await SyncCloudflareIfConfiguredAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Cloudflare sync after remove failed: {Error}", ex.Message);
        }

        try
        {
            await UpdateHostsBlockAsync(Array.Empty<string>(), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Hosts file cleanup failed for {Domain} (may need admin elevation): {Error}",
                domain, ex.Message);
        }

        _logger.LogInformation("Site {Domain} removed", domain);
    }

    /// <summary>
    /// Public wrapper around <see cref="UpdateHostsFileAsync"/> so the uninstall
    /// endpoint can clear the managed hosts block without needing a site payload.
    /// Pass an empty collection to strip the managed block entirely.
    /// </summary>
    public Task UpdateHostsBlockAsync(IEnumerable<string> domainsToAdd, CancellationToken ct) =>
        UpdateHostsFileAsync(domainsToAdd, ct);

    private enum HostsUpdateOutcome { Unchanged, Written, Skipped, Failed }

    /// <summary>
    /// Collects all domains from all sites and writes them into the managed block of the hosts file.
    /// Uses PowerShell with -Verb RunAs (UAC elevation) on Windows — ONLY when content would actually change.
    /// </summary>
    private async Task<HostsUpdateOutcome> UpdateHostsFileAsync(IEnumerable<string> domainsToAdd, CancellationToken ct)
    {
        // Shared with the hosts editor endpoints — see HostsFileWriter.
        await NKS.WebDevConsole.Daemon.Hosts.HostsFileWriter.Gate.WaitAsync(ct);
        try
        {
            return await UpdateHostsFileCoreAsync(domainsToAdd, ct);
        }
        finally
        {
            NKS.WebDevConsole.Daemon.Hosts.HostsFileWriter.Gate.Release();
        }
    }

    private async Task<HostsUpdateOutcome> UpdateHostsFileCoreAsync(IEnumerable<string> domainsToAdd, CancellationToken ct)
    {
        var hostsPath = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts")
            : "/etc/hosts";

        // Collect ALL managed domains (existing + new) so we can write the complete block.
        // Wildcard aliases (*.myapp.loc) are valid in Apache ServerAlias and mkcert certs but
        // CANNOT be written to the hosts file — hosts doesn't support glob matching. Skip them
        // here and warn once so the user knows they need a real DNS resolver for wildcards
        // (Acrylic DNS Proxy on Windows, dnsmasq on macOS/Linux).
        var siteManager = _sp.GetService<SiteManager>();
        var allDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skippedWildcards = new List<string>();

        void AddOrSkipWildcard(string? d)
        {
            if (string.IsNullOrWhiteSpace(d)) return;
            if (d.Contains('*') || d.Contains('?'))
            {
                skippedWildcards.Add(d);
                return;
            }
            allDomains.Add(d);
        }

        if (siteManager is not null)
        {
            foreach (var site in siteManager.Sites.Values)
            {
                AddOrSkipWildcard(site.Domain);
                if (site.Aliases is not null)
                    foreach (var alias in site.Aliases)
                        AddOrSkipWildcard(alias);
            }
        }
        foreach (var d in domainsToAdd)
            AddOrSkipWildcard(d);

        if (skippedWildcards.Count > 0)
        {
            _logger.LogWarning(
                "Skipping {Count} wildcard alias(es) from hosts file ({Wildcards}). " +
                "Apache ServerAlias and mkcert handle them natively, but hosts file requires explicit entries. " +
                "For wildcard DNS install Acrylic DNS Proxy (Windows) or dnsmasq (macOS/Linux).",
                skippedWildcards.Count, string.Join(", ", skippedWildcards));
        }

        // EARLY EXIT: read hosts file without elevation (most users can read it) and check if
        // every required domain already maps to 127.0.0.1. If so, no UAC prompt is needed.
        try
        {
            var existing = await File.ReadAllTextAsync(hostsPath, ct);
            if (AllDomainsAlreadyMapped(existing, allDomains))
            {
                _logger.LogInformation("[hosts] already contains all {Count} managed domains — skipping elevation", allDomains.Count);
                return HostsUpdateOutcome.Unchanged;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Hosts file pre-check failed ({Error}) — proceeding with elevated write", ex.Message);
        }

        // ESCAPE HATCH for CI/e2e/autonomous runs: NKS_WDC_SKIP_HOSTS_UAC=1 skips the UAC
        // prompt and logs what *would* have been written. This lets the integration test
        // harness exercise the full site-creation code path without a human clicking UAC.
        // NEVER set this in a normal interactive session — sites created this way won't
        // resolve via DNS until the user re-runs reapply-all after elevating.
        if (EnvFlags.IsTruthy(Environment.GetEnvironmentVariable("NKS_WDC_SKIP_HOSTS_UAC")))
        {
            _logger.LogWarning(
                "NKS_WDC_SKIP_HOSTS_UAC=1 is set — hosts file write skipped for {Count} domain(s). " +
                "This is intended for CI/e2e tests only.",
                allDomains.Count);
            return HostsUpdateOutcome.Skipped;
        }

        if (!OperatingSystem.IsWindows())
        {
            // macOS/Linux: try direct write first (works when daemon runs as root or
            // /etc/hosts is unexpectedly world-writable). If blocked, escalate via
            // osascript (macOS) or pkexec (Linux) so the user gets a single native
            // TouchID / password prompt per site-save.
            try
            {
                await WriteHostsFileDirectAsync(hostsPath, allDomains, ct);
                return HostsUpdateOutcome.Written;
            }
            catch (UnauthorizedAccessException)
            {
                _logger.LogInformation("[hosts] direct write to {Path} denied — requesting elevation prompt for /etc/hosts write", hostsPath);
            }
            catch (IOException ex) when (IsPermissionDenied(ex))
            {
                _logger.LogInformation("[hosts] direct write to {Path} failed with permission error — requesting elevation prompt", hostsPath);
            }

            var ok = await WriteHostsFileWithElevationAsync(hostsPath, allDomains, ct);
            if (ok)
                _logger.LogInformation("[hosts] wrote {Count} entries via elevation: {List}", allDomains.Count, string.Join(", ", allDomains));
            else
                _logger.LogWarning("[hosts] elevation declined or failed — hosts file NOT updated. Sites will not resolve until the user runs NKS WDC with sudo or re-approves the prompt.");
            return ok ? HostsUpdateOutcome.Written : HostsUpdateOutcome.Failed;
        }

        // FAST PATH: if the daemon is already running elevated (app manifest
        // or admin shell), write the hosts file directly. Skips the
        // PowerShell-Verb-RunAs ceremony, eliminating one UAC prompt per
        // site during startup auto-reapply. Without this the daemon would
        // spam N UAC prompts for N sites even when already admin.
        if (IsCurrentProcessElevated())
        {
            _logger.LogInformation("Daemon is elevated — writing hosts file directly (no UAC spawn)");
            try
            {
                await WriteHostsFileDirectAsync(hostsPath, allDomains, ct);
                return HostsUpdateOutcome.Written;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Direct hosts write failed despite elevation — falling back to PowerShell");
            }
        }

        // Build PowerShell command that writes managed block with elevation
        var entries = string.Join("\\n", allDomains.Select(d => $"127.0.0.1\\t{d}"));
        var psScript = $@"
$hostsPath = '{hostsPath.Replace("'", "''")}'
$begin = '{HostsBlockBegin}'
$end = '{HostsBlockEnd}'
$block = @""
$begin
{string.Join(Environment.NewLine, allDomains.Select(d => $"127.0.0.1\t{d}"))}
$end
""@

# SAFETY: Read existing hosts file — ABORT completely if cannot read
try {{
    $content = Get-Content $hostsPath -Raw -ErrorAction Stop
}} catch {{
    Write-Error ""Cannot read hosts file: $_""
    exit 1
}}
if ($null -eq $content -or $content.Length -eq 0) {{
    Write-Error 'Hosts file is empty or unreadable — refusing to write'
    exit 1
}}

# Rotate backups: keep last 5 versions (.wdc-backup.1 through .wdc-backup.5)
for ($i = 4; $i -ge 1; $i--) {{
    $src = ""$hostsPath.wdc-backup.$i""
    $dst = ""$hostsPath.wdc-backup.$($i+1)""
    if (Test-Path $src) {{ Move-Item $src $dst -Force }}
}}
Copy-Item $hostsPath ""$hostsPath.wdc-backup.1"" -Force

# Snapshot original non-managed lines for post-write sanity check
$originalLines = $content -split ""`r?`n""
$inManagedBlock = $false
$nonManagedOriginal = @()
foreach ($ln in $originalLines) {{
    if ($ln -match [regex]::Escape($begin)) {{ $inManagedBlock = $true; continue }}
    if ($ln -match [regex]::Escape($end)) {{ $inManagedBlock = $false; continue }}
    if (-not $inManagedBlock) {{ $nonManagedOriginal += $ln }}
}}

# Find existing managed block — find end AFTER begin, not from beginning
$bi = $content.IndexOf($begin)
if ($bi -ge 0) {{
    $ei = $content.IndexOf($end, $bi)
}} else {{
    $ei = -1
}}
if ($bi -ge 0 -and $ei -ge 0) {{
    # Replace ONLY the managed block, preserve everything before and after
    $before = $content.Substring(0, $bi)
    $after = $content.Substring($ei + $end.Length)
    $newContent = $before.TrimEnd() + ""`r`n"" + $block + $after
}} else {{
    # No existing block — APPEND to end, never overwrite existing content
    $newContent = $content.TrimEnd() + ""`r`n`r`n"" + $block + ""`r`n""
}}

# SANITY: every original non-managed, non-empty line MUST still appear in new content
foreach ($origLine in $nonManagedOriginal) {{
    $trimmed = $origLine.Trim()
    if ($trimmed.Length -eq 0) {{ continue }}
    if ($newContent -notmatch [regex]::Escape($trimmed)) {{
        Write-Error ""SAFETY ABORT: original line would be lost: $trimmed""
        exit 2
    }}
}}

# SANITY: new content must not be drastically smaller than original
if ($newContent.Length -lt ($content.Length / 4)) {{
    Write-Error ""SAFETY ABORT: new content is suspiciously small (orig=$($content.Length), new=$($newContent.Length))""
    exit 3
}}

Set-Content -Path $hostsPath -Value $newContent -Encoding ASCII -Force
ipconfig /flushdns | Out-Null
";
        // Write script to temp file and execute with elevation
        // Random name + CreateNew so nothing can pre-create or predict the path,
        // and the file is held open with read-only sharing while the elevated
        // PowerShell runs, so another process cannot swap its contents between
        // the write and the UAC-approved execution.
        var scriptPath = Path.Combine(Path.GetTempPath(), $"nks-wdc-hosts-update-{Guid.NewGuid():N}.ps1");
        await NKS.WebDevConsole.Daemon.Config.SecretFile.WriteAllTextAsync(scriptPath, psScript, ct);
        using var scriptLock = new FileStream(scriptPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
            UseShellExecute = true,
            Verb = "runas",
        };

        try
        {
            // `using` so the elevated PowerShell's OS handle releases
            // after WaitForExitAsync — previously leaked on every hosts
            // update (UAC-elevated invocation so the daemon inherits the
            // child's handle table entry and keeps it alive until GC).
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) return HostsUpdateOutcome.Failed;
            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode == 0)
            {
                _logger.LogInformation("Hosts file updated with {Count} domains", allDomains.Count);
                return HostsUpdateOutcome.Written;
            }
            _logger.LogWarning("Hosts update script exited with code {Code}", proc.ExitCode);
            return HostsUpdateOutcome.Failed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Hosts file write failed: {Error}. Try running NKS WDC as administrator.", ex.Message);
            return HostsUpdateOutcome.Failed;
        }
        finally
        {
            scriptLock.Dispose();
            try { File.Delete(scriptPath); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Checks whether the hosts file already matches the required domains: every one maps to 127.0.0.1
    /// and the WDC block holds nothing else, so a domain left behind by a deleted site forces a rewrite.
    /// Works line-by-line, ignoring comments and whitespace. Used to skip UAC elevation when no write is needed.
    /// </summary>
    private static bool AllDomainsAlreadyMapped(string hostsContent, HashSet<string> requiredDomains)
    {
        if (requiredDomains.Count == 0)
        {
            // Clear-the-block request: return true (skip write) only when
            // the managed block is *already* gone. If the block is still
            // present with stale entries, we MUST proceed to the elevated
            // write to strip them. Previously `Count == 0` always early-
            // exited, so factory reset never actually removed the managed
            // block from /etc/hosts — user saw `# BEGIN NKS WebDev…`
            // plus their old site domains still mapped to 127.0.0.1 after
            // what was supposed to be a full reset.
            return !hostsContent.Contains(HostsBlockBegin);
        }

        var mapped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var managed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inManagedBlock = false;
        foreach (var rawLine in hostsContent.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Contains(HostsBlockBegin, StringComparison.Ordinal)) { inManagedBlock = true; continue; }
            if (line.Contains(HostsBlockEnd, StringComparison.Ordinal)) { inManagedBlock = false; continue; }
            if (line.Length == 0 || line.StartsWith("#")) continue;

            // Drop any inline comment BEFORE tokenising — a line like
            // "127.0.0.1 foo.com # disabled bar.com" must not count bar.com as mapped.
            var commentIdx = line.IndexOf('#');
            if (commentIdx >= 0) line = line.Substring(0, commentIdx).TrimEnd();
            if (line.Length == 0) continue;

            // split on whitespace/tabs: first token IP, remaining tokens are hostnames
            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            var ip = parts[0];
            if (ip != "127.0.0.1" && ip != "::1") continue;
            for (int i = 1; i < parts.Length; i++)
            {
                var host = parts[i];
                if (host.Length == 0) continue;
                mapped.Add(host);
                if (inManagedBlock) managed.Add(host);
            }
        }

        return requiredDomains.All(mapped.Contains) && managed.All(requiredDomains.Contains);
    }

    private async Task WriteHostsFileDirectAsync(string hostsPath, HashSet<string> allDomains, CancellationToken ct)
    {
        var content = await File.ReadAllTextAsync(hostsPath, ct);
        var newContent = RewriteManagedHostsContent(content, allDomains);

        RotateHostsBackups(hostsPath);
        await NKS.WebDevConsole.Daemon.Hosts.HostsFileWriter.WriteAsync(hostsPath, newContent, HostsFileEncoding, ct);
        await FlushDnsCacheAsync(ct);

        _logger.LogInformation("[hosts] wrote {Count} entries directly to {Path}: {List}",
            allDomains.Count, hostsPath, string.Join(", ", allDomains));
    }

    /// <summary>
    /// Writes the hosts file via a native elevation prompt on macOS (osascript's
    /// <c>with administrator privileges</c> TouchID/password dialog) or Linux
    /// (pkexec on desktop sessions). Uses <see cref="ProcessStartInfo.ArgumentList"/>
    /// so tmp/dest paths are passed as discrete args and never interpolated into
    /// a shell string — avoids the double-quote-escaping fragility that bit the
    /// previous plugin helper. Returns true when the user approved and the copy
    /// succeeded, false on cancel/failure.
    /// </summary>
    private async Task<bool> WriteHostsFileWithElevationAsync(string hostsPath, HashSet<string> allDomains, CancellationToken ct)
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"nks-wdc-hosts.{Guid.NewGuid():N}");
        try
        {
            string existing;
            try { existing = await File.ReadAllTextAsync(hostsPath, ct); }
            catch (Exception ex)
            {
                _logger.LogWarning("[hosts] cannot read existing hosts file {Path}: {Err}", hostsPath, ex.Message);
                return false;
            }

            string newContent;
            try { newContent = RewriteManagedHostsContent(existing, allDomains); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[hosts] refusing to write — safety check on managed-block rewrite failed");
                return false;
            }

            await File.WriteAllTextAsync(tmp, newContent, HostsFileEncoding, ct);

            if (OperatingSystem.IsMacOS())
            {
                // osascript -e 'do shell script "/bin/cp <tmp> <dest>" with prompt "..." with administrator privileges'
                // We build the AppleScript once in managed code (with the real paths quoted via
                // AppleScript's own 'quoted form of') and pass it as a SINGLE -e argument. Using
                // ArgumentList means .NET emits one argv entry per item — no shell involved, no
                // round of double-quote escaping, no risk of embedded spaces/quotes in the tmp
                // path mangling the command.
                var script =
                    "do shell script \"/bin/cp \" & quoted form of \"" + EscapeForAppleScriptString(tmp) + "\" & \" \" & " +
                    "quoted form of \"" + EscapeForAppleScriptString(hostsPath) + "\" " +
                    "with prompt \"NKS WebDev Console needs permission to update " + EscapeForAppleScriptString(hostsPath) + ".\" " +
                    "with administrator privileges";

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "/usr/bin/osascript",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                };
                psi.ArgumentList.Add("-e");
                psi.ArgumentList.Add(script);

                _logger.LogInformation("[hosts] spawning osascript for TouchID/password prompt (tmp={Tmp})", tmp);
                using var p = System.Diagnostics.Process.Start(psi);
                if (p is null)
                {
                    _logger.LogWarning("[hosts] failed to spawn /usr/bin/osascript");
                    return false;
                }

                await p.WaitForExitAsync(ct);
                if (p.ExitCode != 0)
                {
                    var err = (await p.StandardError.ReadToEndAsync(ct)).Trim();
                    // exit 1 + "User canceled." is the TouchID-cancel / password-dismiss path.
                    if (err.Contains("User canceled", StringComparison.OrdinalIgnoreCase)
                        || err.Contains("(-128)", StringComparison.Ordinal))
                        _logger.LogWarning("[hosts] user cancelled the elevation prompt: {Err}", err);
                    else
                        _logger.LogWarning("[hosts] osascript elevation failed (exit={Code}): {Err}", p.ExitCode, err);
                    return false;
                }

                await FlushDnsCacheAsync(ct);
                return true;
            }

            if (OperatingSystem.IsLinux())
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "pkexec",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                };
                psi.ArgumentList.Add("/bin/cp");
                psi.ArgumentList.Add(tmp);
                psi.ArgumentList.Add(hostsPath);

                _logger.LogInformation("[hosts] spawning pkexec for desktop elevation (tmp={Tmp})", tmp);
                using var p = System.Diagnostics.Process.Start(psi);
                if (p is null) return false;
                await p.WaitForExitAsync(ct);
                if (p.ExitCode != 0)
                {
                    var err = (await p.StandardError.ReadToEndAsync(ct)).Trim();
                    _logger.LogWarning("[hosts] pkexec elevation failed (exit={Code}): {Err}", p.ExitCode, err);
                    return false;
                }
                await FlushDnsCacheAsync(ct);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[hosts] elevation helper failed unexpectedly");
            return false;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
        }
    }

    private static string EscapeForAppleScriptString(string value)
    {
        // AppleScript strings use backslash escapes for " and \ only.
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private static bool IsPermissionDenied(IOException ex)
    {
        var m = ex.Message ?? string.Empty;
        return m.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
            || m.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
            || m.Contains("operation not permitted", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the daemon process is running elevated (admin on Windows,
    /// root on Unix). Used to skip the PowerShell-Verb-RunAs ceremony for
    /// hosts-file writes when we already have the rights.
    /// </summary>
    private static bool IsCurrentProcessElevated()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
#pragma warning disable CA1416 // WindowsIdentity only on Windows — we just checked.
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
#pragma warning restore CA1416
            }
            // On Unix, check getuid() == 0
            return Environment.UserName == "root";
        }
        catch
        {
            return false;
        }
    }

    internal static string RewriteManagedHostsContent(string content, IReadOnlyCollection<string> allDomains)
    {
        var block = allDomains.Count == 0
            ? string.Empty
            : string.Join(Environment.NewLine,
                new[] { HostsBlockBegin }
                    .Concat(allDomains.Select(d => $"127.0.0.1\t{d}"))
                    .Concat(new[] { HostsBlockEnd }));

        var originalLines = content.Split(["\r\n", "\n"], StringSplitOptions.None);
        var nonManagedOriginal = new List<string>();
        var inManagedBlock = false;
        foreach (var raw in originalLines)
        {
            if (raw.Contains(HostsBlockBegin, StringComparison.Ordinal))
            {
                inManagedBlock = true;
                continue;
            }
            if (raw.Contains(HostsBlockEnd, StringComparison.Ordinal))
            {
                inManagedBlock = false;
                continue;
            }
            if (!inManagedBlock)
                nonManagedOriginal.Add(raw);
        }

        var beginIndex = content.IndexOf(HostsBlockBegin, StringComparison.Ordinal);
        var endIndex = beginIndex >= 0
            ? content.IndexOf(HostsBlockEnd, beginIndex, StringComparison.Ordinal)
            : -1;

        string newContent;
        if (beginIndex >= 0 && endIndex >= 0)
        {
            var before = content[..beginIndex].TrimEnd();
            var after = content[(endIndex + HostsBlockEnd.Length)..].TrimStart();
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(before)) parts.Add(before);
            if (!string.IsNullOrWhiteSpace(block)) parts.Add(block);
            if (!string.IsNullOrWhiteSpace(after)) parts.Add(after);
            newContent = parts.Count == 0
                ? string.Empty
                : string.Join($"{Environment.NewLine}{Environment.NewLine}", parts) + Environment.NewLine;
        }
        else if (string.IsNullOrWhiteSpace(block))
        {
            newContent = content;
        }
        else
        {
            newContent = content.TrimEnd() + $"{Environment.NewLine}{Environment.NewLine}{block}{Environment.NewLine}";
        }

        foreach (var originalLine in nonManagedOriginal)
        {
            var trimmed = originalLine.Trim();
            if (trimmed.Length == 0) continue;
            if (!newContent.Contains(trimmed, StringComparison.Ordinal))
                throw new InvalidOperationException($"SAFETY ABORT: original line would be lost: {trimmed}");
        }

        if (content.Length > 0 && newContent.Length < (content.Length / 4))
            throw new InvalidOperationException($"SAFETY ABORT: new content is suspiciously small (orig={content.Length}, new={newContent.Length})");

        return newContent;
    }

    private static void RotateHostsBackups(string hostsPath)
    {
        for (var i = 4; i >= 1; i--)
        {
            var src = $"{hostsPath}.wdc-backup.{i}";
            var dst = $"{hostsPath}.wdc-backup.{i + 1}";
            if (File.Exists(src))
                File.Move(src, dst, overwrite: true);
        }

        File.Copy(hostsPath, $"{hostsPath}.wdc-backup.1", overwrite: true);
    }

    private async Task FlushDnsCacheAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsMacOS())
        {
            await RunBestEffortAsync("dscacheutil", "-flushcache", ct);
            await RunBestEffortAsync("killall", "-HUP mDNSResponder", ct);
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            await RunBestEffortAsync("resolvectl", "flush-caches", ct);
            await RunBestEffortAsync("systemd-resolve", "--flush-caches", ct);
        }
    }

    private async Task RunBestEffortAsync(string fileName, string arguments, CancellationToken ct)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is not null)
                await process.WaitForExitAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Best-effort command failed: {FileName} {Arguments}", fileName, arguments);
        }
    }

    /// <summary>
    /// Invokes a named async method on a cross-assembly plugin instance via reflection.
    /// Unwraps <see cref="System.Reflection.TargetInvocationException"/> so callers see
    /// the real plugin error (with the plugin's stack trace) instead of a generic outer
    /// wrapper. Without this, every plugin failure logs "Exception has been thrown by
    /// the target of an invocation" with no root cause, making debugging opaque.
    /// </summary>
    private static async Task InvokeAsync(object target, string methodName, object[] args)
    {
        var method = target.GetType().GetMethod(methodName)
            ?? throw new MissingMethodException(target.GetType().FullName, methodName);

        object? result;
        try
        {
            result = method.Invoke(target, args);
        }
        catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException is not null)
        {
            // Rethrow the inner exception preserving its stack trace.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw; // unreachable
        }

        if (result is Task task) await task;
    }
}
