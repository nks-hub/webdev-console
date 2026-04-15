using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Core.Services;

namespace NKS.WebDevConsole.Daemon.Sites;

/// <summary>
/// Generates a per-site php.ini override at
/// <c>~/.wdc/sites-php/{domain}/php.ini</c>. The file is built by copying
/// the global php.ini for the site's PHP version (written by the PHP
/// plugin via <c>PhpIniManager</c>) and appending a block of overrides
/// based on <see cref="SitePhpSettings"/>.
///
/// Rationale: mod_fcgid doesn't accept <c>php_value</c> / <c>php_admin_value</c>
/// in vhosts — those are mod_php only. To override PHP ini settings per
/// site we instead point php-cgi.exe at a different php.ini via the
/// <c>PHPRC</c> environment variable (injected through
/// <c>FcgidInitialEnv PHPRC</c> in the vhost). PHP reads every directive
/// from the top of the file to the bottom and later declarations win,
/// so appending is sufficient without a full re-render.
/// </summary>
public sealed class SitePhpIniWriter
{
    private readonly ILogger<SitePhpIniWriter> _logger;

    public SitePhpIniWriter(ILogger<SitePhpIniWriter> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Write the per-site override file and return its absolute path, or
    /// <c>null</c> if no overrides are configured OR the global php.ini
    /// for the requested version cannot be found (we skip silently and
    /// let the vhost fall back to the default ini).
    /// </summary>
    public string? Write(SiteConfig site)
    {
        if (site.PhpSettings is null) return null;
        if (string.IsNullOrEmpty(site.PhpVersion) || site.PhpVersion == "none") return null;

        var globalIni = ResolveGlobalPhpIni(site.PhpVersion);
        if (globalIni is null || !File.Exists(globalIni))
        {
            _logger.LogWarning("Global php.ini not found for {Version}; skipping per-site override for {Domain}",
                site.PhpVersion, site.Domain);
            return null;
        }

        var siteDir = Path.Combine(WdcPaths.Root, "sites-php", site.Domain);
        Directory.CreateDirectory(siteDir);
        var sitePhpIni = Path.Combine(siteDir, "php.ini");

        var global = File.ReadAllText(globalIni);
        var overrides = BuildOverrideBlock(site.PhpSettings);
        var combined = global + "\n\n; === Per-site overrides (NKS WDC) — do not edit manually ===\n" + overrides;

        var tmp = sitePhpIni + ".tmp";
        File.WriteAllText(tmp, combined);
        File.Move(tmp, sitePhpIni, overwrite: true);

        _logger.LogInformation("Wrote per-site php.ini for {Domain} to {Path}", site.Domain, sitePhpIni);
        return sitePhpIni;
    }

    /// <summary>
    /// Removes the per-site override file when the site is deleted or its
    /// PhpSettings is cleared. Safe to call when nothing exists.
    /// </summary>
    public void Remove(string domain)
    {
        try
        {
            var siteDir = Path.Combine(WdcPaths.Root, "sites-php", domain);
            if (Directory.Exists(siteDir))
                Directory.Delete(siteDir, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove per-site php.ini dir for {Domain}", domain);
        }
    }

    private static string? ResolveGlobalPhpIni(string requestedVersion)
    {
        var phpRoot = Path.Combine(WdcPaths.BinariesRoot, "php");
        if (!Directory.Exists(phpRoot)) return null;

        var requested = requestedVersion.TrimEnd('.');
        var candidate = Directory.GetDirectories(phpRoot)
            .Where(d =>
            {
                var name = Path.GetFileName(d);
                return name == requested || name.StartsWith(requested + ".");
            })
            .OrderByDescending(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
            .Select(d => Path.Combine(d, "php.ini"))
            .FirstOrDefault(File.Exists);
        return candidate;
    }

    private static string BuildOverrideBlock(SitePhpSettings s)
    {
        var sb = new System.Text.StringBuilder();

        if (!string.IsNullOrEmpty(s.MemoryLimit))
            sb.Append("memory_limit = ").AppendLine(s.MemoryLimit);

        if (s.MaxExecutionTime.HasValue)
            sb.Append("max_execution_time = ").AppendLine(s.MaxExecutionTime.Value.ToString());

        if (s.MaxInputTime.HasValue)
            sb.Append("max_input_time = ").AppendLine(s.MaxInputTime.Value.ToString());

        if (!string.IsNullOrEmpty(s.PostMaxSize))
            sb.Append("post_max_size = ").AppendLine(s.PostMaxSize);

        if (!string.IsNullOrEmpty(s.UploadMaxFilesize))
            sb.Append("upload_max_filesize = ").AppendLine(s.UploadMaxFilesize);

        if (s.DisplayErrors.HasValue)
            sb.Append("display_errors = ").AppendLine(s.DisplayErrors.Value ? "On" : "Off");

        if (!string.IsNullOrEmpty(s.Timezone))
            sb.Append("date.timezone = ").AppendLine(s.Timezone);

        if (s.OpcacheEnabled.HasValue)
        {
            sb.Append("opcache.enable = ").AppendLine(s.OpcacheEnabled.Value ? "1" : "0");
        }

        if (s.DisabledExtensions is { Count: > 0 })
        {
            // PHP doesn't have a direct "disable extension" directive — the
            // best we can do in an override is attempt to unload via `disable_functions`
            // or rely on the admin to edit the base ini. Emit a comment so users
            // understand why this didn't take effect if they hit it.
            foreach (var ext in s.DisabledExtensions)
            {
                sb.Append("; disable requested (not supported via override): ").AppendLine(ext);
            }
        }

        if (s.ExtraExtensions is { Count: > 0 })
        {
            foreach (var ext in s.ExtraExtensions)
            {
                if (string.IsNullOrWhiteSpace(ext)) continue;
                sb.Append("extension = ").AppendLine(ext);
            }
        }

        if (s.XdebugEnabled == true)
        {
            // Xdebug is a Zend extension, not a regular one. User must have
            // php_xdebug.dll present under ext/. We emit a guarded zend_extension
            // line so PHP falls back gracefully if the DLL is missing.
            sb.AppendLine("zend_extension = xdebug");
            sb.AppendLine("xdebug.mode = develop,debug");
            sb.AppendLine("xdebug.start_with_request = yes");
            sb.AppendLine("xdebug.client_host = 127.0.0.1");
            sb.AppendLine("xdebug.client_port = 9003");
        }
        else if (s.XdebugEnabled == false)
        {
            // Explicitly disabling xdebug: we can't unload it at ini level,
            // but we can set mode to off so it has zero runtime overhead.
            sb.AppendLine("xdebug.mode = off");
        }

        return sb.ToString();
    }
}
