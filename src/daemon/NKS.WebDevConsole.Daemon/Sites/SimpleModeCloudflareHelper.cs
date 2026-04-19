using NKS.WebDevConsole.Core.Models;

namespace NKS.WebDevConsole.Daemon.Sites;

/// <summary>
/// Pure-static helper that derives a <see cref="SiteCloudflareConfig"/> from the
/// Simple Mode <c>cloudflareTunnel: true</c> hint. Keeps all the "what defaults
/// should we use?" logic out of Program.cs and makes it unit-testable without a
/// running HTTP server.
///
/// Inputs accepted via a thin <see cref="CloudflarePluginContext"/> so the helper
/// has no direct compile-time dependency on the Cloudflare plugin assembly (which
/// lives in an isolated AssemblyLoadContext). Program.cs populates the context by
/// reading the plugin's config instance via reflection, the same pattern used
/// everywhere else in the daemon.
/// </summary>
public static class SimpleModeCloudflareHelper
{
    /// <summary>
    /// Snapshot of the fields we need from <c>CloudflareConfig</c>, resolved
    /// by Program.cs via reflection before calling
    /// <see cref="BuildCloudflareConfig"/>. All fields are nullable — Program.cs
    /// leaves them null when the plugin is not loaded or the value is unset.
    /// </summary>
    public sealed class CloudflarePluginContext
    {
        /// <summary>Value of <c>CloudflareConfig.DefaultZoneId</c>.</summary>
        public string? DefaultZoneId { get; init; }

        /// <summary>Value of <c>CloudflareConfig.TunnelId</c>.</summary>
        public string? TunnelId { get; init; }

        /// <summary>
        /// Rendered subdomain for the site domain, produced by calling
        /// <c>CloudflareConfig.RenderSubdomain(domain)</c> via reflection.
        /// </summary>
        public string? RenderedSubdomain { get; init; }
    }

    /// <summary>
    /// Result of <see cref="TryBuild"/>: either a populated
    /// <see cref="SiteCloudflareConfig"/> or a warning key explaining why
    /// automatic provisioning was skipped.
    /// </summary>
    public sealed class BuildResult
    {
        /// <summary>
        /// Populated when provisioning can proceed. Null when
        /// <see cref="Warning"/> is set.
        /// </summary>
        public SiteCloudflareConfig? Config { get; init; }

        /// <summary>
        /// Machine-readable warning key, or null when <see cref="Config"/> is
        /// populated. Current values:
        /// <list type="bullet">
        ///   <item><c>cloudflare_plugin_not_loaded</c></item>
        ///   <item><c>cloudflare_not_configured</c> — plugin loaded but no zone
        ///   or tunnel set in <c>CloudflareConfig</c>.</item>
        /// </list>
        /// </summary>
        public string? Warning { get; init; }
    }

    /// <summary>
    /// Derives a <see cref="SiteCloudflareConfig"/> for the given
    /// <paramref name="siteDomain"/> using data from the live Cloudflare plugin
    /// settings. Returns a warning key instead of a config when the plugin is
    /// absent or not yet set up — callers should surface the warning to the
    /// frontend but still create the site normally.
    ///
    /// Idempotency: the caller must NOT call this when the site already has a
    /// non-null <see cref="SiteConfig.Cloudflare"/> (full Advanced Mode object
    /// wins, Simple Mode hint is ignored).
    /// </summary>
    /// <param name="siteDomain">The site's local domain, e.g. <c>foo.loc</c>.</param>
    /// <param name="context">
    /// Values resolved from the live <c>CloudflareConfig</c> via reflection.
    /// Pass <see langword="null"/> when the plugin is not loaded.
    /// </param>
    public static BuildResult TryBuild(string siteDomain, CloudflarePluginContext? context)
    {
        if (context is null)
            return new BuildResult { Warning = "cloudflare_plugin_not_loaded" };

        // We need at minimum a ZoneId to know which zone to create the CNAME in.
        // TunnelId is also required for the CNAME target. Subdomain comes from
        // the template engine — if that failed (empty salt, no template), we fall
        // back to the domain stem.
        if (string.IsNullOrWhiteSpace(context.DefaultZoneId)
            || string.IsNullOrWhiteSpace(context.TunnelId))
        {
            return new BuildResult { Warning = "cloudflare_not_configured" };
        }

        var subdomain = string.IsNullOrWhiteSpace(context.RenderedSubdomain)
            ? DeriveSubdomainFallback(siteDomain)
            : context.RenderedSubdomain;

        return new BuildResult
        {
            Config = new SiteCloudflareConfig
            {
                Enabled    = true,
                Subdomain  = subdomain,
                ZoneId     = context.DefaultZoneId,
                // ZoneName is intentionally left empty here: we'd need an async
                // API call to map ZoneId → apex domain, which is inappropriate
                // during synchronous site creation. The Cloudflare sync in
                // SiteOrchestrator.SyncCloudflareIfConfiguredAsync will attempt
                // the CNAME upsert and log a warning if ZoneName is still empty
                // when the user has not yet run the auto-setup or zone lookup.
                ZoneName   = "",
                LocalService = "localhost:80",
                Protocol   = "http",
            },
        };
    }

    /// <summary>
    /// Strips common local TLDs (<c>.loc</c>, <c>.local</c>, <c>.test</c>) from
    /// the domain to produce a bare stem, e.g. <c>foo.loc</c> → <c>foo</c>.
    /// Used as a last-resort subdomain when the plugin's template engine could
    /// not produce a value.
    /// </summary>
    private static string DeriveSubdomainFallback(string domain)
    {
        ReadOnlySpan<string> localTlds = [".loc", ".local", ".test"];
        foreach (var tld in localTlds)
        {
            if (domain.EndsWith(tld, StringComparison.OrdinalIgnoreCase))
                return domain[..^tld.Length];
        }
        // For domains that don't end in a local TLD, strip one level: e.g.
        // "myapp.example.com" → "myapp.example" is not useful, just use the
        // whole thing minus extension.
        var lastDot = domain.LastIndexOf('.');
        return lastDot > 0 ? domain[..lastDot] : domain;
    }
}
