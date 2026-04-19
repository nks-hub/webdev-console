using NKS.WebDevConsole.Core.Services;

namespace NKS.WebDevConsole.Daemon.Tests;

// Local mirror of the AccessEntry record defined in Program.cs (top-level).
// Kept internal so this test assembly compiles without referencing the
// top-level program statement directly.
internal record AccessEntry(
    DateTimeOffset Timestamp,
    string RemoteIp,
    string? Method,
    string? Path,
    string? Protocol,
    int Status,
    long Bytes,
    string? Referer,
    string? UserAgent);

/// <summary>
/// Tests for the GET /api/sites/{domain}/logs/access endpoint logic.
/// Exercises AccessLogInspector.Tail + the mapping/filtering pipeline
/// used by the endpoint without spinning up a full HTTP host.
/// </summary>
public sealed class AccessLogApiTests : IDisposable
{
    private readonly string _tempDir;

    // Five synthetic Combined Log Format lines spanning different timestamps.
    private static readonly string[] SyntheticLines =
    [
        "10.0.0.1 - - [19/Apr/2026:08:00:00 +0000] \"GET /index.php HTTP/1.1\" 200 1024 \"-\" \"Mozilla/5.0\"",
        "10.0.0.2 - - [19/Apr/2026:09:00:00 +0000] \"POST /api/login HTTP/1.1\" 302 0 \"https://example.com/\" \"curl/8.0\"",
        "10.0.0.3 - - [19/Apr/2026:10:00:00 +0000] \"GET /style.css HTTP/1.1\" 304 0 \"-\" \"Mozilla/5.0\"",
        "10.0.0.4 - - [19/Apr/2026:11:00:00 +0000] \"GET /missing.html HTTP/1.1\" 404 512 \"-\" \"Googlebot/2.1\"",
        "10.0.0.5 - - [19/Apr/2026:12:00:00 +0000] \"DELETE /api/item/9 HTTP/1.1\" 204 0 \"-\" \"TestAgent/1.0\"",
    ];

    public AccessLogApiTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"nks-access-api-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }

    private string WriteLogFile(IEnumerable<string> lines)
    {
        var path = Path.Combine(_tempDir, $"access-{Guid.NewGuid():N}.log");
        File.WriteAllLines(path, lines);
        return path;
    }

    /// <summary>
    /// Mirrors the endpoint's map-and-filter pipeline so all tests share
    /// one authoritative translation of AccessLogEntry → AccessEntry.
    /// </summary>
    private static List<AccessEntry> RunPipeline(
        IEnumerable<string> candidatePaths,
        int limit = 100,
        DateTimeOffset? since = null)
    {
        var raw = AccessLogInspector.Tail(candidatePaths, limit, 512 * 1024);

        return raw
            .Select(e => new AccessEntry(
                Timestamp: new DateTimeOffset(e.TimestampUtc, TimeSpan.Zero),
                RemoteIp: e.RemoteAddr,
                Method: string.IsNullOrEmpty(e.Method) ? null : e.Method,
                Path: string.IsNullOrEmpty(e.Path) ? null : e.Path,
                Protocol: string.IsNullOrEmpty(e.Protocol) ? null : e.Protocol,
                Status: e.Status,
                Bytes: e.ResponseBytes,
                Referer: string.IsNullOrEmpty(e.Referer) ? null : e.Referer,
                UserAgent: string.IsNullOrEmpty(e.UserAgent) ? null : e.UserAgent))
            .Where(e => since is null || e.Timestamp >= since)
            .Reverse()
            .ToList();
    }

    [Fact]
    public void Returns_AllFiveEntries_WhenNoFilter()
    {
        var path = WriteLogFile(SyntheticLines);
        var entries = RunPipeline([path]);

        Assert.Equal(5, entries.Count);
    }

    [Fact]
    public void Entries_AreNewestFirst()
    {
        var path = WriteLogFile(SyntheticLines);
        var entries = RunPipeline([path]);

        Assert.Equal(new DateTimeOffset(2026, 4, 19, 12, 0, 0, TimeSpan.Zero), entries[0].Timestamp);
        Assert.Equal(new DateTimeOffset(2026, 4, 19, 8, 0, 0, TimeSpan.Zero), entries[4].Timestamp);
    }

    [Fact]
    public void Returns_EmptyList_WhenLogFileDoesNotExist()
    {
        var missing = Path.Combine(_tempDir, "nonexistent-access.log");
        var entries = RunPipeline([missing]);

        Assert.Empty(entries);
    }

    [Fact]
    public void Since_Filter_ExcludesOlderEntries()
    {
        var path = WriteLogFile(SyntheticLines);
        var since = new DateTimeOffset(2026, 4, 19, 11, 0, 0, TimeSpan.Zero);
        var entries = RunPipeline([path], since: since);

        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.True(e.Timestamp >= since));
    }

    [Fact]
    public void Lines_Cap_IsRespected()
    {
        var path = WriteLogFile(SyntheticLines);
        var entries = RunPipeline([path], limit: 3);

        Assert.Equal(3, entries.Count);
        // Newest first: 12:00, 11:00, 10:00
        Assert.Equal(new DateTimeOffset(2026, 4, 19, 12, 0, 0, TimeSpan.Zero), entries[0].Timestamp);
        Assert.Equal(new DateTimeOffset(2026, 4, 19, 10, 0, 0, TimeSpan.Zero), entries[2].Timestamp);
    }

    [Fact]
    public void ParseLine_MapsFieldsCorrectly()
    {
        var entry = AccessLogInspector.ParseLine(SyntheticLines[0]);

        Assert.NotNull(entry);
        Assert.Equal("10.0.0.1", entry.RemoteAddr);
        Assert.Equal("GET", entry.Method);
        Assert.Equal("/index.php", entry.Path);
        Assert.Equal(200, entry.Status);
        Assert.Equal(1024L, entry.ResponseBytes);
    }
}
