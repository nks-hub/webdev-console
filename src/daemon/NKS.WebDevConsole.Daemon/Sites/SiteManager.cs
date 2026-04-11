using System.Runtime.InteropServices;
using Tomlyn;
using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Core.Services;
using NKS.WebDevConsole.Daemon.Config;
using Microsoft.Extensions.Logging;

namespace NKS.WebDevConsole.Daemon.Sites;

public class SiteManager
{
    private readonly ILogger<SiteManager> _logger;
    private readonly TemplateEngine _templateEngine;
    private readonly ConfigValidator _validator;
    private readonly AtomicWriter _writer;
    private readonly string _sitesDir;
    private readonly string _generatedDir;
    private readonly Dictionary<string, SiteConfig> _sites = new();

    public SiteManager(ILogger<SiteManager> logger, TemplateEngine te, ConfigValidator cv, AtomicWriter aw, string sitesDir, string generatedDir)
    {
        _logger = logger;
        _templateEngine = te;
        _validator = cv;
        _writer = aw;
        _sitesDir = sitesDir;
        _generatedDir = generatedDir;
        Directory.CreateDirectory(_sitesDir);
        Directory.CreateDirectory(_generatedDir);
    }

    public IReadOnlyDictionary<string, SiteConfig> Sites => _sites;

    public void LoadAll()
    {
        _sites.Clear();
        if (!Directory.Exists(_sitesDir)) return;

        // Simple line-based check: find top-level TOML keys (lines matching
        // `^key\s*=` at indent 0, excluding comment lines and table headers)
        // and warn when any don't correspond to a SiteConfig property. This
        // catches the class of bugs where someone hand-edits a TOML with a
        // typo like "phpVerion" — Tomlyn 2.3.0's deserialiser silently
        // drops unknown keys so without this check the user sees no hint
        // that their override wasn't applied. Warnings-only so one bad key
        // never bricks the loader.
        var knownKeys = typeof(SiteConfig)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var topKeyPattern = new System.Text.RegularExpressions.Regex(
            @"^(?<key>[A-Za-z_][A-Za-z0-9_\-]*)\s*=",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        foreach (var file in Directory.GetFiles(_sitesDir, "*.toml"))
        {
            try
            {
                var toml = File.ReadAllText(file);
                var model = TomlSerializer.Deserialize<SiteConfig>(toml)
                    ?? throw new InvalidOperationException($"Failed to deserialize {file}");
                if (string.IsNullOrEmpty(model.Domain))
                    model.Domain = Path.GetFileNameWithoutExtension(file);

                // Top-level key sanity check — once we hit a `[table]` header,
                // subsequent keys belong to a subtable and aren't validated
                // against the root SiteConfig schema.
                var inSubtable = false;
                foreach (var rawLine in toml.Split('\n'))
                {
                    var line = rawLine.TrimEnd('\r');
                    if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) continue;
                    if (line.TrimStart().StartsWith('['))
                    {
                        inSubtable = true;
                        continue;
                    }
                    if (inSubtable) continue;
                    var m = topKeyPattern.Match(line);
                    if (!m.Success) continue;
                    var key = m.Groups["key"].Value;
                    if (!knownKeys.Contains(key))
                    {
                        _logger.LogWarning(
                            "Site TOML {File} contains unknown key '{Key}' — possibly a typo, value ignored",
                            Path.GetFileName(file), key);
                    }
                }

                _sites[model.Domain] = model;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load site config {File}", file);
            }
        }
        _logger.LogInformation("Loaded {Count} sites", _sites.Count);
    }

    public SiteConfig? Get(string domain) => _sites.GetValueOrDefault(domain);

