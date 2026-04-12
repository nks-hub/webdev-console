using Microsoft.Extensions.Logging;
using Moq;
using Tomlyn;
using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Daemon.Config;
using NKS.WebDevConsole.Daemon.Sites;

namespace NKS.WebDevConsole.Daemon.Tests;

public class SiteManagerTests : IDisposable
{
    private readonly Mock<ILogger<SiteManager>> _loggerMock = new();
    private readonly TemplateEngine _templateEngine = new();
    private readonly ConfigValidator _validator;
    private readonly AtomicWriter _writer = new();
    private readonly string _sitesDir;
    private readonly string _generatedDir;
    private readonly string _tempDir;
    private readonly SiteManager _manager;

    public SiteManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nks-site-tests-" + Guid.NewGuid().ToString("N"));
        _sitesDir = Path.Combine(_tempDir, "sites");
        _generatedDir = Path.Combine(_tempDir, "generated");

        var validatorLogger = new Mock<ILogger<ConfigValidator>>();
        _validator = new ConfigValidator(validatorLogger.Object);

        _manager = new SiteManager(
            _loggerMock.Object,
            _templateEngine,
            _validator,
            _writer,
            _sitesDir,
            _generatedDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public async Task CreateAsync_CreatesTomlFile()
    {
        var site = new SiteConfig
        {
            Domain = "mysite.loc",
            DocumentRoot = "C:/htdocs/mysite",
            PhpVersion = "8.3"
        };

        await _manager.CreateAsync(site);

        var tomlPath = Path.Combine(_sitesDir, "mysite.loc.toml");
        Assert.True(File.Exists(tomlPath));
        var content = await File.ReadAllTextAsync(tomlPath);
        Assert.Contains("mysite.loc", content);
    }

    [Fact]
    public async Task CreateAsync_AddsSiteToInMemoryCollection()
    {
        var site = new SiteConfig
        {
            Domain = "test.loc",
            DocumentRoot = "C:/htdocs/test"
        };

        await _manager.CreateAsync(site);

        Assert.True(_manager.Sites.ContainsKey("test.loc"));
    }

    [Fact]
    public async Task CreateAsync_ArchivesExistingToml_WhenRecreatingDomain()
    {
        var domain = "existing.loc";
        var targetPath = Path.Combine(_sitesDir, $"{domain}.toml");
        await File.WriteAllTextAsync(targetPath, "domain = \"existing.loc\"\ndocumentRoot = \"C:/htdocs/old\"\n");

        var site = new SiteConfig
        {
            Domain = domain,
            DocumentRoot = "C:/htdocs/new",
            PhpVersion = "8.3"
        };

        await _manager.CreateAsync(site);

        var historyDir = Path.Combine(_sitesDir, "history");
        var historyFiles = Directory.GetFiles(historyDir, $"{domain}.toml.*");
        Assert.Single(historyFiles);
        var archived = await File.ReadAllTextAsync(historyFiles[0]);
        Assert.Contains("C:/htdocs/old", archived);
        var current = await File.ReadAllTextAsync(targetPath);
        Assert.Contains("C:/htdocs/new", current);
    }

    [Fact]
    public async Task Get_ReturnsCreatedSite()
    {
        var site = new SiteConfig
        {
            Domain = "app.loc",
            DocumentRoot = "C:/htdocs/app",
            PhpVersion = "8.4",
            SslEnabled = true
        };
        await _manager.CreateAsync(site);

        var result = _manager.Get("app.loc");

        Assert.NotNull(result);
        Assert.Equal("app.loc", result.Domain);
        Assert.Equal("C:/htdocs/app", result.DocumentRoot);
        Assert.Equal("8.4", result.PhpVersion);
        Assert.True(result.SslEnabled);
    }

    [Fact]
    public void Get_ReturnsNull_ForNonexistentDomain()
    {
        var result = _manager.Get("nonexistent.loc");

        Assert.Null(result);
    }

    [Fact]
    public async Task Delete_RemovesSiteAndFiles()
    {
        var site = new SiteConfig { Domain = "delete-me.loc", DocumentRoot = "C:/htdocs/del" };
        await _manager.CreateAsync(site);

        // Verify it exists first
        Assert.NotNull(_manager.Get("delete-me.loc"));
        Assert.True(File.Exists(Path.Combine(_sitesDir, "delete-me.loc.toml")));

        var removed = _manager.Delete("delete-me.loc");

        Assert.True(removed);
        Assert.Null(_manager.Get("delete-me.loc"));
        Assert.False(File.Exists(Path.Combine(_sitesDir, "delete-me.loc.toml")));
    }

    [Fact]
    public void Delete_ReturnsFalse_ForNonexistentDomain()
    {
        var removed = _manager.Delete("ghost.loc");

        Assert.False(removed);
    }

    [Fact]
    public void DetectFramework_FindsLaravel_ByArtisanFile()
    {
        var docRoot = Path.Combine(_tempDir, "laravel-app");
        Directory.CreateDirectory(docRoot);
        File.WriteAllText(Path.Combine(docRoot, "artisan"), "#!/usr/bin/env php");

        var framework = _manager.DetectFramework(docRoot);

        Assert.Equal("laravel", framework);
    }

    [Fact]
    public void DetectFramework_FindsWordPress_ByWpConfig()
    {
        var docRoot = Path.Combine(_tempDir, "wp-app");
        Directory.CreateDirectory(docRoot);
        File.WriteAllText(Path.Combine(docRoot, "wp-config.php"), "<?php");

        var framework = _manager.DetectFramework(docRoot);

        Assert.Equal("wordpress", framework);
    }

    [Fact]
    public void DetectFramework_FindsNette_ByComposerJson()
    {
        var docRoot = Path.Combine(_tempDir, "nette-app");
        Directory.CreateDirectory(docRoot);
        File.WriteAllText(Path.Combine(docRoot, "composer.json"),
            """{"require": {"nette/application": "^3.2"}}""");

        var framework = _manager.DetectFramework(docRoot);

        Assert.Equal("nette", framework);
    }

    [Fact]
    public void DetectFramework_FindsSymfony_ByComposerJson()
    {
        var docRoot = Path.Combine(_tempDir, "symfony-app");
        Directory.CreateDirectory(docRoot);
        File.WriteAllText(Path.Combine(docRoot, "composer.json"),
            """{"require": {"symfony/framework-bundle": "^6.0"}}""");

        var framework = _manager.DetectFramework(docRoot);

        Assert.Equal("symfony", framework);
    }

    [Fact]
    public void DetectFramework_FindsLaravel_ByComposerJson()
    {
        var docRoot = Path.Combine(_tempDir, "laravel-composer");
        Directory.CreateDirectory(docRoot);
        File.WriteAllText(Path.Combine(docRoot, "composer.json"),
            """{"require": {"laravel/framework": "^10.0"}}""");

        var framework = _manager.DetectFramework(docRoot);

        Assert.Equal("laravel", framework);
    }

    [Fact]
    public void DetectFramework_ReturnsNull_ForUnknownProject()
    {
        var docRoot = Path.Combine(_tempDir, "plain-html");
        Directory.CreateDirectory(docRoot);
        File.WriteAllText(Path.Combine(docRoot, "index.html"), "<html></html>");

        var framework = _manager.DetectFramework(docRoot);

        Assert.Null(framework);
    }

    [Fact]
    public void DetectFramework_ReturnsNull_ForEmptyDirectory()
    {
        var docRoot = Path.Combine(_tempDir, "empty-dir");
        Directory.CreateDirectory(docRoot);

        var framework = _manager.DetectFramework(docRoot);

        Assert.Null(framework);
    }

    [Fact]
    public void DetectFramework_FindsNextjs_ByPackageJson()
    {
        var docRoot = Path.Combine(_tempDir, "nextapp");
        Directory.CreateDirectory(docRoot);
        File.WriteAllText(Path.Combine(docRoot, "package.json"),
            @"{""dependencies"":{""next"":""^15.0.0"",""react"":""^19.0.0""}}");

        Assert.Equal("nextjs", _manager.DetectFramework(docRoot));
    }

    [Fact]
    public void DetectFramework_FindsNuxt_ByPackageJson()
    {
        var docRoot = Path.Combine(_tempDir, "nuxtapp");
        Directory.CreateDirectory(docRoot);
        File.WriteAllText(Path.Combine(docRoot, "package.json"),
            @"{""dependencies"":{""nuxt"":""^3.15.0""}}");

        Assert.Equal("nuxt", _manager.DetectFramework(docRoot));
    }

    [Fact]
    public void DetectFramework_FindsExpress_ByPackageJson()
    {
        var docRoot = Path.Combine(_tempDir, "expressapp");
        Directory.CreateDirectory(docRoot);
        File.WriteAllText(Path.Combine(docRoot, "package.json"),
            @"{""dependencies"":{""express"":""^5.0.0""}}");

        Assert.Equal("express", _manager.DetectFramework(docRoot));
    }

    [Fact]
    public void DetectFramework_FindsAstro_ByPackageJson()
    {
        var docRoot = Path.Combine(_tempDir, "astroapp");
        Directory.CreateDirectory(docRoot);
        File.WriteAllText(Path.Combine(docRoot, "package.json"),
            @"{""dependencies"":{""astro"":""^5.0.0""}}");

        Assert.Equal("astro", _manager.DetectFramework(docRoot));
    }

    [Fact]
    public void DetectFramework_PrefersNextOverExpress_InSamePackageJson()
    {
        var docRoot = Path.Combine(_tempDir, "next-express");
        Directory.CreateDirectory(docRoot);
        File.WriteAllText(Path.Combine(docRoot, "package.json"),
            @"{""dependencies"":{""next"":""^15.0.0"",""express"":""^5.0.0""}}");

        Assert.Equal("nextjs", _manager.DetectFramework(docRoot));
    }

    [Fact]
    public void DetectFramework_PrefersPhpOverNode_WhenBothPresent()
    {
        var docRoot = Path.Combine(_tempDir, "laravel-next");
        Directory.CreateDirectory(docRoot);
        File.WriteAllText(Path.Combine(docRoot, "artisan"), "#!/usr/bin/env php\n");
        File.WriteAllText(Path.Combine(docRoot, "package.json"),
            @"{""dependencies"":{""next"":""^15.0.0""}}");

        Assert.Equal("laravel", _manager.DetectFramework(docRoot));
    }

    [Fact]
    public void DetectFramework_NoFalsePositive_WhenNameAppearsInDescription()
    {
        var docRoot = Path.Combine(_tempDir, "not-next");
        Directory.CreateDirectory(docRoot);
        File.WriteAllText(Path.Combine(docRoot, "package.json"),
            @"{""name"":""my-app"",""description"":""The next generation tool for express delivery"",""dependencies"":{""lodash"":""^4.0.0""}}");

        Assert.Null(_manager.DetectFramework(docRoot));
    }

    [Fact]
    public void DetectFramework_NoFalsePositive_OnSimilarPackageNames()
    {
        var docRoot = Path.Combine(_tempDir, "express-sub");
        Directory.CreateDirectory(docRoot);
        File.WriteAllText(Path.Combine(docRoot, "package.json"),
            @"{""dependencies"":{""express-validator"":""^7.0.0"",""next-auth"":""^5.0.0""}}");

        Assert.Null(_manager.DetectFramework(docRoot));
    }

    // ── DetectPhpVersion ──────────────────────────────────────────────────

    [Fact]
    public void DetectPhpVersion_ReturnsNull_ForMissingDirectory()
    {
        Assert.Null(SiteManager.DetectPhpVersion("/nonexistent/path"));
    }

    [Fact]
    public void DetectPhpVersion_ReturnsNull_ForEmptyDirectory()
    {
        var dir = Path.Combine(_tempDir, "empty-php");
        Directory.CreateDirectory(dir);
        Assert.Null(SiteManager.DetectPhpVersion(dir));
    }

    [Fact]
    public void DetectPhpVersion_FindsVersionFile_InDocroot()
    {
        var dir = Path.Combine(_tempDir, "php-docroot");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".php-version"), "8.3");
        Assert.Equal("8.3", SiteManager.DetectPhpVersion(dir));
    }

    [Fact]
    public void DetectPhpVersion_NormalizesToMajorMinor()
    {
        var dir = Path.Combine(_tempDir, "php-full");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".php-version"), "8.4.20");
        Assert.Equal("8.4", SiteManager.DetectPhpVersion(dir));
    }

    [Fact]
    public void DetectPhpVersion_FindsVersionFile_InParent()
    {
        var parent = Path.Combine(_tempDir, "php-parent");
        var child = Path.Combine(parent, "public");
        Directory.CreateDirectory(child);
        File.WriteAllText(Path.Combine(parent, ".php-version"), "7.4");
        Assert.Equal("7.4", SiteManager.DetectPhpVersion(child));
    }

    [Fact]
    public void DetectPhpVersion_ReturnsNull_ForNullInput()
    {
        Assert.Null(SiteManager.DetectPhpVersion(null!));
    }

    [Fact]
    public void LoadAll_ReadsExistingTomlFiles()
    {
        // Use the same serializer that SiteManager.CreateAsync uses
        var siteObj = new SiteConfig
        {
            Domain = "loaded.loc",
            DocumentRoot = "C:/htdocs/loaded",
            PhpVersion = "8.3"
        };
        var toml = TomlSerializer.Serialize(siteObj);
        File.WriteAllText(Path.Combine(_sitesDir, "loaded.loc.toml"), toml);

        _manager.LoadAll();

        Assert.Single(_manager.Sites);
        var site = _manager.Get("loaded.loc");
        Assert.NotNull(site);
        Assert.Equal("C:/htdocs/loaded", site.DocumentRoot);
    }

    [Fact]
    public void LoadAll_SkipsInvalidToml_WithWarning()
    {
        File.WriteAllText(Path.Combine(_sitesDir, "bad.toml"), "this is not valid toml = [[[");

        _manager.LoadAll();

        Assert.Empty(_manager.Sites);
    }

    [Fact]
    public void LoadAll_LoadsMultipleFiles()
    {
        var site1 = new SiteConfig { Domain = "site1.loc", DocumentRoot = "C:/htdocs/site1", PhpVersion = "8.3" };
        var site2 = new SiteConfig { Domain = "site2.loc", DocumentRoot = "C:/htdocs/site2", PhpVersion = "8.4", SslEnabled = true };

        File.WriteAllText(Path.Combine(_sitesDir, "site1.loc.toml"), TomlSerializer.Serialize(site1));
        File.WriteAllText(Path.Combine(_sitesDir, "site2.loc.toml"), TomlSerializer.Serialize(site2));

        _manager.LoadAll();

        Assert.Equal(2, _manager.Sites.Count);
        Assert.NotNull(_manager.Get("site1.loc"));
        Assert.NotNull(_manager.Get("site2.loc"));
    }

    [Fact]
    public void LoadAll_UsesDomainFromFilename_WhenDomainEmpty()
    {
        // Serialize a SiteConfig with empty domain; the filename should become the key
        var site = new SiteConfig { Domain = "", DocumentRoot = "C:/htdocs/noname", PhpVersion = "8.3" };
        File.WriteAllText(Path.Combine(_sitesDir, "fromfile.loc.toml"), TomlSerializer.Serialize(site));

        _manager.LoadAll();

        Assert.True(_manager.Sites.ContainsKey("fromfile.loc"));
    }
}
