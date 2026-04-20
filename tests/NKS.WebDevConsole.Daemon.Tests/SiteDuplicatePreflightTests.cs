using NKS.WebDevConsole.Daemon.Sites;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// F65 preflight: verifies the pure path-length helper without having
/// to spin up the /api/sites/{domain}/duplicate endpoint. Each test
/// supplies a fake enumerator so the assertions are deterministic and
/// the test doesn't touch the filesystem.
/// </summary>
public sealed class SiteDuplicatePreflightTests
{
    [Fact]
    public void FindPathTooLong_NoEntries_ReturnsNull()
    {
        var result = SiteDuplicatePreflight.FindPathTooLong(
            sourceRoot: "C:/src",
            newRoot: "C:/dst",
            entryEnumerator: Array.Empty<string>());
        Assert.Null(result);
    }

    [Fact]
    public void FindPathTooLong_ShortestPath_ReturnsNull()
    {
        var entries = new[] { "C:/src/index.html" };
        var result = SiteDuplicatePreflight.FindPathTooLong("C:/src", "C:/dst-site", entries);
        Assert.Null(result);
    }

    [Fact]
    public void FindPathTooLong_PrefixDeltaPushesPathOverLimit_ReturnsOffender()
    {
        // "C:/src/" = 7 chars + 245 x's = 252-char source path.
        // prefixDelta = len("C:/very-long-destination") - len("C:/src") = 24 - 6 = 18.
        // 252 + 18 = 270 > 259 → must surface as offender.
        var longName = new string('x', 245);
        var sourcePath = $"C:/src/{longName}";
        var entries = new[] { sourcePath };

        var result = SiteDuplicatePreflight.FindPathTooLong(
            sourceRoot: "C:/src",
            newRoot: "C:/very-long-destination",
            entryEnumerator: entries);

        Assert.Equal(sourcePath, result);
    }

    [Fact]
    public void FindPathTooLong_SmallerTargetPrefix_AllowsExistingLongPaths()
    {
        // Source already has a near-cap path but the new root is SHORTER,
        // so projected target fits — scan must not flag it.
        var longName = new string('x', 240);
        var sourcePath = $"C:/very-long-source-dir/{longName}"; // length ~ 264
        var entries = new[] { sourcePath };

        // New root is shorter by 14 chars → target length ~ 250, under the cap.
        var result = SiteDuplicatePreflight.FindPathTooLong(
            sourceRoot: "C:/very-long-source-dir",
            newRoot: "C:/short",
            entryEnumerator: entries);

        Assert.Null(result);
    }

    [Fact]
    public void FindPathTooLong_ReturnsLongestOffendingEntry()
    {
        // Mix of short + long entries — the longest should surface as
        // the offender even if earlier entries already exceed the cap.
        var longA = "C:/src/" + new string('a', 250);
        var longB = "C:/src/" + new string('b', 260);
        var entries = new[] { longA, "C:/src/short.txt", longB };

        var result = SiteDuplicatePreflight.FindPathTooLong(
            "C:/src", "C:/longer-dst", entries);

        Assert.Equal(longB, result);
    }

    [Theory]
    [InlineData("all", true)]
    [InlineData("top", true)]
    [InlineData("empty", false)]
    public void ShouldPreflight_SkipsEmptyCopy(string copyFiles, bool expectedOnWindows)
    {
        var actual = SiteDuplicatePreflight.ShouldPreflight(copyFiles);
        if (OperatingSystem.IsWindows())
            Assert.Equal(expectedOnWindows, actual);
        else
            Assert.False(actual); // non-Windows always false regardless
    }
}
