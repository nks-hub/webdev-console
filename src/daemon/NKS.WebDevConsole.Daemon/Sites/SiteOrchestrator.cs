using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Daemon.Plugin;

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

        // 1b. SSL — generate certificate if sslEnabled
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
                        var result = resultProp?.GetValue(task);
                        if (result != null)
                            _logger.LogInformation("SSL certificate generated for {Domain}", site.Domain);
                        else
                            _logger.LogWarning("SSL certificate generation returned null for {Domain} — mkcert may not be installed", site.Domain);
                    }
                }
                else
                {
                    _logger.LogWarning("SSL plugin not loaded — skipping cert generation for {Domain}", site.Domain);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate SSL certificate for {Domain}", site.Domain);
            }
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
    /// Uses PowerShell with -Verb RunAs (UAC elevation) on Windows — ONLY when content would actually change.
    /// </summary>
    private async Task UpdateHostsFileAsync(IEnumerable<string> domainsToAdd, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return; // TODO: Unix implementation

        var hostsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "drivers", "etc", "hosts");

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
                _logger.LogInformation("Hosts file already contains all {Count} managed domains — skipping UAC elevation", allDomains.Count);
                return;
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
        if (Environment.GetEnvironmentVariable("NKS_WDC_SKIP_HOSTS_UAC") == "1")
        {
            _logger.LogWarning(
                "NKS_WDC_SKIP_HOSTS_UAC=1 is set — hosts file write skipped for {Count} domain(s). " +
                "This is intended for CI/e2e tests only.",
                allDomains.Count);
            return;
        }

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
        var scriptPath = Path.Combine(Path.GetTempPath(), "nks-wdc-hosts-update.ps1");
        await File.WriteAllTextAsync(scriptPath, psScript, ct);

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
            UseShellExecute = true,
            Verb = "runas",
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
    /// Checks whether every required domain already maps to 127.0.0.1 in the hosts file content.
    /// Works line-by-line, ignoring comments and whitespace. Used to skip UAC elevation when no write is needed.
    /// </summary>
    private static bool AllDomainsAlreadyMapped(string hostsContent, HashSet<string> requiredDomains)
    {
        if (requiredDomains.Count == 0) return true;

        var mapped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in hostsContent.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;

            // split on whitespace/tabs: first token IP, remaining tokens are hostnames
            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            var ip = parts[0];
            if (ip != "127.0.0.1" && ip != "::1") continue;
            for (int i = 1; i < parts.Length; i++)
            {
                var host = parts[i];
                // strip inline comments
                var hash = host.IndexOf('#');
                if (hash >= 0) host = host.Substring(0, hash);
                if (host.Length > 0) mapped.Add(host);
            }
        }

        return requiredDomains.All(d => mapped.Contains(d));
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
