using NKS.WebDevConsole.Daemon.Services;
using Xunit;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Pins the accepted truthy surface for daemon env-var flags. Each inline
/// parser that grew up alongside a feature (plugin auto-sync, hosts-UAC
/// skip, …) previously had its own tolerance for whitespace + case, and
/// the plugin auto-sync one in particular silently rejected
/// <c>NKS_WDC_PLUGIN_AUTOSYNC="1 "</c> because it compared the raw
/// unsurrendered value via <c>StringComparison.Ordinal</c>.
/// </summary>
public class EnvFlagsTests
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
    public void IsTruthy_AcceptsCanonicalTruthyValues(string raw)
    {
        Assert.True(EnvFlags.IsTruthy(raw));
    }

    [Theory]
    [InlineData(" 1 ")]
    [InlineData("\t1")]
    [InlineData("true\n")]
    [InlineData("  yes  ")]
    public void IsTruthy_TolerantOfSurroundingWhitespace(string raw)
    {
        Assert.True(EnvFlags.IsTruthy(raw));
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
    public void IsTruthy_RejectsFalsyOrUnknownValues(string? raw)
    {
        Assert.False(EnvFlags.IsTruthy(raw));
    }
}