    public static void ValidateDomain(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
            throw new ArgumentException("Domain name is required");
        if (domain.Length > 253)
            throw new ArgumentException("Domain name too long (max 253 chars)");
        // Reject dangerous characters per SPEC section 18
        var forbidden = new[] { ' ', '\t', '\n', '\r', '\0', ';', '|', '&', '$', '`', '>', '<', '"', '\'', '%' };
        foreach (var c in forbidden)
            if (domain.Contains(c))
                throw new ArgumentException($"Domain contains forbidden character: '{c}'");
        if (domain.Contains("../") || domain.Contains("..\\") || domain.Contains(".."))
            throw new ArgumentException("Domain contains consecutive dots / path traversal sequence");
        // Must look like a hostname (primary site domain must not be wildcard — wildcards are aliases only)
        if (!System.Text.RegularExpressions.Regex.IsMatch(domain, @"^[a-zA-Z0-9]([a-zA-Z0-9\-\.]*[a-zA-Z0-9])?$"))
            throw new ArgumentException("Domain must be a valid hostname (letters, digits, hyphens, dots)");
        // RFC 1035: each DNS label between dots is max 63 chars AND must not
        // start or end with a hyphen. Without the hyphen check the whole-string
        // regex accepts "foo.-bar.com" (invalid per RFC; some DNS resolvers
        // and hosts file parsers silently drop such entries, leading to
        // diagnostically-confusing "site created but doesn't resolve" bugs).
        foreach (var label in domain.Split('.'))
        {
            if (label.Length == 0)
                throw new ArgumentException("Domain contains empty label (leading, trailing, or consecutive dots)");
            if (label.Length > 63)
                throw new ArgumentException($"Domain label '{label}' exceeds 63 characters (RFC 1035)");
            if (label[0] == '-' || label[^1] == '-')
                throw new ArgumentException($"Domain label '{label}' must not start or end with a hyphen (RFC 1035)");
        }
    }

    /// <summary>
    /// Validates a ServerAlias entry. Unlike the primary domain, aliases may be wildcard
    /// patterns like <c>*.myapp.loc</c> which Apache ServerAlias and mkcert both support
    /// natively. The hosts file layer is responsible for skipping wildcard entries.
    /// </summary>
    public static void ValidateAlias(string alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
            throw new ArgumentException("Alias cannot be empty");
        if (alias.Length > 253)
            throw new ArgumentException("Alias too long (max 253 chars)");
        var forbidden = new[] { ' ', '\t', '\n', '\r', '\0', ';', '|', '&', '$', '`', '>', '<', '"', '\'' };
        foreach (var c in forbidden)
            if (alias.Contains(c))
                throw new ArgumentException($"Alias contains forbidden character: '{c}'");
        if (alias.Contains("../") || alias.Contains("..\\"))
            throw new ArgumentException("Alias contains path traversal sequence");
        // Allow leading `*.` or `?` for wildcards; rest must be hostname-safe
        var normalized = alias.StartsWith("*.") ? alias[2..] : alias;
        if (!System.Text.RegularExpressions.Regex.IsMatch(normalized, @"^[a-zA-Z0-9\*\?]([a-zA-Z0-9\-\.\*\?]*[a-zA-Z0-9\*\?])?$"))
            throw new ArgumentException($"Alias '{alias}' is not a valid hostname pattern");
    }

    /// <summary>
    /// Validates a DocumentRoot path. Rejects characters that would break out of the
    /// quoted DocumentRoot directive in the generated Apache vhost (quotes, newlines,
    /// null bytes, shell metacharacters) — without this a crafted POST body could
    /// inject additional Apache directives into the rendered vhost.
    /// </summary>
    public static void ValidateDocumentRoot(string documentRoot)
    {
        if (string.IsNullOrWhiteSpace(documentRoot))
            throw new ArgumentException("DocumentRoot is required");
        if (documentRoot.Length > 4096)
            throw new ArgumentException("DocumentRoot too long (max 4096 chars)");
        // Characters that could break out of the quoted Apache directive:
        var forbidden = new[] { '"', '\n', '\r', '\0', '\t', '|', '&', '`', '<', '>' };
        foreach (var c in forbidden)
            if (documentRoot.Contains(c))
                throw new ArgumentException($"DocumentRoot contains forbidden character: '{c}'");
    }

