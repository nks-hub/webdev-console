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
    public void SuggestFallback_ReturnsAlternative_ForApachePort80()
    {
        // One of 8080/8000/8888 should be free on a normal dev host.
        var fallback = PortConflictDetector.SuggestFallback(80);
        Assert.NotNull(fallback);
        Assert.NotEqual(80, fallback);
        Assert.Contains(fallback!.Value, new[] { 8080, 8000, 8888 });
    }

    [Fact]
    public void SuggestFallback_ReturnsAlternative_ForMySqlPort3306()
    {
        var fallback = PortConflictDetector.SuggestFallback(3306);
        Assert.NotNull(fallback);
        Assert.Contains(fallback!.Value, new[] { 3307, 3308, 33060 });
    }

    [Fact]
    public void SuggestFallback_UsesGenericCandidates_ForUnknownPort()
    {
        // For arbitrary ports we get port+1/+10/+100
        var fallback = PortConflictDetector.SuggestFallback(40000);
        Assert.NotNull(fallback);
        Assert.Contains(fallback!.Value, new[] { 40001, 40010, 40100 });
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

    [Fact]
    public void SuggestFallback_ReturnsAlternative_ForSslPort443()
    {
        var fallback = PortConflictDetector.SuggestFallback(443);
        Assert.NotNull(fallback);
        Assert.Contains(fallback!.Value, new[] { 8443, 4443, 9443 });
    }

    [Fact]
    public void SuggestFallback_ReturnsAlternative_ForRedisPort6379()
    {
        var fallback = PortConflictDetector.SuggestFallback(6379);
        Assert.NotNull(fallback);
        Assert.Contains(fallback!.Value, new[] { 6380, 6381, 16379 });
    }

    [Fact]
    public void SuggestFallback_ReturnsAlternative_ForSmtpPort1025()
    {
        var fallback = PortConflictDetector.SuggestFallback(1025);
        Assert.NotNull(fallback);
        Assert.Contains(fallback!.Value, new[] { 1026, 2525, 25252 });
    }

    [Fact]
    public void SuggestFallback_ReturnsAlternative_ForMailpitUiPort8025()
    {
        var fallback = PortConflictDetector.SuggestFallback(8025);
        Assert.NotNull(fallback);
        Assert.Contains(fallback!.Value, new[] { 8026, 18025 });
    }

    [Fact]
    public void SuggestFallback_AcceptsCustomCandidates()
    {
        var fallback = PortConflictDetector.SuggestFallback(80, new[] { 9090, 9091 });
        Assert.NotNull(fallback);
        Assert.Contains(fallback!.Value, new[] { 9090, 9091 });
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
