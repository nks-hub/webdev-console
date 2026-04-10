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

    public string? DetectFramework(string documentRoot)
    {
        if (File.Exists(Path.Combine(documentRoot, "artisan"))) return "laravel";
        if (File.Exists(Path.Combine(documentRoot, "wp-config.php"))) return "wordpress";
        var composerJson = Path.Combine(documentRoot, "composer.json");
        if (File.Exists(composerJson))
        {
            var content = File.ReadAllText(composerJson);
            if (content.Contains("nette/application")) return "nette";
            if (content.Contains("symfony/framework-bundle")) return "symfony";
            if (content.Contains("laravel/framework")) return "laravel";
        }
        return null;
    }
}
