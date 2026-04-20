using NKS.WebDevConsole.Daemon.Plugin;
using Xunit;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Covers the env-var parser used by <see cref="PluginCatalogSyncService.IsEnabled"/>.
/// The previous inline parser used <c>IsNullOrWhiteSpace</c> only as a null
/// guard and then did an Ordinal compare on the raw, non-trimmed string.
/// A trailing newline or space from shell quoting
/// (<c>NKS_WDC_PLUGIN_AUTOSYNC="1 "</c>) silently disabled auto-sync — this
/// suite locks the accepted inputs down.
/// </summary>
public class PluginCatalogSyncServiceTests
{
    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("True")]
    [InlineData("yes")]
    [InlineData("YES")]
    [InlineData("on")]
    [InlineData("On")]
    public void IsTruthyEnv_AcceptsCanonicalTruthyValues(string raw)
    {
        Assert.True(PluginCatalogSyncService.IsTruthyEnv(raw));
    }

    [Theory]
    [InlineData(" 1 ")]
    [InlineData("\t1")]
    [InlineData("true\n")]
    [InlineData("  yes  ")]
    public void IsTruthyEnv_TolerantOfSurroundingWhitespace(string raw)
    {
        Assert.True(PluginCatalogSyncService.IsTruthyEnv(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("no")]
    [InlineData("off")]
    [InlineData("enable")]
    [InlineData("2")]
    public void IsTruthyEnv_RejectsFalsyOrUnknownValues(string? raw)
    {
        Assert.False(PluginCatalogSyncService.IsTruthyEnv(raw));
    }
}
