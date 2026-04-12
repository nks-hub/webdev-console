using Microsoft.Extensions.Logging;
using Moq;
using NKS.WebDevConsole.Plugin.PHP;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Tests for <see cref="ComposerIntegration"/> — specifically <see cref="ComposerIntegration.ComposerPharPath"/>,
/// the pure path-construction property used by every caller of composer to
/// locate the phar file. A regression here (wrong subdir, wrong separator,
/// trailing slash handling) would make EnsureComposerAsync re-download
/// composer on every run or fail to find an already-downloaded copy.
/// </summary>
public sealed class ComposerIntegrationTests
{
    private static ComposerIntegration Make(string appDir) =>
        new(new Mock<ILogger<ComposerIntegration>>().Object, appDir);

    [Fact]
    public void ComposerPharPath_IsUnderBinSubdir()
    {
        var sut = Make(@"C:\wdc\app");
        Assert.Equal(
            Path.Combine(@"C:\wdc\app", "bin", "composer.phar"),
            sut.ComposerPharPath);
    }

    [Fact]
    public void ComposerPharPath_UnixStyle()
    {
        var sut = Make("/opt/wdc");
        // Path.Combine normalizes to platform separator
        var expected = Path.Combine("/opt/wdc", "bin", "composer.phar");
        Assert.Equal(expected, sut.ComposerPharPath);
    }

    [Fact]
    public void ComposerPharPath_AppDirWithTrailingSeparator_Honoured()
    {
        // Path.Combine tolerates trailing separators; verify the behaviour
        // is stable (no duplicate separators, no data loss).
        var sut = Make(@"C:\wdc\app\");
        Assert.EndsWith("composer.phar", sut.ComposerPharPath);
        Assert.Contains("bin", sut.ComposerPharPath);
        Assert.DoesNotContain(@"\\", sut.ComposerPharPath[3..]); // no double-sep after root
    }

    [Fact]
    public void ComposerPharPath_Deterministic()
    {
        var sut = Make(@"C:\wdc\app");
        Assert.Equal(sut.ComposerPharPath, sut.ComposerPharPath);
    }

    [Fact]
    public void ComposerPharPath_DifferentAppDirs_ProduceDifferentPaths()
    {
        var a = Make(@"C:\wdc\appA");
        var b = Make(@"C:\wdc\appB");
        Assert.NotEqual(a.ComposerPharPath, b.ComposerPharPath);
    }
}
