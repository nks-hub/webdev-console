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

    // ── Apache parser edge cases ──────────────────────────────────────────────

    [Fact]
    public void ParseApacheLine_MicrosecondTimestamp_Parsed()
    {
        // 6-digit microsecond fraction in timestamp
        const string line = "[Tue Apr 14 15:30:45.987654 2026] [core:error] [pid 999] AH00124: some error";

        var entry = ErrorLogInspector.ParseApacheLine(line);

        Assert.NotNull(entry);
        Assert.Equal(2026, entry!.Timestamp.Year);
        Assert.Equal(4, entry.Timestamp.Month);
        Assert.Equal(14, entry.Timestamp.Day);
        Assert.Equal(15, entry.Timestamp.Hour);
        Assert.Equal(30, entry.Timestamp.Minute);
        Assert.Equal(45, entry.Timestamp.Second);
        Assert.Equal("error", entry.Severity);
    }

    [Fact]
    public void ParseApacheLine_NoSeverity_DefaultsToNotice()
    {
        // [module] without :level — rare but valid in some Apache builds
        const string line = "[Mon Apr 13 10:00:00.000000 2026] [autoindex] [pid 42] Directory listing generated";

        var entry = ErrorLogInspector.ParseApacheLine(line);

        Assert.NotNull(entry);
        Assert.Equal("notice", entry!.Severity);
        Assert.Equal("42", entry.Pid);
        Assert.Contains("Directory listing", entry.Message);
    }

    [Fact]
    public void ParseApacheLine_PidWithLeadingZeroes_ParsedCorrectly()
    {
        const string line = "[Mon Apr 13 10:00:00.000000 2026] [core:notice] [pid 0000] AH00094: startup";

        var entry = ErrorLogInspector.ParseApacheLine(line);

        Assert.NotNull(entry);
        Assert.Equal("0000", entry!.Pid);
        Assert.Equal("notice", entry.Severity);
    }

    [Fact]
    public void ParseApacheLine_ClientWithIpv6_ParsedCorrectly()
    {
        // Apache formats IPv6 clients as [::1]:port inside the [client ...] bracket
        const string line = "[Mon Apr 13 10:00:00.000000 2026] [core:error] [pid 7] [client [::1]:12345] AH01626: auth required";

        var entry = ErrorLogInspector.ParseApacheLine(line);

        Assert.NotNull(entry);
        Assert.Equal("[::1]:12345", entry!.Client);
        Assert.Equal("error", entry.Severity);
        Assert.Contains("AH01626", entry.Message);
    }

    [Fact]
    public void ParseApacheLine_EmptyMessage_ReturnsEntryWithEmptyMessage()
    {
        // A line where there is no trailing message text after the header fields
        const string line = "[Mon Apr 13 10:00:00.000000 2026] [core:notice] [pid 1] ";

        var entry = ErrorLogInspector.ParseApacheLine(line);

        Assert.NotNull(entry);
        Assert.Equal("", entry!.Message);
    }

    // ── PHP-FPM parser edge cases ─────────────────────────────────────────────

    [Fact]
    public void ParsePhpLine_StackTraceContinuation_ReturnsNull()
    {
        // Raw stack trace continuation lines (no bracketed timestamp) must be dropped
        const string stackFrame = "#0 /var/www/html/index.php(42): someFunction()";
        const string stackHeader = "Stack trace:";

        Assert.Null(ErrorLogInspector.ParsePhpLine(stackFrame));
        Assert.Null(ErrorLogInspector.ParsePhpLine(stackHeader));
    }

    [Fact]
    public void ParsePhpLine_WithPoolPrefix_ParsesAsInfo()
    {
        // PHP-FPM pool lines: "[pool www] child 1234 started" — no FPM severity keyword
        const string line = "[13-Apr-2026 10:00:05] [pool www] child 1234 started";

        var entry = ErrorLogInspector.ParsePhpLine(line);

        Assert.NotNull(entry);
        // Body starts with '[' so the short-word-before-colon heuristic doesn't apply → info
        Assert.Equal("info", entry!.Severity);
        Assert.Equal(2026, entry.Timestamp.Year);
        Assert.Equal(5, entry.Timestamp.Second);
    }

    [Fact]
    public void ParsePhpLine_UppercaseSeverity_NormalizedToLowercase()
    {
        // FPM global log uses uppercase: WARNING, ERROR — must be lowercased
        const string line = "[13-Apr-2026 10:00:06] WARNING: [pool www] child 5678 exited";

        var entry = ErrorLogInspector.ParsePhpLine(line);

        Assert.NotNull(entry);
        Assert.Equal("warning", entry!.Severity);
        Assert.Contains("child 5678", entry.Message);
    }

    [Fact]
    public void ParsePhpLine_LocalizedMonth_ReturnsNull()
    {
        // Non-English month abbreviation — TryParseExact with InvariantCulture must fail gracefully
        const string line = "[13-Avr-2026 10:00:00] PHP Warning: localized month test";

        Assert.Null(ErrorLogInspector.ParsePhpLine(line));
    }

    // ── Tail edge cases ───────────────────────────────────────────────────────

    [Fact]
    public void Tail_FileLargerThanMaxBytes_OnlyLastBytesRead()
    {
        // Build a synthetic Apache log where the "old" section is large enough
        // to be entirely above the maxTailBytes seek point.
        //
        // Layout:
        //   200 "January" lines × ~540 bytes each  ≈ 108 KB  (old section)
        //   20  "June"    lines × ~75  bytes each  ≈   1.5 KB (recent section)
        //   Total ≈ 109.5 KB
        //
        // maxTailBytes = 500 → seek point lands inside the June block (last 500
        // bytes), so only a handful of June entries are read.  The January lines
        // are entirely above the seek point and never reach the parser.
        var path = Path.Combine(_tempDir, "big-error.log");

        // Build a 480-char filler so each January line is ~540 bytes on disk.
        var filler = new string('X', 480);

        var oldLines = Enumerable.Range(0, 200)
            .Select(i => $"[Mon Jan 01 00:00:{i % 60:D2}.000000 2026] [core:notice] [pid {i}] {filler}")
            .ToArray();

        var newLines = Enumerable.Range(0, 20)
            .Select(i => $"[Mon Jun 01 12:00:{i % 60:D2}.000000 2026] [core:error] [pid {10000 + i}] Recent entry {i}")
            .ToArray();

        File.WriteAllLines(path, oldLines.Concat(newLines));

        var fileSize = new FileInfo(path).Length;
        Assert.True(fileSize > 80_000, $"File must be >80 KB for this test (actual: {fileSize})");

        // 500 bytes tail: seek lands well inside the 1.5 KB June block.
        // The partial first June line is skipped; remaining June entries parsed.
        // No January line can fit inside 500 bytes from the end.
        var entries = ErrorLogInspector.Tail(new[] { path }, "apache-error", limit: 10_000, maxTailBytes: 500);

        Assert.NotEmpty(entries);
        Assert.All(entries, e => Assert.Equal(6, e.Timestamp.Month));
    }

    [Fact]
    public void Tail_FileContainsBinaryBytes_NonCrashing()
    {
        // Write a file that mixes valid Apache log lines with raw binary / invalid UTF-8
        var path = Path.Combine(_tempDir, "binary-error.log");

        var validLine = "[Mon Apr 13 10:00:00.000000 2026] [core:error] [pid 1] Valid line before binary\n"u8;
        var binaryJunk = new byte[] { 0xFF, 0xFE, 0x00, 0x80, 0x90, 0xC0, 0x10, 0x0A }; // includes \n at end
        var validLine2 = "[Mon Apr 13 10:00:01.000000 2026] [core:notice] [pid 2] Valid line after binary\n"u8;

        using (var fs = File.OpenWrite(path))
        {
            fs.Write(validLine);
            fs.Write(binaryJunk);
            fs.Write(validLine2);
        }

        // Must not throw — the parser handles replacement chars from UTF-8 decoder
        var entries = ErrorLogInspector.Tail(new[] { path }, "apache-error");

        // At least the two clean lines should survive
        Assert.True(entries.Count >= 2, $"Expected ≥2 valid entries, got {entries.Count}");
        Assert.All(entries, e => Assert.Equal(2026, e.Timestamp.Year));
    }

    [Fact]
    public void TailMultiple_WithDuplicateEntries_ReturnsAllNoDedup()
    {
        // Two source files with identical content — both copies must appear
        // (ErrorLogInspector does not deduplicate; confirmed contract)
        var path1 = Path.Combine(_tempDir, "dup-error-1.log");
        var path2 = Path.Combine(_tempDir, "dup-error-2.log");

        var lines = new[]
        {
            "[Mon Apr 13 10:00:00.000000 2026] [core:error] [pid 1] Identical error message",
            "[Mon Apr 13 10:00:01.000000 2026] [core:warn] [pid 2] Identical warning message",
        };

        File.WriteAllLines(path1, lines);
        File.WriteAllLines(path2, lines);

        var entries = ErrorLogInspector.TailMultiple(
        [
            (new[] { path1 }, "apache-error"),
            (new[] { path2 }, "apache-error"),
        ]);

        // 2 files × 2 lines = 4 entries, no deduplication
        Assert.Equal(4, entries.Count);
        Assert.Equal(2, entries.Count(e => e.Timestamp.Second == 1));
        Assert.Equal(2, entries.Count(e => e.Timestamp.Second == 0));
    }
}
