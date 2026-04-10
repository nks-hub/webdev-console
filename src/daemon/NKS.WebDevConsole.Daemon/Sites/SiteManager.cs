using System.Runtime.InteropServices;
using Tomlyn;
using NKS.WebDevConsole.Core.Models;
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
        foreach (var file in Directory.GetFiles(_sitesDir, "*.toml"))
        {
            try
            {
                var toml = File.ReadAllText(file);
                var model = TomlSerializer.Deserialize<SiteConfig>(toml)
                    ?? throw new InvalidOperationException($"Failed to deserialize {file}");
                if (string.IsNullOrEmpty(model.Domain))
                    model.Domain = Path.GetFileNameWithoutExtension(file);
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

    public async Task<SiteConfig> CreateAsync(SiteConfig site)
    {
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
        var toml = TomlSerializer.Serialize(site);
        var path = Path.Combine(_sitesDir, $"{site.Domain}.toml");
        await _writer.WriteAsync(path, toml);
        _sites[site.Domain] = site;
        return site;
    }

    public bool Delete(string domain)
    {
        var path = Path.Combine(_sitesDir, $"{domain}.toml");
        if (File.Exists(path)) File.Delete(path);
        var genPath = Path.Combine(_generatedDir, $"{domain}.conf");
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
                cert_path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".wdc", "ssl", $"{site.Domain}.crt"),
                key_path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".wdc", "ssl", $"{site.Domain}.key"),
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
