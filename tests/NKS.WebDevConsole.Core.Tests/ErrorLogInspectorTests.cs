using NKS.WebDevConsole.Core.Services;

namespace NKS.WebDevConsole.Core.Tests;

/// <summary>
/// Tests for <see cref="ErrorLogInspector"/> — Apache error log and PHP-FPM
/// error log parsing, file discovery, and the since-filter.
/// </summary>
public sealed class ErrorLogInspectorTests : IDisposable
{
    private readonly string _tempDir;

    public ErrorLogInspectorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"nks-wdc-error-log-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }

    // ── Apache parser ─────────────────────────────────────────────────────────

    [Fact]
    public void ParseApacheLine_ValidLine_ParsesAllFields()
    {
        const string line = "[Sun Apr 13 10:00:00.123456 2026] [core:error] [pid 1234] [client 127.0.0.1:54321] AH00124: Request exceeded the limit";

        var entry = ErrorLogInspector.ParseApacheLine(line);

        Assert.NotNull(entry);
        Assert.Equal("error", entry!.Severity);
        Assert.Equal("apache-error", entry.Source);
        Assert.Equal("1234", entry.Pid);
        Assert.Equal("127.0.0.1:54321", entry.Client);
        Assert.Contains("AH00124", entry.Message);
        Assert.Equal(2026, entry.Timestamp.Year);
        Assert.Equal(4, entry.Timestamp.Month);
        Assert.Equal(13, entry.Timestamp.Day);
    }

    [Fact]
    public void ParseApacheLine_NoClient_ParsesWithNullClient()
    {
        const string line = "[Mon Apr 13 10:00:00.000000 2026] [mpm_winnt:notice] [pid 4] AH00455: Apache lounge started.";

        var entry = ErrorLogInspector.ParseApacheLine(line);

        Assert.NotNull(entry);
        Assert.Equal("notice", entry!.Severity);
        Assert.Null(entry.Client);
        Assert.Equal("4", entry.Pid);
    }

    [Fact]
    public void ParseApacheLine_InvalidLine_ReturnsNull()
    {
        Assert.Null(ErrorLogInspector.ParseApacheLine("This is not an apache error log line"));
        Assert.Null(ErrorLogInspector.ParseApacheLine(""));
        Assert.Null(ErrorLogInspector.ParseApacheLine("   "));
    }

    [Fact]
    public void ParseApacheLine_MultiLineContinuation_ReturnsNull()
    {
        // Apache multi-line stack trace lines (after the initial error line)
        // don't match the header format — they are deliberately dropped.
        const string continuationLine = "referer: http://example.com/page";
        Assert.Null(ErrorLogInspector.ParseApacheLine(continuationLine));
    }

    // ── PHP parser ────────────────────────────────────────────────────────────

    [Fact]
    public void ParsePhpLine_FatalError_ParsesCorrectly()
    {
        const string line = "[13-Apr-2026 10:00:00] PHP Fatal error: Uncaught RuntimeException in /var/www/app.php:42";

        var entry = ErrorLogInspector.ParsePhpLine(line);

        Assert.NotNull(entry);
        Assert.Equal("fatal", entry!.Severity);
        Assert.Equal("php-error", entry.Source);
        Assert.Contains("Uncaught RuntimeException", entry.Message);
        Assert.Equal(2026, entry.Timestamp.Year);
    }

    [Fact]
    public void ParsePhpLine_PhpWarning_ParsesCorrectly()
    {
        const string line = "[13-Apr-2026 10:00:01] PHP Warning: Division by zero in /app/calc.php on line 5";

        var entry = ErrorLogInspector.ParsePhpLine(line);

        Assert.NotNull(entry);
        Assert.Equal("warning", entry!.Severity);
        Assert.Contains("Division by zero", entry.Message);
    }

    [Fact]
    public void ParsePhpLine_FpmNotice_ParsesCorrectly()
    {
        const string line = "[13-Apr-2026 10:00:02] NOTICE: fpm is running, pid 1234";

        var entry = ErrorLogInspector.ParsePhpLine(line);

        Assert.NotNull(entry);
        Assert.Equal("notice", entry!.Severity);
        Assert.Contains("fpm is running", entry.Message);
    }

    [Fact]
    public void ParsePhpLine_FpmError_ParsesCorrectly()
    {
        const string line = "[13-Apr-2026 10:00:03] ERROR: unable to bind listening socket for address '127.0.0.1:9084'";

        var entry = ErrorLogInspector.ParsePhpLine(line);

        Assert.NotNull(entry);
        Assert.Equal("error", entry!.Severity);
        Assert.Contains("unable to bind", entry.Message);
    }

    [Fact]
    public void ParsePhpLine_WithTimezone_ParsesTimestamp()
    {
        // Some PHP configs append timezone abbreviation: "[13-Apr-2026 10:00:00 UTC]"
        const string line = "[13-Apr-2026 10:00:00 UTC] PHP Notice: session_start(): ...";

        var entry = ErrorLogInspector.ParsePhpLine(line);

        Assert.NotNull(entry);
        Assert.Equal("notice", entry!.Severity);
        Assert.Equal(2026, entry.Timestamp.Year);
    }

