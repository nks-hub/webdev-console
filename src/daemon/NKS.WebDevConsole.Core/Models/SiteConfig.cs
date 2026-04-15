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

    /// <summary>
    /// Per-site PHP runtime overrides (memory_limit, max_execution_time,
    /// extensions, Xdebug). Null means inherit global defaults. Writes
    /// to <c>~/.wdc/sites-php/{domain}/php.ini</c> and the vhost wires
    /// php-cgi to it via <c>FcgidInitialEnv PHPRC</c>.
    /// </summary>
    public SitePhpSettings? PhpSettings { get; set; }

    /// <summary>
    /// Per-site Apache / mod_fcgid tuning (timeouts, process lifetime,
    /// max request length). Null means daemon defaults apply. Use for
    /// long-running admin imports, large uploads, or PDF generation.
    /// </summary>
    public SiteApacheSettings? ApacheSettings { get; set; }
}

/// <summary>
/// Per-site PHP configuration overrides. All fields are nullable — null
/// means "inherit the global default". The <see cref="ExtraExtensions"/>
/// list is merged with the global set; <see cref="DisabledExtensions"/>
/// subtracts from it.
/// </summary>
public class SitePhpSettings
{
    /// <summary>e.g. "256M", "512M", "1G". Null = global default.</summary>
    public string? MemoryLimit { get; set; }

    /// <summary>Seconds. 0 = unlimited. Null = global default.</summary>
    public int? MaxExecutionTime { get; set; }

    /// <summary>Seconds. Per-request input parsing limit. Null = global default.</summary>
    public int? MaxInputTime { get; set; }

    /// <summary>e.g. "64M", "128M". Must be ≥ UploadMaxFilesize.</summary>
    public string? PostMaxSize { get; set; }

    /// <summary>e.g. "32M", "256M".</summary>
    public string? UploadMaxFilesize { get; set; }

    /// <summary>Override global Xdebug on/off just for this site.</summary>
    public bool? XdebugEnabled { get; set; }

    /// <summary>Override global OPcache on/off just for this site.</summary>
    public bool? OpcacheEnabled { get; set; }

    /// <summary>Show PHP errors in the response body. False = log only.</summary>
    public bool? DisplayErrors { get; set; }

    /// <summary>
    /// Extra extensions to load for this site on top of the global set.
    /// Names without `.dll` prefix, e.g. "imagick", "oauth", "tidy".
    /// </summary>
    public List<string>? ExtraExtensions { get; set; }

    /// <summary>
    /// Extensions to disable for this site even if globally enabled.
    /// </summary>
    public List<string>? DisabledExtensions { get; set; }

    /// <summary>Site-specific timezone override.</summary>
    public string? Timezone { get; set; }
}

/// <summary>
/// Per-site Apache / mod_fcgid tuning knobs. All fields nullable — null
/// = daemon default. Values are in seconds unless otherwise noted.
/// </summary>
public class SiteApacheSettings
{
    /// <summary>
    /// Apache core <c>Timeout</c> for the request. Default Apache value
    /// is 60; bump for sites that do long uploads or streaming.
    /// </summary>
    public int? Timeout { get; set; }

    /// <summary>
    /// mod_fcgid <c>FcgidIOTimeout</c> — how long Apache waits for
    /// bytes from the php-cgi child. Default ~40.
    /// </summary>
    public int? FcgidIOTimeout { get; set; }

    /// <summary>
    /// mod_fcgid <c>FcgidBusyTimeout</c> — max seconds a child may
    /// spend processing a single request before Apache kills it.
    /// Default ~300.
    /// </summary>
    public int? FcgidBusyTimeout { get; set; }

    /// <summary>
    /// mod_fcgid <c>FcgidIdleTimeout</c> — how long an idle child may
    /// sit before being reaped. Default ~300.
    /// </summary>
    public int? FcgidIdleTimeout { get; set; }

    /// <summary>
    /// mod_fcgid <c>FcgidProcessLifeTime</c> — hard upper bound on how
    /// long any single child may live before forced recycle.
    /// Default 3600.
    /// </summary>
    public int? FcgidProcessLifeTime { get; set; }

    /// <summary>
    /// mod_fcgid <c>FcgidMaxRequestLen</c> — max request body size in
    /// bytes. Mirror post_max_size when uploading large files.
    /// </summary>
    public long? FcgidMaxRequestLen { get; set; }
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
