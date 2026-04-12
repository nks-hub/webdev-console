using NKS.WebDevConsole.Core.Services;

namespace NKS.WebDevConsole.Core.Tests;

/// <summary>
/// Covers the pure / deterministic surface of <see cref="PortConflictDetector"/>:
/// range validation on CheckPort, well-known fallback candidate tables in
/// SuggestFallback, and <see cref="PortConflictInfo.ToUserMessage"/> formatting.
/// Does not test the netstat owner lookup — that depends on live TCP state.
/// </summary>
public sealed class PortConflictDetectorTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void CheckPort_OutOfRange_ReturnsNull(int port)
    {
        Assert.Null(PortConflictDetector.CheckPort(port));
    }

    [Fact]
    public void SuggestFallback_ExplicitCandidates_ReturnsFirstFree()
    {
        // Ports in the ephemeral range are almost certainly free on CI.
        var result = PortConflictDetector.SuggestFallback(80, new[] { 54321, 54322 });
        Assert.NotNull(result);
        Assert.Contains(result!.Value, new[] { 54321, 54322 });
    }

    [Fact]
    public void SuggestFallback_Port80_DefaultsIncludeCommonAlternatives()
    {
        // We can't guarantee any specific port is free, but for port 80 the
        // default candidates table should be used. If *all* defaults happen
        // to be held, method returns null — that's fine, we're only asserting
        // the result is in the expected candidate set when non-null.
        var result = PortConflictDetector.SuggestFallback(80);
        if (result is not null)
            Assert.Contains(result.Value, new[] { 8080, 8000, 8888 });
    }

    [Fact]
    public void SuggestFallback_UnknownPort_UsesArithmeticFallback()
    {
        // For unknown primary ports, candidates are primary+1, primary+10, primary+100.
        var result = PortConflictDetector.SuggestFallback(54320);
        if (result is not null)
            Assert.Contains(result.Value, new[] { 54321, 54330, 54420 });
    }

    [Fact]
    public void SuggestFallback_AllCandidatesUnavailable_ReturnsNull()
    {
        // Hack: pass an empty candidate enumerable — nothing can be free.
        var result = PortConflictDetector.SuggestFallback(80, Array.Empty<int>());
        Assert.Null(result);
    }

    [Fact]
    public void PortConflictInfo_ToUserMessage_WithNamedOwnerAndFallback()
    {
        var info = new PortConflictInfo(80, 1234, "httpd", "0.0.0.0");
        var msg = info.ToUserMessage(8080);
        Assert.Contains("80", msg);
        Assert.Contains("httpd", msg);
        Assert.Contains("1234", msg);
        Assert.Contains("8080", msg);
    }

    [Fact]
    public void PortConflictInfo_ToUserMessage_WithPidOnly()
    {
        // OwnerPid known but OwnerProcessName null (process gone between netstat and lookup)
        var info = new PortConflictInfo(3306, 5678, null, "127.0.0.1");
        var msg = info.ToUserMessage(3307);
        Assert.Contains("PID 5678", msg);
        Assert.Contains("3307", msg);
        Assert.DoesNotContain("''", msg); // no empty quoted name
    }

    [Fact]
    public void PortConflictInfo_ToUserMessage_UnknownOwner()
    {
        var info = new PortConflictInfo(443, null, null, "::");
        var msg = info.ToUserMessage(null);
        Assert.Contains("unknown process", msg);
        Assert.Contains("Stop the conflicting process", msg);
    }

    [Fact]
    public void PortConflictInfo_ToUserMessage_NoFallbackHint_WhenSuggestedNull()
    {
        var info = new PortConflictInfo(80, 1, "nginx", "0.0.0.0");
        var msg = info.ToUserMessage(null);
        Assert.DoesNotContain("Try port", msg);
        Assert.Contains("Stop the conflicting process", msg);
    }

    [Fact]
    public void PortConflictInfo_IsRecord_EqualByValue()
    {
        var a = new PortConflictInfo(80, 1234, "httpd", "0.0.0.0");
        var b = new PortConflictInfo(80, 1234, "httpd", "0.0.0.0");
        Assert.Equal(a, b);
    }
}
