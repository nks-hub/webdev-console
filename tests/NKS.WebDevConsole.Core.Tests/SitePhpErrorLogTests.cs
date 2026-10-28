using NKS.WebDevConsole.Core.Services;

namespace NKS.WebDevConsole.Core.Tests;

/// <summary>
/// Tests for <see cref="SitePhpErrorLog"/>: a site's PHP log is only the one to read when the site's
/// PHP process is really pointed at an ini that writes to it.
/// </summary>
public sealed class SitePhpErrorLogTests : IDisposable
{
    private const string Domain = "shop.loc";
    private readonly string _root;
    private readonly string _apache;
    private readonly string _iniDir;
    private readonly string _log;

    public SitePhpErrorLogTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"nks-wdc-site-php-log-{Guid.NewGuid():N}");
        _apache = Path.Combine(_root, "apache");
        _iniDir = Path.Combine(_root, "sites-php", Domain);
        _log = Path.Combine(_root, "logs", "php", $"{Domain}-errors.log");
        Directory.CreateDirectory(Path.Combine(_apache, "conf", "sites-enabled"));
        Directory.CreateDirectory(Path.Combine(_iniDir, "conf.d"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private void Vhost(string directives) =>
        File.WriteAllText(Path.Combine(_apache, "conf", "sites-enabled", $"{Domain}.conf"),
            $"<VirtualHost *:80>\n    ServerName {Domain}\n{directives}</VirtualHost>\n");

    private string? Resolve() => SitePhpErrorLog.Resolve(Domain, [_apache], _iniDir, _log);

    private static string Forward(string path) => path.Replace('\\', '/');

    private void SiteIni(string content) => File.WriteAllText(Path.Combine(_iniDir, "php.ini"), content);
    private void ScanIni(string content) => File.WriteAllText(Path.Combine(_iniDir, "conf.d", SitePhpErrorLog.ScanFileName), content);

    [Fact]
    public void PhprcPointsAtTheSiteIni_WhichSetsTheLog_TheSiteLogApplies()
    {
        SiteIni("memory_limit = 256M\nerror_log = \"C:/global/php8.5-errors.log\"\n" + SitePhpErrorLog.IniBlock(_log));
        Vhost($"    FcgidInitialEnv PHPRC \"{Forward(Path.Combine(_iniDir, "php.ini"))}\"\n");

        Assert.Equal(_log, Resolve());
    }

    [Fact]
    public void ScanDirPointsAtTheSiteDirectory_WhichSetsTheLog_TheSiteLogApplies()
    {
        ScanIni(SitePhpErrorLog.IniBlock(_log));
        Vhost($"    FcgidInitialEnv PHP_INI_SCAN_DIR \"{Forward(Path.Combine(_iniDir, "conf.d"))}\"\n");

        Assert.Equal(_log, Resolve());
    }

    [Fact]
    public void BackslashesAndTheDirectoryFormOfPhprcAreAccepted()
    {
        SiteIni(SitePhpErrorLog.IniBlock(_log));
        Vhost($"    FcgidInitialEnv PHPRC \"{_iniDir.TrimEnd('\\')}\\\"\n");

        Assert.Equal(_log, Resolve());
    }

    [Fact]
    public void IniWithoutTheSiteLog_Phprc_VersionLogStillApplies()
    {
        // A copy of the global ini written before sites had their own log.
        SiteIni("memory_limit = 256M\nerror_log = \"C:/global/php8.5-errors.log\"\n");
        Vhost($"    FcgidInitialEnv PHPRC \"{Forward(Path.Combine(_iniDir, "php.ini"))}\"\n");

        Assert.Null(Resolve());
    }

    [Fact]
    public void LogSetInTheIniButNobodyPointsPhpAtIt_VersionLogStillApplies()
    {
        ScanIni(SitePhpErrorLog.IniBlock(_log));
        Vhost("    DirectoryIndex index.php\n");

        Assert.Null(Resolve());
    }

    [Fact]
    public void NoVhostYet_VersionLogStillApplies()
    {
        ScanIni(SitePhpErrorLog.IniBlock(_log));

        Assert.Null(Resolve());
    }

    [Fact]
    public void DirectiveForAnotherSite_DoesNotCount()
    {
        ScanIni(SitePhpErrorLog.IniBlock(_log));
        Vhost($"    FcgidInitialEnv PHP_INI_SCAN_DIR \"{Forward(Path.Combine(_root, "sites-php", "other.loc", "conf.d"))}\"\n");

        Assert.Null(Resolve());
    }

    [Fact]
    public void ALaterErrorLogInTheIniWins()
    {
        SiteIni(SitePhpErrorLog.IniBlock(_log) + "error_log = \"C:/elsewhere/php.log\"\n");
        Vhost($"    FcgidInitialEnv PHPRC \"{Forward(Path.Combine(_iniDir, "php.ini"))}\"\n");

        Assert.Null(Resolve());
    }

    [Fact]
    public void ACommentedOutErrorLogDoesNotCount()
    {
        ScanIni("; error_log = \"" + Forward(_log) + "\"\n");
        Vhost($"    FcgidInitialEnv PHP_INI_SCAN_DIR \"{Forward(Path.Combine(_iniDir, "conf.d"))}\"\n");

        Assert.Null(Resolve());
    }

    [Fact]
    public void IniBlock_QuotesTheLogAndUsesForwardSlashes()
    {
        Assert.Equal("error_log = \"C:/Users/Jane Doe/.wdc/logs/php/shop.loc-errors.log\"\nlog_errors = 1\n",
            SitePhpErrorLog.IniBlock(@"C:\Users\Jane Doe\.wdc\logs\php\shop.loc-errors.log"));
    }
}
