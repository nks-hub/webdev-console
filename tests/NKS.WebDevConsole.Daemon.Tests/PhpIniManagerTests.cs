using Microsoft.Extensions.Logging;
using Moq;
using NKS.WebDevConsole.Plugin.PHP;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Tests for <see cref="PhpIniManager.Render"/> — the pure template-rendering
/// surface that transforms <see cref="PhpIniOptions"/> into a php.ini file.
///
/// Critical because php.ini misconfiguration (wrong timezone, OPcache
/// contention with Xdebug, leaked error display in production) is exactly the
/// class of bug that's silent on the dev machine and painful in production —
/// or worse, information-discloses stack traces to users. These tests lock
/// the profile/mode branching so a template refactor can't silently flip the
/// Development/Production defaults.
/// </summary>
public sealed class PhpIniManagerTests
{
    private readonly PhpIniManager _sut;

    public PhpIniManagerTests()
    {
        var logger = new Mock<ILogger<PhpIniManager>>();
        _sut = new PhpIniManager(logger.Object);
    }

    private static PhpIniOptions DefaultOpts(
        PhpIniProfile profile = PhpIniProfile.Development,
        PhpIniMode mode = PhpIniMode.Web) =>
        new(
            Version: "8.3.10",
            Profile: profile,
            Mode: mode,
            ExtDir: @"C:\wdc\binaries\php\8.3.10\ext",
            ErrorLog: @"C:\wdc\logs\php.log",
            TmpDir: @"C:\wdc\tmp"
        );

    [Fact]
    public void Render_DevelopmentProfile_EnablesErrorDisplay()
    {
        var ini = _sut.Render(DefaultOpts(profile: PhpIniProfile.Development));

        Assert.Contains("display_errors = On", ini);
        Assert.Contains("display_startup_errors = On", ini);
    }

    [Fact]
    public void Render_ProductionProfile_DisablesErrorDisplay()
    {
        var ini = _sut.Render(DefaultOpts(profile: PhpIniProfile.Production));

        Assert.Contains("display_errors = Off", ini);
        Assert.Contains("display_startup_errors = Off", ini);
    }

    [Fact]
    public void Render_DevelopmentProfile_ReportsAllErrors()
    {
        var ini = _sut.Render(DefaultOpts(profile: PhpIniProfile.Development));

        // E_ALL (no masking) on dev so deprecation warnings are visible
        Assert.Contains("error_reporting = E_ALL", ini);
    }

    [Fact]
    public void Render_ProductionProfile_MasksDeprecations()
    {
        var ini = _sut.Render(DefaultOpts(profile: PhpIniProfile.Production));

        // Prod masks E_DEPRECATED and E_STRICT so third-party vendor noise
        // doesn't flood the error log
        Assert.Contains("~E_DEPRECATED", ini);
    }

    [Fact]
    public void Render_CliMode_RaisesMemoryAndTimeLimits()
    {
        var ini = _sut.Render(DefaultOpts(mode: PhpIniMode.Cli));

        Assert.Contains("memory_limit = 512M", ini);
        Assert.Contains("max_execution_time = 0", ini);
    }

    [Fact]
    public void Render_WebMode_UsesConservativeLimits()
    {
        var ini = _sut.Render(DefaultOpts(mode: PhpIniMode.Web));

        Assert.Contains("memory_limit = 256M", ini);
        Assert.Contains("max_execution_time = 30", ini);
    }

    [Fact]
    public void Render_XdebugEnabled_EmitsZendExtension()
    {
        var opts = DefaultOpts() with
        {
            XdebugEnabled = true,
            XdebugSo = @"C:\wdc\binaries\php\8.3.10\ext\php_xdebug.dll",
            XdebugMode = "debug",
            XdebugPort = 9003,
        };

        var ini = _sut.Render(opts);

        Assert.Contains("xdebug", ini, StringComparison.OrdinalIgnoreCase);
        // Forward slashes — Scriban template normalizes Windows paths
        Assert.Contains("php_xdebug.dll", ini);
    }

    [Fact]
    public void Render_XdebugEnabled_DisablesOpcache()
    {
        // Xdebug and OPcache are mutually exclusive — loading both causes
        // crashes on some PHP builds. The option resolution logic must
        // force OPcache off when Xdebug is on, regardless of OpcacheEnabled.
        var opts = DefaultOpts() with { XdebugEnabled = true, OpcacheEnabled = true };

        var ini = _sut.Render(opts);

        // opcache.enable=1 should NOT appear as an active setting
        Assert.DoesNotContain("opcache.enable=1", ini);
    }

    [Fact]
    public void Render_OpcacheEnabled_RevalidateFreqDiffersByProfile()
    {
        var dev = _sut.Render(DefaultOpts(profile: PhpIniProfile.Development));
        var prod = _sut.Render(DefaultOpts(profile: PhpIniProfile.Production));

        // Dev: revalidate every request for immediate reload; Prod: every 60s
        Assert.Contains("opcache.revalidate_freq=0", dev);
        Assert.Contains("opcache.revalidate_freq=60", prod);
    }

    [Fact]
    public void Render_Timezone_EmittedAsConfigured()
    {
        var opts = DefaultOpts() with { Timezone = "America/New_York" };
        var ini = _sut.Render(opts);

        Assert.Contains("date.timezone = America/New_York", ini);
    }

    [Fact]
    public void Render_CustomTimezone_OverridesDefault()
    {
        var defaultIni = _sut.Render(DefaultOpts());
        var customIni = _sut.Render(DefaultOpts() with { Timezone = "UTC" });

        Assert.Contains("Europe/Prague", defaultIni);
        Assert.Contains("UTC", customIni);
        Assert.DoesNotContain("Europe/Prague", customIni);
    }

    [Fact]
    public void Render_Extensions_EnabledAreEmitted()
    {
        var opts = DefaultOpts() with
        {
            Extensions =
            [
                ("mbstring", true),
                ("openssl", true),
                ("curl", true),
                ("gd", false),
            ],
        };

        var ini = _sut.Render(opts);

        Assert.Contains("extension=mbstring", ini);
        Assert.Contains("extension=openssl", ini);
        Assert.Contains("extension=curl", ini);
    }

    [Fact]
    public void Render_ExtDir_ForwardSlashPaths()
    {
        // Scriban template must normalize Windows \ to / so PHP's own path
        // parser doesn't choke on escape-like sequences in extension_dir
        var opts = DefaultOpts() with { ExtDir = @"C:\wdc\binaries\php\8.3.10\ext" };
        var ini = _sut.Render(opts);

        Assert.Contains("C:/wdc/binaries/php/8.3.10/ext", ini);
        Assert.DoesNotContain(@"C:\wdc\binaries", ini);
    }

    [Fact]
    public void Render_ErrorLog_ForwardSlashPaths()
    {
        var opts = DefaultOpts() with { ErrorLog = @"C:\wdc\logs\errors.log" };
        var ini = _sut.Render(opts);

        Assert.Contains("C:/wdc/logs/errors.log", ini);
    }

    [Fact]
    public void Render_ProducesNonEmptyOutput()
    {
        var ini = _sut.Render(DefaultOpts());
        Assert.NotNull(ini);
        Assert.True(ini.Length > 100, "Rendered php.ini should have substantial content");
    }

    [Fact]
    public void Render_RepeatedCalls_AreStable()
    {
        var opts = DefaultOpts();
        var first = _sut.Render(opts);
        var second = _sut.Render(opts);
        Assert.Equal(first, second);
    }
}
