using NKS.WebDevConsole.Core.Services;

namespace NKS.WebDevConsole.Core.Tests;

/// <summary>
/// Protects the Phase 11 Performance monitoring foothold — the cheap
/// access log metadata inspector used by the daemon's
/// <c>/api/sites/{domain}/metrics</c> endpoint.
/// </summary>
public sealed class AccessLogInspectorTests : IDisposable
{
    private readonly string _tempDir;

    public AccessLogInspectorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"nks-wdc-access-log-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public void Inspect_ReturnsNull_WhenNoCandidates()
    {
        Assert.Null(AccessLogInspector.Inspect(Array.Empty<string>()));
    }

    [Fact]
    public void Inspect_ReturnsNull_WhenAllCandidatesMissing()
    {
        var paths = new[]
        {
            Path.Combine(_tempDir, "a-access.log"),
            Path.Combine(_tempDir, "b-access.log"),
        };
        Assert.Null(AccessLogInspector.Inspect(paths));
    }

    [Fact]
    public void Inspect_SkipsEmptyAndNullCandidates()
    {
        var real = Path.Combine(_tempDir, "real.log");
        File.WriteAllText(real, "line1\nline2\n");

        var stats = AccessLogInspector.Inspect(new[] { "", " ", null!, real });
        Assert.NotNull(stats);
        Assert.Equal(real, stats!.Path);
    }

    [Fact]
    public void Inspect_CountsLinesExactlyForSmallFile()
    {
        var path = Path.Combine(_tempDir, "small.log");
        File.WriteAllLines(path, new[]
        {
            "127.0.0.1 - - [13/Apr/2026:10:00:01 +0200] \"GET / HTTP/1.1\" 200 42",
            "127.0.0.1 - - [13/Apr/2026:10:00:02 +0200] \"GET /api HTTP/1.1\" 200 13",
            "127.0.0.1 - - [13/Apr/2026:10:00:03 +0200] \"POST /login HTTP/1.1\" 302 0",
        });

        var stats = AccessLogInspector.Inspect(new[] { path });
        Assert.NotNull(stats);
        Assert.Equal(3, stats!.LineCount);
        Assert.True(stats.SizeBytes > 0);
        Assert.Equal(path, stats.Path);
    }

    [Fact]
    public void Inspect_ReturnsFirstExistingCandidate()
    {
        var a = Path.Combine(_tempDir, "a.log");
        var b = Path.Combine(_tempDir, "b.log");
        File.WriteAllText(a, "A\n");
        File.WriteAllText(b, "B\nC\n");

        var stats = AccessLogInspector.Inspect(new[] { a, b });
        Assert.NotNull(stats);
        // `a` exists, so it wins — b is not even looked at.
        Assert.Equal(a, stats!.Path);
        Assert.Equal(1, stats.LineCount);
    }

    [Fact]
    public void Inspect_HandlesEmptyFile()
    {
        var path = Path.Combine(_tempDir, "empty.log");
        File.WriteAllText(path, "");

        var stats = AccessLogInspector.Inspect(new[] { path });
        Assert.NotNull(stats);
        Assert.Equal(0, stats!.LineCount);
        Assert.Equal(0, stats.SizeBytes);
    }

    [Fact]
    public void Inspect_SingleLineWithoutNewline()
    {
        var path = Path.Combine(_tempDir, "noeol.log");
        File.WriteAllText(path, "127.0.0.1 - - [13/Apr/2026] \"GET / HTTP/1.1\" 200 42");

        var stats = AccessLogInspector.Inspect(new[] { path });
        Assert.NotNull(stats);
        // No \n in file → line count is 0 (counts newline chars, not logical lines).
        // This is by design: Apache always writes \n at the end of each log line,
        // so a file without \n means it was truncated or is being written.
        Assert.Equal(0, stats!.LineCount);
        Assert.True(stats.SizeBytes > 0);
    }

    [Fact]
    public void Inspect_ReportsLastWriteUtc()
    {
        var path = Path.Combine(_tempDir, "timed.log");
        File.WriteAllText(path, "line1\n");

        var stats = AccessLogInspector.Inspect(new[] { path });
        Assert.NotNull(stats);
        Assert.True((DateTime.UtcNow - stats!.LastWrittenUtc).TotalMinutes < 1);
    }

    [Fact]
    public void Inspect_ExtrapolatesWhenScanCapExceeded()
    {
        // Write a file larger than the scan cap and verify line count is
        // extrapolated rather than an exact count. The cap is 64 bytes
        // here for a fast test; the production default is 10 MB.
        var path = Path.Combine(_tempDir, "big.log");
        var lines = Enumerable.Range(0, 200).Select(i => $"line {i}").ToArray();
        File.WriteAllLines(path, lines);

        var stats = AccessLogInspector.Inspect(new[] { path }, maxLineScanBytes: 64);
        Assert.NotNull(stats);
        // Extrapolated count should be in the ballpark of the real 200;
        // allow generous margin since tail-half distribution can skew.
        Assert.InRange(stats!.LineCount, 100, 400);
    }

