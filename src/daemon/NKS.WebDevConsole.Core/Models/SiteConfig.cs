namespace NKS.WebDevConsole.Core.Models;

public class SiteConfig
{
    public string Domain { get; set; } = "";
    public string DocumentRoot { get; set; } = "";
    public string PhpVersion { get; set; } = "8.4";
    public bool SslEnabled { get; set; }
    public int HttpPort { get; set; } = 80;
    public int HttpsPort { get; set; } = 443;
    public string[] Aliases { get; set; } = [];
    public string? Framework { get; set; }
    public Dictionary<string, string> Environment { get; set; } = new();

    /// <summary>
    /// When non-zero, Apache reverse-proxies all requests to
    /// <c>http://localhost:{NodeUpstreamPort}</c> instead of serving from
    /// DocumentRoot. Used for Node.js/Next.js/Express apps that have
    /// their own HTTP listener. Setting this also disables PHP routing
    /// for this site (phpVersion is ignored when proxying).
    /// </summary>
    public int NodeUpstreamPort { get; set; }

    /// <summary>
    /// Shell command the Node plugin uses to start the app process for
    /// this site, e.g. <c>npm start</c>, <c>npm run dev</c>, or
    /// <c>node server.js</c>. When empty, defaults to <c>npm start</c>.
    /// Only meaningful when <see cref="NodeUpstreamPort"/> is non-zero.
    /// </summary>
    public string NodeStartCommand { get; set; } = "";

    /// <summary>
    /// Optional Cloudflare Tunnel exposure for this site. Null means the
    /// site is only accessible locally (default). Set to a populated
    /// <see cref="SiteCloudflareConfig"/> to expose the site via the
    /// Cloudflare plugin's managed tunnel. The plugin reads this on
    /// ApplyAsync to create/update DNS records and ingress rules.
    /// </summary>
    public SiteCloudflareConfig? Cloudflare { get; set; }
}

/// <summary>
/// Per-site Cloudflare Tunnel configuration. Structure aligns with the
/// FlyEnv reference implementation so the DNS CNAME + ingress rule pair
/// behaves identically (including the crucial <c>httpHostHeader</c>
/// override that makes Apache match the correct vhost when the inbound
/// Host is the public name like <c>blog.nks-dev.cz</c> but the local
/// vhost is bound to <c>blog.loc</c>).
/// </summary>
public class SiteCloudflareConfig
{
    /// <summary>Whether this site is currently exposed through the tunnel.</summary>
    public bool Enabled { get; set; }

    /// <summary>Public subdomain label, e.g. "blog" for <c>blog.nks-dev.cz</c>.</summary>
    public string Subdomain { get; set; } = "";

    /// <summary>Target Cloudflare zone ID (looked up via /api/cloudflare/zones).</summary>
    public string ZoneId { get; set; } = "";

    /// <summary>Zone apex, e.g. <c>nks-dev.cz</c> (cached for display).</summary>
    public string ZoneName { get; set; } = "";

    /// <summary>
    /// Local service URL fragment used by cloudflared's ingress rule. Example:
    /// <c>localhost:80</c> — the protocol is prepended from <see cref="Protocol"/>.
    /// </summary>
    public string LocalService { get; set; } = "localhost:80";

    /// <summary>HTTP protocol for the local service. "http" or "https".</summary>
    public string Protocol { get; set; } = "http";
}
