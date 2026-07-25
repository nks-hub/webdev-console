using System.Net;
using System.Net.Sockets;
using NKS.WebDevConsole.Core.Services;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Tests for <see cref="PortConflictDetector"/>. These cover the SPEC §9
/// behaviour: return null when a port is free, return a populated
/// <see cref="PortConflictInfo"/> when it's held, and return meaningful
/// fallback suggestions for well-known ports.
///
/// Process-identification via netstat is Windows-only and depends on host
/// state, so it's NOT exercised here. Tests use ephemeral TcpListeners on
/// 127.0.0.1 to keep them deterministic and platform-independent.
/// </summary>
public class PortConflictDetectorTests
{
    [Fact]
    public void CheckPort_ReturnsNull_WhenPortIsFree()
    {
        // Pick a high random port that's extremely unlikely to be in use.
        var port = GetFreePort();
        var result = PortConflictDetector.CheckPort(port);
        Assert.Null(result);
    }

    [Fact]
    public void CheckPort_ReturnsConflict_WhenPortIsBound()
    {
        // Bind a real TcpListener on a free port, then check it.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var result = PortConflictDetector.CheckPort(port);
            Assert.NotNull(result);
            Assert.Equal(port, result!.Port);
            // OwnerPid/OwnerProcessName may be null depending on platform —
            // the critical invariant is that we DETECTED the conflict.
        }
        finally
        {
            listener.Stop();
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    [InlineData(99999)]
    public void CheckPort_ReturnsNull_ForOutOfRangePorts(int port)
    {
        Assert.Null(PortConflictDetector.CheckPort(port));
    }

    [Fact]
    public void ToUserMessage_IncludesOwnerAndFallback_WhenBothKnown()
    {
        var info = new PortConflictInfo(80, 1234, "httpd", "0.0.0.0");
        var msg = info.ToUserMessage(suggestedFallback: 8080);
        Assert.Contains("Port 80", msg);
        Assert.Contains("httpd", msg);
        Assert.Contains("1234", msg);
        Assert.Contains("8080", msg);
    }

    [Fact]
    public void ToUserMessage_SaysUnknownProcess_WhenOwnerMissing()
    {
        var info = new PortConflictInfo(80, null, null, null);
        var msg = info.ToUserMessage(suggestedFallback: null);
        Assert.Contains("Port 80", msg);
        Assert.Contains("unknown", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToUserMessage_FallsBackToPidOnly_WhenNameMissing()
    {
        var info = new PortConflictInfo(3306, 5678, null, null);
        var msg = info.ToUserMessage(suggestedFallback: 3307);
        Assert.Contains("Port 3306", msg);
        Assert.Contains("5678", msg);
        Assert.Contains("3307", msg);
    }

    // The built-in-map cases can only assert set membership: whether any
    // candidate is actually free depends on what else is listening on the
    // machine running the suite, so asserting NotNull here would be
    // asserting about the host, not about the code. The "a free candidate
    // is returned" half of the contract is covered deterministically by
    // SuggestFallback_AcceptsCustomCandidates below.
    [Theory]
    [InlineData(80, new[] { 8080, 8000, 8888 })]
    [InlineData(443, new[] { 8443, 4443, 9443 })]
    [InlineData(3306, new[] { 3307, 3308, 33060 })]
    [InlineData(6379, new[] { 6380, 6381, 16379 })]
    [InlineData(1025, new[] { 1026, 2525, 25252 })]
    [InlineData(8025, new[] { 8026, 18025 })]
    // Ports with no entry in the map fall through to the generic +1/+10/+100 rule.
    [InlineData(40000, new[] { 40001, 40010, 40100 })]
    public void SuggestFallback_StaysWithinDocumentedCandidates(int primary, int[] expected)
    {
        var fallback = PortConflictDetector.SuggestFallback(primary);
        if (fallback is not null)
            Assert.Contains(fallback.Value, expected);
    }

    [Fact]
    public void SuggestFallback_AcceptsCustomCandidates()
    {
        // Bind-probed free ports, so this asserts the real contract
        // (custom list honoured, first free candidate wins) without
        // depending on whichever ports this machine happens to have free.
        var free = GetFreePort();
        var alsoFree = GetFreePort();

        var fallback = PortConflictDetector.SuggestFallback(80, new[] { free, alsoFree });

        Assert.Equal(free, fallback);
    }

    [Fact]
    public void SuggestFallback_ReturnsNull_WhenNoCandidateIsFree()
    {
        using var held = new TcpListener(IPAddress.Loopback, 0);
        held.Start();
        var occupied = ((IPEndPoint)held.LocalEndpoint).Port;

        Assert.Null(PortConflictDetector.SuggestFallback(80, new[] { occupied }));
    }

    [Fact]
    public void ToUserMessage_NoFallback_SuggestsStopProcess()
    {
        var info = new PortConflictInfo(3306, 999, "mysqld", "0.0.0.0");
        var msg = info.ToUserMessage(suggestedFallback: null);
        Assert.Contains("Port 3306", msg);
        Assert.Contains("mysqld", msg);
        Assert.Contains("Stop the conflicting process", msg);
    }

    private static int GetFreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