    [Fact]
    public void ParsePhpLine_InvalidLine_ReturnsNull()
    {
        Assert.Null(ErrorLogInspector.ParsePhpLine("No timestamp prefix here"));
        Assert.Null(ErrorLogInspector.ParsePhpLine(""));
    }

    // ── File discovery + Tail ─────────────────────────────────────────────────

    [Fact]
    public void Tail_MissingFile_ReturnsEmpty()
    {
        var missing = Path.Combine(_tempDir, "no-such-error.log");
        var result = ErrorLogInspector.Tail(new[] { missing }, "apache-error");
        Assert.Empty(result);
    }

    [Fact]
    public void Tail_EmptyCandidates_ReturnsEmpty()
    {
        var result = ErrorLogInspector.Tail(Array.Empty<string>(), "apache-error");
        Assert.Empty(result);
    }

    [Fact]
    public void Tail_ValidApacheLog_ParsesAndReturnsNewestFirst()
    {
        var path = Path.Combine(_tempDir, "error.log");
        File.WriteAllLines(path, new[]
        {
            "[Sun Apr 13 10:00:00.000000 2026] [core:notice] [pid 1] AH00094: Command line: 'httpd.exe'",
            "[Sun Apr 13 10:00:01.000000 2026] [core:error] [pid 2] [client 127.0.0.1:1234] AH00124: Request exceeded limit",
            "[Sun Apr 13 10:00:02.000000 2026] [mpm_winnt:warn] [pid 3] AH00452: Apache stopping...",
        });

        var entries = ErrorLogInspector.Tail(new[] { path }, "apache-error");

        Assert.Equal(3, entries.Count);
        // Newest first
        Assert.Equal(2026, entries[0].Timestamp.Year);
        Assert.Equal(2, entries[0].Timestamp.Second); // 10:00:02 is newest
        Assert.Equal(0, entries[2].Timestamp.Second); // 10:00:00 is oldest
    }

    [Fact]
    public void Tail_ValidPhpLog_ParsesAndReturnsNewestFirst()
    {
        var path = Path.Combine(_tempDir, "php84-errors.log");
        File.WriteAllLines(path, new[]
        {
            "[13-Apr-2026 10:00:00] PHP Notice: Undefined variable $x in /app/index.php on line 1",
            "[13-Apr-2026 10:00:01] PHP Warning: Division by zero in /app/calc.php on line 5",
            "[13-Apr-2026 10:00:02] PHP Fatal error: Uncaught Exception in /app/run.php:10",
        });

        var entries = ErrorLogInspector.Tail(new[] { path }, "php-error");

        Assert.Equal(3, entries.Count);
        Assert.Equal("fatal", entries[0].Severity);   // newest first
        Assert.Equal("notice", entries[2].Severity);  // oldest last
    }

    // ── Since filter ──────────────────────────────────────────────────────────

    [Fact]
    public void Tail_SinceFilter_ExcludesOlderEntries()
    {
        var path = Path.Combine(_tempDir, "since-test.log");
        File.WriteAllLines(path, new[]
        {
            "[Sun Apr 13 09:00:00.000000 2026] [core:notice] [pid 1] Old entry — before cutoff",
            "[Sun Apr 13 10:00:00.000000 2026] [core:error] [pid 2] Exactly at cutoff",
            "[Sun Apr 13 11:00:00.000000 2026] [core:warn] [pid 3] After cutoff",
        });

        var cutoff = new DateTimeOffset(2026, 4, 13, 10, 0, 0, TimeSpan.Zero);
        var entries = ErrorLogInspector.Tail(new[] { path }, "apache-error", since: cutoff);

        // Entries at or after the cutoff: 10:00:00 and 11:00:00
        Assert.Equal(2, entries.Count);
        Assert.DoesNotContain(entries, e => e.Timestamp.Hour == 9);
    }

    [Fact]
    public void TailMultiple_MergesAndSortsNewestFirst()
    {
        var apachePath = Path.Combine(_tempDir, "apache-error.log");
        var phpPath = Path.Combine(_tempDir, "php84-fpm-error.log");

        File.WriteAllLines(apachePath, new[]
        {
            "[Sun Apr 13 10:00:00.000000 2026] [core:error] [pid 1] Apache error at T0",
            "[Sun Apr 13 10:00:02.000000 2026] [core:notice] [pid 2] Apache notice at T2",
        });
        File.WriteAllLines(phpPath, new[]
        {
            "[13-Apr-2026 10:00:01] NOTICE: PHP-FPM notice at T1",
            "[13-Apr-2026 10:00:03] ERROR: PHP-FPM error at T3",
        });

        var entries = ErrorLogInspector.TailMultiple(
        [
            (new[] { apachePath }, "apache-error"),
            (new[] { phpPath }, "php-fpm-error"),
        ]);

        // Should be sorted newest-first: T3, T2, T1, T0
        Assert.Equal(4, entries.Count);
        Assert.Equal(3, entries[0].Timestamp.Second);
        Assert.Equal(2, entries[1].Timestamp.Second);
        Assert.Equal(1, entries[2].Timestamp.Second);
        Assert.Equal(0, entries[3].Timestamp.Second);
        Assert.Equal("apache-error", entries[1].Source);
        Assert.Equal("php-fpm-error", entries[0].Source);
    }
}
