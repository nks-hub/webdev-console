using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;

namespace NKS.WebDevConsole.Daemon.Sites;

/// <summary>
/// Coordinates per-site setup across loaded plugins: generates the Apache vhost,
/// ensures PHP is running for the requested version, and reloads Apache.
/// Calls into plugin types via reflection because plugins are loaded into
/// isolated <see cref="System.Runtime.Loader.AssemblyLoadContext"/> instances.
/// </summary>
public sealed class SiteOrchestrator
{
    private readonly ILogger<SiteOrchestrator> _logger;
    private readonly IServiceProvider _sp;

    public SiteOrchestrator(ILogger<SiteOrchestrator> logger, IServiceProvider sp)
    {
        _logger = logger;
        _sp = sp;
    }

    /// <summary>
    /// Applies a site config: generate Apache vhost, ensure PHP is running, reload Apache.
    /// </summary>
    public async Task ApplyAsync(SiteConfig site, CancellationToken ct = default)
    {
        _logger.LogInformation("Orchestrating site {Domain}...", site.Domain);

        var modules = _sp.GetServices<IServiceModule>().ToList();

        // 1. Apache — generate vhost file via reflection (cross-ALC boundary)
        var apacheModule = modules.FirstOrDefault(m =>
            m.ServiceId.Equals("apache", StringComparison.OrdinalIgnoreCase));
        if (apacheModule is not null)
        {
            try
            {
                await InvokeAsync(apacheModule, "GenerateVhostAsync", new object[] { site, ct });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate vhost for {Domain}", site.Domain);
            }
        }
        else
        {
            _logger.LogWarning("Apache module not registered — skipping vhost generation");
        }

        // 2. PHP — ensure module is running (it manages all installed versions internally)
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
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to ensure PHP module running for {Domain}", site.Domain);
            }
        }

        // 3. Reload Apache if it is running so the new vhost takes effect
        if (apacheModule is not null)
        {
            try
            {
                var apacheStatus = await apacheModule.GetStatusAsync(ct);
                if (apacheStatus.State == ServiceState.Running)
                {
                    await apacheModule.ReloadAsync(ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reload Apache after applying {Domain}", site.Domain);
            }
        }

        _logger.LogInformation("Site {Domain} applied", site.Domain);
    }

    /// <summary>
    /// Removes a site: delete Apache vhost and reload Apache.
    /// </summary>
    public async Task RemoveAsync(string domain, CancellationToken ct = default)
    {
        _logger.LogInformation("Orchestrating removal of site {Domain}...", domain);

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
                    await apacheModule.ReloadAsync(ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reload Apache after removing {Domain}", domain);
            }
        }

        _logger.LogInformation("Site {Domain} removed", domain);
    }

    /// <summary>
    /// Invokes a named async method on a cross-assembly plugin instance via reflection.
    /// </summary>
    private static async Task InvokeAsync(object target, string methodName, object[] args)
    {
        var method = target.GetType().GetMethod(methodName)
            ?? throw new MissingMethodException(target.GetType().FullName, methodName);

        if (method.Invoke(target, args) is Task task)
            await task;
    }
}
