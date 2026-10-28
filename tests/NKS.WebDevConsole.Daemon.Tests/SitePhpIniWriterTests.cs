using Microsoft.Extensions.Logging.Abstractions;
using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Core.Services;
using NKS.WebDevConsole.Daemon.Sites;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// The per-site PHP error log: what the writer puts into the site's ini, and that the Logs tab only
/// switches to that log once the vhost really points PHP at it.
/// </summary>
public sealed class SitePhpIniWriterTests : IDisposable
{
    private const string Domain = "shop.loc";
    private readonly string _root;
    private readonly string _sitesPhp;
    private readonly string _phpLogs;
    private readonly string _apache;
    private readonly SitePhpIniWriter _writer;

    public SitePhpIniWriterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"nks-wdc-site-php-ini-{Guid.NewGuid():N}");
        _sitesPhp = Path.Combine(_root, "sites-php");
        _phpLogs = Path.Combine(_root, "logs", "php");
        _apache = Path.Combine(_root, "apache");
        var binaries = Path.Combine(_root, "binaries");
        Directory.CreateDirectory(Path.Combine(binaries, "php", "8.5.12"));
        File.WriteAllText(Path.Combine(binaries, "php", "8.5.12", "php.ini"),
            "memory_limit = 256M\nerror_log = \"C:/global/php8.5-errors.log\"\n");
        Directory.CreateDirectory(Path.Combine(_apache, "conf", "sites-enabled"));
        _writer = new SitePhpIniWriter(NullLogger<SitePhpIniWriter>.Instance, _sitesPhp, binaries, _phpLogs);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private string SiteLog(string domain = Domain) => Path.Combine(_phpLogs, $"{domain}-errors.log");
    private string IniDir(string domain = Domain) => Path.Combine(_sitesPhp, domain);

    private static SiteConfig Php(string domain = Domain) => new() { Domain = domain, PhpVersion = "8.5" };

    private void Vhost(string directives) =>
        File.WriteAllText(Path.Combine(_apache, "conf", "sites-enabled", $"{Domain}.conf"),
            $"<VirtualHost *:80>\n    ServerName {Domain}\n{directives}</VirtualHost>\n");

    [Fact]
    public void WriteSiteLog_PhpSite_WritesAScanDirIniWithTheSiteLog()
    {
        var file = _writer.WriteSiteLog(Php());

        Assert.Equal(Path.Combine(IniDir(), "conf.d", SitePhpErrorLog.ScanFileName), file);
        var text = File.ReadAllText(file!);
        Assert.Contains($"error_log = \"{SiteLog().Replace('\\', '/')}\"", text);
        Assert.Contains("log_errors = 1", text);
        Assert.True(Directory.Exists(_phpLogs));
    }

    [Theory]
    [InlineData("none", 0)]
    [InlineData("8.5", 3000)]
    public void WriteSiteLog_StaticAndNodeSites_GetNoIni_AndLoseAStaleOne(string phpVersion, int nodePort)
    {
        _writer.WriteSiteLog(Php());
        Assert.True(Directory.Exists(Path.Combine(IniDir(), "conf.d")));

        var site = new SiteConfig { Domain = Domain, PhpVersion = phpVersion, NodeUpstreamPort = nodePort };
        Assert.Null(_writer.WriteSiteLog(site));

        Assert.False(Directory.Exists(Path.Combine(IniDir(), "conf.d")));
    }

    [Fact]
    public void Write_SiteWithOverrides_CopyEndsWithTheSiteLog_SoItWinsOverTheGlobalOne()
    {
        var site = Php();
        site.PhpSettings = new SitePhpSettings { MemoryLimit = "512M" };

        var ini = _writer.Write(site);

        var text = File.ReadAllText(ini!);
        Assert.Contains("memory_limit = 512M", text);
        Assert.EndsWith(SitePhpErrorLog.IniBlock(SiteLog()), text);
        Assert.Contains("error_log = \"C:/global/php8.5-errors.log\"", text);
    }

    [Fact]
    public void BothWrittenFiles_AreAcceptedBySiteLogResolution_OnceTheVhostPointsPhpAtThem()
    {
        var site = Php();
        site.PhpSettings = new SitePhpSettings { MemoryLimit = "512M" };
        var ini = _writer.Write(site)!;
        var scanDir = Path.GetDirectoryName(_writer.WriteSiteLog(site))!;

        string? Resolve() => SitePhpErrorLog.Resolve(Domain, [_apache], IniDir(), SiteLog());

        Vhost("");
        Assert.Null(Resolve());
        Vhost($"    FcgidInitialEnv PHPRC \"{ini.Replace('\\', '/')}\"\n");
        Assert.Equal(SiteLog(), Resolve());
        Vhost($"    FcgidInitialEnv PHP_INI_SCAN_DIR \"{scanDir.Replace('\\', '/')}\"\n");
        Assert.Equal(SiteLog(), Resolve());
    }

    [Fact]
    public void Remove_DeletesBothFiles()
    {
        var site = Php();
        site.PhpSettings = new SitePhpSettings { MemoryLimit = "512M" };
        _writer.Write(site);
        _writer.WriteSiteLog(site);

        _writer.Remove(Domain);

        Assert.False(Directory.Exists(IniDir()));
    }

    // ── What the Logs tab reads ───────────────────────────────────────────────

    private static string PhpLine(string message) => $"[13-Apr-2026 10:00:00 UTC] PHP Fatal error: {message}";

    private IReadOnlyList<string> Messages(string? sitePhpLog) =>
        SiteErrorLogReader.Read(Domain, [_apache], Path.Combine(_root, "apache-global.log"), _phpLogs, sitePhpLog, 100, null)
            .Select(e => e.Message)
            .ToList();

    [Fact]
    public void WithoutASitePhpLog_TheVersionLogIsRead()
    {
        Directory.CreateDirectory(_phpLogs);
        File.WriteAllLines(Path.Combine(_phpLogs, "php8.5-errors.log"), [PhpLine("from the version log")]);

        Assert.Equal(["from the version log"], Messages(null));
    }

    [Fact]
    public void WithASitePhpLog_OnlyThatLogIsRead_NotTheVersionLogOfOtherSites()
    {
        Directory.CreateDirectory(_phpLogs);
        File.WriteAllLines(Path.Combine(_phpLogs, "php8.5-errors.log"), [PhpLine("from another site")]);

        Assert.Empty(Messages(SiteLog()));

        File.WriteAllLines(SiteLog(), [PhpLine("from this site")]);

        Assert.Equal(["from this site"], Messages(SiteLog()));
    }

    [Fact]
    public void FpmMasterLog_IsStillReadWithASitePhpLog()
    {
        Directory.CreateDirectory(_phpLogs);
        File.WriteAllLines(Path.Combine(_phpLogs, "php85-fpm-error.log"), ["[13-Apr-2026 10:00:00] ERROR: fpm could not bind"]);

        var entries = SiteErrorLogReader.Read(Domain, [_apache], Path.Combine(_root, "g.log"), _phpLogs, SiteLog(), 100, null);

        Assert.Single(entries);
        Assert.Equal("php-fpm-error", entries[0].Source);
    }
}