    public async Task<SiteConfig> CreateAsync(SiteConfig site)
    {
        ValidateDomain(site.Domain);
        ValidateDocumentRoot(site.DocumentRoot);
        if (site.Aliases is { Length: > 0 })
            foreach (var alias in site.Aliases)
                ValidateAlias(alias);
        var toml = TomlSerializer.Serialize(site);
        var path = Path.Combine(_sitesDir, $"{site.Domain}.toml");
        await File.WriteAllTextAsync(path, toml);
        _sites[site.Domain] = site;

        // Generate Apache vhost configuration from Scriban template
        await GenerateVhostAsync(site);

        _logger.LogInformation("Created site {Domain}", site.Domain);
        return site;
    }

    public async Task<SiteConfig> UpdateAsync(SiteConfig site)
    {
        // SECURITY: validate before building any path from domain (prevents traversal
        // via crafted PUT body). Same treatment as Delete/Create.
        ValidateDomain(site.Domain);
        ValidateDocumentRoot(site.DocumentRoot);
        if (site.Aliases is { Length: > 0 })
            foreach (var alias in site.Aliases)
                ValidateAlias(alias);

        var toml = TomlSerializer.Serialize(site);
        var sitesRoot = Path.GetFullPath(_sitesDir);
        var path = Path.GetFullPath(Path.Combine(_sitesDir, $"{site.Domain}.toml"));
        if (!path.StartsWith(sitesRoot, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Resolved site path escapes sites root");

        await _writer.WriteAsync(path, toml);
        _sites[site.Domain] = site;

        // Regenerate the Apache vhost under ~/.wdc/generated/ so the GUI config-history
        // view and rollback endpoint see the updated version; AtomicWriter archives the
        // previous vhost to generated/history/ automatically. Without this call, an edit
        // via PUT /api/sites silently left the stale vhost on disk (the real Apache vhost
        // in conf/sites-enabled/ IS regenerated separately by SiteOrchestrator via the
        // ApacheModule plugin, but the SiteManager-owned copy used for history was not).
        await GenerateVhostAsync(site);

        return site;
    }

    public bool Delete(string domain)
    {
        // SECURITY: validate domain before touching any path. Without this, a caller
        // could pass "../../something" and delete files outside the sites directory.
        ValidateDomain(domain);

        // Additional defense: resolve full paths and verify they live inside the
        // expected directories. Defends against TOCTOU / creative inputs even if
        // ValidateDomain ever loosens its pattern.
        var sitesRoot = Path.GetFullPath(_sitesDir);
        var generatedRoot = Path.GetFullPath(_generatedDir);
        var path = Path.GetFullPath(Path.Combine(_sitesDir, $"{domain}.toml"));
        var genPath = Path.GetFullPath(Path.Combine(_generatedDir, $"{domain}.conf"));

        if (!path.StartsWith(sitesRoot, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Resolved site path escapes sites root");
        if (!genPath.StartsWith(generatedRoot, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Resolved generated path escapes generated root");

        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(genPath)) File.Delete(genPath);
        return _sites.Remove(domain);
    }

    /// <summary>
    /// Renders the Apache vhost Scriban template for the given site and writes it to the generated directory.
    /// </summary>
    public async Task GenerateVhostAsync(SiteConfig site)
    {
        // Locate the vhost template — look in plugin templates directory first, then fallback to embedded
        var templatePath = FindVhostTemplate();
        if (templatePath == null)
        {
            _logger.LogWarning("Apache vhost template not found, skipping vhost generation for {Domain}", site.Domain);
            return;
        }

        // Canonical SSL paths written by the SSL plugin (mkcert):
        // ~/.wdc/ssl/sites/{domain}/cert.pem + key.pem. Older versions of this
        // file generated ~/.wdc/ssl/{domain}.crt paths that never existed,
        // which silently broke the history-preview vhost for SSL-enabled
        // sites. The real Apache vhost is still produced by ApacheModule
        // under sites-enabled/ with the correct paths, but this display copy
        // needs to match so rollback works.
        var sslDir = Path.Combine(WdcPaths.SslRoot, "sites", site.Domain);

        var model = new
        {
            site = new
            {
                domain = site.Domain,
                aliases = site.Aliases,
                root = site.DocumentRoot,
                php_enabled = !string.IsNullOrEmpty(site.PhpVersion) && site.PhpVersion != "none",
                php_version = site.PhpVersion,
                php_fcgi_port = 9000,
                ssl = site.SslEnabled,
                cert_path = Path.Combine(sslDir, "cert.pem"),
                key_path = Path.Combine(sslDir, "key.pem"),
                redirects = Array.Empty<object>()
            },
            port = site.SslEnabled ? site.HttpsPort : site.HttpPort,
            is_windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        };

        try
        {
            var rendered = _templateEngine.RenderFile(templatePath, model);
            var outputPath = Path.Combine(_generatedDir, $"{site.Domain}.conf");
            await _writer.WriteAsync(outputPath, rendered);
            _logger.LogInformation("Generated vhost config for {Domain} at {Path}", site.Domain, outputPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate vhost config for {Domain}", site.Domain);
        }
    }

    private string? FindVhostTemplate()
    {
        // Check common locations for the vhost template
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Templates", "vhost.conf.scriban"),
            Path.Combine(AppContext.BaseDirectory, "plugins", "Templates", "vhost.conf.scriban"),
            // Dev: walk up to repo root and check plugin templates
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..",
                "src", "plugins", "NKS.WebDevConsole.Plugin.Apache", "Templates", "vhost.conf.scriban"))
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Detects the PHP framework powering a site by inspecting its document root
    /// and parent directory (Laravel/Symfony/Nette typically have docroot=public/ or www/).
    /// Returns a lowercase identifier or null. Recognized frameworks:
    /// laravel, symfony, nette, wordpress, drupal, joomla, codeigniter, yii, slim, statamic.
    /// </summary>
    public string? DetectFramework(string documentRoot)
    {
        if (string.IsNullOrEmpty(documentRoot) || !Directory.Exists(documentRoot))
            return null;

        // Search the docroot AND its parent (frameworks like Laravel/Symfony/Nette
        // expose only public/www as docroot but the project root holds composer.json/artisan)
        var searchDirs = new List<string> { documentRoot };
        var parent = Path.GetDirectoryName(documentRoot.TrimEnd('/', '\\'));
        if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
            searchDirs.Add(parent);

        foreach (var dir in searchDirs)
        {
            // File markers — fastest, no parsing
            if (File.Exists(Path.Combine(dir, "artisan"))) return "laravel";
            if (File.Exists(Path.Combine(dir, "wp-config.php"))) return "wordpress";
            if (File.Exists(Path.Combine(dir, "wp-load.php"))) return "wordpress";
            if (File.Exists(Path.Combine(dir, "configuration.php")) &&
                Directory.Exists(Path.Combine(dir, "administrator"))) return "joomla";
            if (Directory.Exists(Path.Combine(dir, "core", "lib", "Drupal"))) return "drupal";
            if (File.Exists(Path.Combine(dir, "bin", "console")) &&
                Directory.Exists(Path.Combine(dir, "config", "packages"))) return "symfony";
            if (File.Exists(Path.Combine(dir, "system", "core", "CodeIgniter.php"))) return "codeigniter";
            if (File.Exists(Path.Combine(dir, "yii"))) return "yii";

            // composer.json content marker — slower but most accurate
            var composerJson = Path.Combine(dir, "composer.json");
            if (File.Exists(composerJson))
            {
                try
                {
                    var content = File.ReadAllText(composerJson);
                    if (content.Contains("\"laravel/framework\"")) return "laravel";
                    if (content.Contains("\"symfony/framework-bundle\"")) return "symfony";
                    if (content.Contains("\"symfony/symfony\"")) return "symfony";
                    if (content.Contains("\"nette/application\"")) return "nette";
                    if (content.Contains("\"nette/nette\"")) return "nette";
                    if (content.Contains("\"drupal/core\"")) return "drupal";
                    if (content.Contains("\"yiisoft/yii2\"")) return "yii";
                    if (content.Contains("\"slim/slim\"")) return "slim";
                    if (content.Contains("\"statamic/cms\"")) return "statamic";
                    if (content.Contains("\"codeigniter4/framework\"")) return "codeigniter";
                    if (content.Contains("\"cakephp/cakephp\"")) return "cakephp";
                }
                catch { /* unreadable composer.json */ }
            }
        }

        return null;
    }
}
