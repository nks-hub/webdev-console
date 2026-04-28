using Microsoft.Extensions.Logging.Abstractions;
using NKS.WebDevConsole.Daemon.Mcp;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Unit tests for the orphan-snapshot file sweep added in Phase 6.5b.
/// Targets the pure <c>SweepOrphanSnapshotFiles</c> static helper so
/// each test seeds a temp <c>pre-deploy</c> directory + the
/// referenced-paths set + a synthetic "now" timestamp without spinning
/// the full <see cref="IntentSweeperService"/> BackgroundService.
/// </summary>
public sealed class IntentSweeperOrphanSnapshotsTests : IDisposable
{
    private readonly string _dir;

    public IntentSweeperOrphanSnapshotsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"nks-orphan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string WriteSnapshot(string deployId, DateTime lastWriteUtc)
    {
        var path = Path.Combine(_dir, $"{deployId}.sql.gz");
        File.WriteAllText(path, $"-- snapshot for {deployId}\n");
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    private static int Sweep(string dir, TimeSpan retention, HashSet<string> refs, DateTime now) =>
        IntentSweeperService.SweepOrphanSnapshotFiles(
            dir, retention, refs, now, NullLogger.Instance);

    [Fact]
    public void Sweep_DoesNothing_WhenDirectoryMissing()
    {
        var phantom = Path.Combine(Path.GetTempPath(), $"nks-orphan-missing-{Guid.NewGuid():N}");
        var deleted = Sweep(phantom, TimeSpan.FromDays(30), new(), DateTime.UtcNow);
        Assert.Equal(0, deleted);
    }

    [Fact]
    public void Sweep_DeletesNothing_WhenAllFilesYoungerThanRetention()
    {
        var young = WriteSnapshot("y1", DateTime.UtcNow.AddDays(-1));
        var deleted = Sweep(_dir, TimeSpan.FromDays(30), new(), DateTime.UtcNow);
        Assert.Equal(0, deleted);
        Assert.True(File.Exists(young));
    }

    [Fact]
    public void Sweep_DeletesOldUnreferencedFile()
    {
        var orphan = WriteSnapshot("o1", DateTime.UtcNow.AddDays(-31));
        var deleted = Sweep(_dir, TimeSpan.FromDays(30), new(), DateTime.UtcNow);
        Assert.Equal(1, deleted);
        Assert.False(File.Exists(orphan));
    }

    [Fact]
    public void Sweep_KeepsOldFile_WhenStillReferencedByDeployRunsRow()
    {
        var stillUsed = WriteSnapshot("u1", DateTime.UtcNow.AddDays(-31));
        var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { stillUsed };
        var deleted = Sweep(_dir, TimeSpan.FromDays(30), refs, DateTime.UtcNow);
        Assert.Equal(0, deleted);
        Assert.True(File.Exists(stillUsed));
    }

    [Fact]
    public void Sweep_DeletesOnlyTheOrphans_InMixedDirectory()
    {
        var young = WriteSnapshot("young", DateTime.UtcNow.AddDays(-5));
        var oldRef = WriteSnapshot("old-ref", DateTime.UtcNow.AddDays(-60));
        var oldOrphan1 = WriteSnapshot("o1", DateTime.UtcNow.AddDays(-45));
        var oldOrphan2 = WriteSnapshot("o2", DateTime.UtcNow.AddDays(-90));
        var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { oldRef };

        var deleted = Sweep(_dir, TimeSpan.FromDays(30), refs, DateTime.UtcNow);

        Assert.Equal(2, deleted);
        Assert.True(File.Exists(young));
        Assert.True(File.Exists(oldRef));
        Assert.False(File.Exists(oldOrphan1));
        Assert.False(File.Exists(oldOrphan2));
    }

    [Fact]
    public void Sweep_HonoursReferencedPathCaseInsensitively()
    {
        // The HashSet uses OrdinalIgnoreCase — tests that even if the DB
        // stored a different case path than what's on disk, we treat it
        // as referenced (Windows file systems are case-insensitive).
        var path = WriteSnapshot("u1", DateTime.UtcNow.AddDays(-31));
        var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { path.ToUpperInvariant() };
        var deleted = Sweep(_dir, TimeSpan.FromDays(30), refs, DateTime.UtcNow);
        Assert.Equal(0, deleted);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Sweep_IgnoresFilesWithoutSqlGzExtension()
    {
        // Just .gz, .bak, .sql alone — none match the *.sql.gz glob.
        var notArchive = Path.Combine(_dir, "stray.bak");
        File.WriteAllText(notArchive, "x");
        File.SetLastWriteTimeUtc(notArchive, DateTime.UtcNow.AddDays(-90));

        var deleted = Sweep(_dir, TimeSpan.FromDays(30), new(), DateTime.UtcNow);
        Assert.Equal(0, deleted);
        Assert.True(File.Exists(notArchive));
    }

    [Fact]
    public void Sweep_RetentionBoundary_KeepsFileExactlyAtCutoff()
    {
        // File written EXACTLY at the cutoff timestamp — comparison is
        // `< cutoff` so equal-to is kept (defensive — preserves anything
        // borderline rather than racing the second hand).
        var now = DateTime.UtcNow;
        var atCutoff = WriteSnapshot("edge", now.AddDays(-30));
        var deleted = Sweep(_dir, TimeSpan.FromDays(30), new(), now);
        Assert.Equal(0, deleted);
        Assert.True(File.Exists(atCutoff));
    }
}
