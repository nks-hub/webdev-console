using NKS.WebDevConsole.Daemon.Services;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// The PHP page toggles Xdebug and OPcache too; those are zend_extension
/// lines and must stay zend_extension, not gain a second extension= line.
/// </summary>
public sealed class PhpExtensionLinePatchTests
{
    [Fact]
    public void ZendExtensionWithPath_IsToggledInPlace()
    {
        var ini = new[] { "[xdebug]", @";zend_extension=""C:\php\ext\php_xdebug.dll""", "xdebug.mode=debug" };

        var on = PhpEndpoints.PatchExtensionLines(ini, "xdebug", enabled: true);
        var off = PhpEndpoints.PatchExtensionLines(on, "xdebug", enabled: false);

        Assert.Equal(@"zend_extension=""C:\php\ext\php_xdebug.dll""", on[1]);
        Assert.Equal(@";zend_extension=""C:\php\ext\php_xdebug.dll""", off[1]);
        Assert.Equal(3, off.Count);
    }

    [Theory]
    [InlineData("extension=curl")]
    [InlineData("extension=php_curl.dll")]
    [InlineData("extension = curl.so")]
    public void PlainExtensionSpellings_AreRecognised(string line)
    {
        var off = PhpEndpoints.PatchExtensionLines([line], "curl", enabled: false);

        Assert.Single(off);
        Assert.StartsWith(";extension", off[0].Replace(" ", ""));
    }

    [Fact]
    public void UnknownZendExtension_IsAppendedAsZendExtension()
    {
        var result = PhpEndpoints.PatchExtensionLines(["extension=curl"], "opcache", enabled: true);

        Assert.Equal("zend_extension=opcache", result[^1]);
        Assert.Equal("extension=curl", result[0]);
    }

    [Fact]
    public void SimilarNames_AreNotTouched()
    {
        var result = PhpEndpoints.PatchExtensionLines(["extension=pdo_mysql", "extension=mysqli"], "mysql", enabled: false);

        Assert.Equal("extension=pdo_mysql", result[0]);
        Assert.Equal("extension=mysqli", result[1]);
        Assert.Equal(";extension=mysql", result[^1]);
    }
}