    [Fact]
    public void Inspect_FileLockedByWriter_DoesNotThrow()
    {
        var path = Path.Combine(_tempDir, "locked.log");
        File.WriteAllText(path, "line1\nline2\n");

        // Open file with FileShare.ReadWrite to simulate Apache holding a write handle
        using var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

        var stats = AccessLogInspector.Inspect(new[] { path });
        Assert.NotNull(stats);
        Assert.Equal(2, stats!.LineCount);
    }

    [Fact]
    public void Inspect_CrlfLineEndings_CountsCorrectly()
    {
        var path = Path.Combine(_tempDir, "crlf.log");
        File.WriteAllText(path, "line1\r\nline2\r\nline3\r\n");

        var stats = AccessLogInspector.Inspect(new[] { path });
        Assert.NotNull(stats);
        Assert.Equal(3, stats!.LineCount);
    }

    [Fact]
    public void Inspect_ZeroScanCap_DoesNotCrash()
    {
        // Edge case: caller passes 0 as max scan cap — should not hang or crash.
        // With scanLimit=0, no bytes are scanned, lineCount=0.
        var path = Path.Combine(_tempDir, "zero-cap.log");
        File.WriteAllText(path, "line1\nline2\nline3\n");

        var stats = AccessLogInspector.Inspect(new[] { path }, maxLineScanBytes: 0);
        Assert.NotNull(stats);
        // With scanLimit=0 and fileSize>0, extrapolation is skipped (scanned=0)
        Assert.Equal(0, stats!.LineCount);
        Assert.True(stats.SizeBytes > 0);
    }

    [Fact]
    public void Inspect_AllCandidatesWhitespace_ReturnsNull()
    {
        var stats = AccessLogInspector.Inspect(new[] { "  ", "\t", "" });
        Assert.Null(stats);
    }

    // ── ParseLine: XFF / CF-Connecting-IP field tests ────────────────────────

    [Fact]
    public void ParseLine_ClassicCombined_NoXffFields_ParsesSuccessfully()
    {
        // Classic combined format — no trailing XFF/CF-IP fields.
        // Both optional fields must be null and EffectiveClientIp falls back to socket IP.
        const string line = "203.0.113.5 - - [19/Apr/2026:10:00:00 +0000] \"GET / HTTP/1.1\" 200 1024 \"-\" \"Mozilla/5.0\"";

        var entry = AccessLogInspector.ParseLine(line);

        Assert.NotNull(entry);
        Assert.Equal("203.0.113.5", entry!.RemoteAddr);
        Assert.Null(entry.XForwardedFor);
        Assert.Null(entry.CfConnectingIp);
        Assert.Equal("203.0.113.5", entry.EffectiveClientIp);
    }

    [Fact]
    public void ParseLine_WdcCombined_WithCfIp_ReturnsCfIpAsEffective()
    {
        // wdc-combined format — both XFF and CF-IP are present.
        // EffectiveClientIp must prefer CF-Connecting-IP.
        const string line = "::1 - - [19/Apr/2026:10:00:00 +0000] \"GET /api HTTP/1.1\" 200 512 \"-\" \"curl/8.0\" \"1.2.3.4\" \"1.2.3.4\"";

        var entry = AccessLogInspector.ParseLine(line);

        Assert.NotNull(entry);
        Assert.Equal("::1", entry!.RemoteAddr);
        Assert.Equal("1.2.3.4", entry.XForwardedFor);
        Assert.Equal("1.2.3.4", entry.CfConnectingIp);
        Assert.Equal("1.2.3.4", entry.EffectiveClientIp);
    }

    [Fact]
    public void ParseLine_WdcCombined_XffMultipleIps_ReturnsFirstIp()
    {
        // XFF contains a comma-list; CF-IP is absent (dash).
        // EffectiveClientIp must take the leftmost (original client) IP from XFF.
        const string line = "127.0.0.1 - - [19/Apr/2026:10:00:00 +0000] \"GET / HTTP/1.1\" 200 0 \"-\" \"-\" \"10.0.0.1, 192.168.1.1\" \"-\"";

        var entry = AccessLogInspector.ParseLine(line);

        Assert.NotNull(entry);
        Assert.Equal("10.0.0.1, 192.168.1.1", entry!.XForwardedFor);
        Assert.Null(entry.CfConnectingIp);
        Assert.Equal("10.0.0.1", entry.EffectiveClientIp);
    }

    [Fact]
    public void ParseLine_WdcCombined_BothDash_FallsBackToSocketIp()
    {
        // Both XFF and CF-IP are "-" (tunnel not active / direct connection).
        // EffectiveClientIp must fall back to socket RemoteAddr.
        const string line = "192.168.0.1 - - [19/Apr/2026:10:00:00 +0000] \"GET / HTTP/1.1\" 200 0 \"-\" \"Agent\" \"-\" \"-\"";

        var entry = AccessLogInspector.ParseLine(line);

        Assert.NotNull(entry);
        Assert.Null(entry!.XForwardedFor);
        Assert.Null(entry.CfConnectingIp);
        Assert.Equal("192.168.0.1", entry.EffectiveClientIp);
    }
}
