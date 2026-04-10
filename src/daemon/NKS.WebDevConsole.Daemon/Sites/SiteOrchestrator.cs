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

        // 4. Hosts file — add domain + aliases to system hosts (requires elevation on Windows)
        try
        {
            var domains = new List<string> { site.Domain };
            if (site.Aliases is { Length: > 0 })
                domains.AddRange(site.Aliases);

            await UpdateHostsFileAsync(domains, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Hosts file update failed for {Domain} (may need admin elevation): {Error}",
                site.Domain, ex.Message);
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
    /// Collects all domains from all sites and writes them into the managed block of the hosts file.
    /// Uses PowerShell with -Verb RunAs (UAC elevation) on Windows.
    /// </summary>
    private async Task UpdateHostsFileAsync(IEnumerable<string> domainsToAdd, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return; // TODO: Unix implementation

        var hostsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "drivers", "etc", "hosts");

        // Collect ALL managed domains (existing + new) so we can write the complete block
        var siteManager = _sp.GetService<SiteManager>();
        var allDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (siteManager is not null)
        {
            foreach (var site in siteManager.Sites.Values)
            {
                allDomains.Add(site.Domain);
                if (site.Aliases is not null)
                    foreach (var alias in site.Aliases)
                        allDomains.Add(alias);
            }
        }
        foreach (var d in domainsToAdd)
            allDomains.Add(d);

        // Build PowerShell command that writes managed block with elevation
        var entries = string.Join("\\n", allDomains.Select(d => $"127.0.0.1\\t{d}"));
        var psScript = $@"
$hostsPath = '{hostsPath.Replace("'", "''")}'
$begin = '# BEGIN NKS WebDev Console'
$end = '# END NKS WebDev Console'
$block = @""
$begin
{string.Join(Environment.NewLine, allDomains.Select(d => $"127.0.0.1\t{d}"))}
$end
""@
$content = Get-Content $hostsPath -Raw -ErrorAction SilentlyContinue
if (-not $content) {{ $content = '' }}
$bi = $content.IndexOf($begin)
$ei = $content.IndexOf($end)
if ($bi -ge 0 -and $ei -ge 0) {{
    $before = $content.Substring(0, $bi)
    $after = $content.Substring($ei + $end.Length)
    $content = $before.TrimEnd() + ""`r`n`r`n"" + $block + $after.TrimStart()
}} else {{
    $content = $content.TrimEnd() + ""`r`n`r`n"" + $block + ""`r`n""
}}
Set-Content -Path $hostsPath -Value $content -Encoding ASCII -Force
ipconfig /flushdns | Out-Null
";
        // Write script to temp file and execute with elevation
        var scriptPath = Path.Combine(Path.GetTempPath(), "nks-wdc-hosts-update.ps1");
        await File.WriteAllTextAsync(scriptPath, psScript, ct);

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            var proc = System.Diagnostics.Process.Start(psi);
            if (proc is not null)
            {
                await proc.WaitForExitAsync(ct);
                if (proc.ExitCode == 0)
                    _logger.LogInformation("Hosts file updated with {Count} domains", allDomains.Count);
                else
                    _logger.LogWarning("Hosts update script exited with code {Code}", proc.ExitCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Hosts file write failed: {Error}. Try running NKS WDC as administrator.", ex.Message);
        }
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
