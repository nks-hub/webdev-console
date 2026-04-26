using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Daemon.Deploy;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Unit tests for <see cref="SnapshotRestorer"/>. Mocks both
/// <see cref="ISiteRegistry"/> and <see cref="IDeployRunsRepository"/> so
/// every test stays hermetic — no real DB or file system layout outside
/// per-test temp dirs.
///
/// Coverage focuses on the gating + sniffing layer (the daemon-side
/// invariants); the actual mysql/psql client subprocesses are NOT exercised
/// here — those need real DB instances and live in integration tests.
/// SQLite mode IS exercised end-to-end because it has no external
/// dependency.
/// </summary>
public sealed class SnapshotRestorerTests : IDisposable
{
    private readonly List<string> _tempDirs = new();
    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            try { File.Delete(f); } catch { }
        foreach (var d in _tempDirs)
            try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
    }

    private string MakeTempDir(string label = "site")
    {
        var dir = Path.Combine(Path.GetTempPath(), $"nks-restore-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private string TempArchive() => RegisterTemp(
        Path.Combine(Path.GetTempPath(), $"nks-restore-archive-{Guid.NewGuid():N}.sql.gz"));

    private string RegisterTemp(string path)
    {
        _tempFiles.Add(path);
        return path;
    }

    private static SnapshotRestorer MakeRestorer(ISiteRegistry sites, IDeployRunsRepository runs) =>
        new(sites, runs, NullLogger<SnapshotRestorer>.Instance);

    private static Mock<ISiteRegistry> RegistryWith(string domain, SiteConfig? config)
    {
        var mock = new Mock<ISiteRegistry>();
        mock.Setup(r => r.GetSite(domain)).Returns(config);
        return mock;
    }

    private static Mock<IDeployRunsRepository> RunsWith(string deployId, DeployRunRow? row)
    {
        var mock = new Mock<IDeployRunsRepository>();
        mock.Setup(r => r.GetByIdAsync(deployId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(row);
        return mock;
    }

    private static DeployRunRow MakeRow(string deployId, string domain, string? backupPath)
    {
        var now = DateTimeOffset.UtcNow;
        return new DeployRunRow(
            Id: deployId,
            Domain: domain,
            Host: "production",
            ReleaseId: null,
            Branch: null,
            CommitSha: null,
            Status: "completed",
            IsPastPonr: true,
            StartedAt: now,
            CompletedAt: now,
            ExitCode: 0,
            ErrorMessage: null,
            DurationMs: 1000,
            TriggeredBy: "gui",
            BackendId: "nks-deploy",
            CreatedAt: now,
            UpdatedAt: now,
            PreDeployBackupPath: backupPath,
            PreDeployBackupSizeBytes: backupPath is null ? null : 16);
    }

    /// <summary>Helper to create a gzipped SQLite-magic file at <paramref name="path"/>.</summary>
    private static void WriteGzippedSqlite(string path, byte[]? body = null)
    {
        // SQLite header is the literal ASCII "SQLite format 3\0".
        var bytes = new List<byte>(Encoding.ASCII.GetBytes("SQLite format 3\0"));
        if (body is not null) bytes.AddRange(body);
        using var fs = File.Create(path);
        using var gz = new GZipStream(fs, CompressionLevel.Fastest);
        gz.Write(bytes.ToArray(), 0, bytes.Count);
    }

    /// <summary>Helper to create a gzipped scaffold-stub file at <paramref name="path"/>.</summary>
    private static void WriteGzippedScaffold(string path, string deployId)
    {
        var content = "-- NKS WDC pre-deploy snapshot SCAFFOLD\n" +
                      $"-- deployId: {deployId}\n";
        using var fs = File.Create(path);
        using var gz = new GZipStream(fs, CompressionLevel.Fastest);
        var bytes = Encoding.UTF8.GetBytes(content);
        gz.Write(bytes, 0, bytes.Length);
    }

    [Fact]
    public async Task RestoreAsync_ThrowsKeyNotFoundException_ForUnknownDeployId()
    {
        var deployId = Guid.NewGuid().ToString("N");
        var sites = RegistryWith("a.loc", null);
        var runs = RunsWith(deployId, null);
        var restorer = MakeRestorer(sites.Object, runs.Object);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            restorer.RestoreAsync("a.loc", deployId, default));
    }

    [Fact]
    public async Task RestoreAsync_ThrowsInvalidOperation_WhenRunHasNoBackupPath()
    {
        var deployId = Guid.NewGuid().ToString("N");
        var row = MakeRow(deployId, "a.loc", backupPath: null);
        var sites = RegistryWith("a.loc", new SiteConfig { Domain = "a.loc", DocumentRoot = MakeTempDir("dr") });
        var runs = RunsWith(deployId, row);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MakeRestorer(sites.Object, runs.Object).RestoreAsync("a.loc", deployId, default));
        Assert.Contains("no pre-deploy snapshot", ex.Message);
    }

    [Fact]
    public async Task RestoreAsync_ThrowsFileNotFound_WhenArchivePathDoesNotExist()
    {
        var deployId = Guid.NewGuid().ToString("N");
        var phantomPath = Path.Combine(Path.GetTempPath(), $"nks-restore-phantom-{Guid.NewGuid():N}.sql.gz");
        var row = MakeRow(deployId, "a.loc", backupPath: phantomPath);
        var sites = RegistryWith("a.loc", new SiteConfig { Domain = "a.loc", DocumentRoot = MakeTempDir("dr") });
        var runs = RunsWith(deployId, row);

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            MakeRestorer(sites.Object, runs.Object).RestoreAsync("a.loc", deployId, default));
    }

    [Fact]
    public async Task RestoreAsync_RefusesCrossSiteRestore()
    {
        var deployId = Guid.NewGuid().ToString("N");
        var archive = TempArchive();
        WriteGzippedSqlite(archive);
        var row = MakeRow(deployId, "real-owner.loc", backupPath: archive);
        var sites = RegistryWith("attacker.loc", new SiteConfig { Domain = "attacker.loc", DocumentRoot = MakeTempDir("dr") });
        var runs = RunsWith(deployId, row);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MakeRestorer(sites.Object, runs.Object).RestoreAsync("attacker.loc", deployId, default));
        Assert.Contains("Snapshot belongs to domain", ex.Message);
        Assert.Contains("real-owner.loc", ex.Message);
    }

    [Fact]
    public async Task RestoreAsync_RefusesScaffoldArchive()
    {
        var deployId = Guid.NewGuid().ToString("N");
        var archive = TempArchive();
        WriteGzippedScaffold(archive, deployId);
        var row = MakeRow(deployId, "a.loc", backupPath: archive);
        var sites = RegistryWith("a.loc", new SiteConfig { Domain = "a.loc", DocumentRoot = MakeTempDir("dr") });
        var runs = RunsWith(deployId, row);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MakeRestorer(sites.Object, runs.Object).RestoreAsync("a.loc", deployId, default));
        Assert.Contains("SCAFFOLD", ex.Message);
    }

    [Fact]
    public async Task RestoreAsync_ThrowsInvalidOperation_WhenSiteNotFound()
    {
        var deployId = Guid.NewGuid().ToString("N");
        var archive = TempArchive();
        WriteGzippedSqlite(archive);
        var row = MakeRow(deployId, "ghost.loc", backupPath: archive);
        var sites = RegistryWith("ghost.loc", null);
        var runs = RunsWith(deployId, row);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MakeRestorer(sites.Object, runs.Object).RestoreAsync("ghost.loc", deployId, default));
        Assert.Contains("Site 'ghost.loc' not found", ex.Message);
    }

    [Fact]
    public async Task RestoreAsync_RefusesSqliteWhenNoLiveDatabaseFileFound()
    {
        var deployId = Guid.NewGuid().ToString("N");
        var archive = TempArchive();
        WriteGzippedSqlite(archive);

        // Site has a docroot but NO sqlite/db file anywhere underneath.
        var siteRoot = MakeTempDir("siteRoot");
        var docRoot = Path.Combine(siteRoot, "public");
        Directory.CreateDirectory(docRoot);

        var row = MakeRow(deployId, "a.loc", backupPath: archive);
        var sites = RegistryWith("a.loc", new SiteConfig { Domain = "a.loc", DocumentRoot = docRoot });
        var runs = RunsWith(deployId, row);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MakeRestorer(sites.Object, runs.Object).RestoreAsync("a.loc", deployId, default));
        Assert.Contains("No SQLite database file found", ex.Message);
    }

    [Fact]
    public async Task RestoreAsync_SqliteMode_OverwritesLiveAndCreatesSafetyBackup()
    {
        var deployId = Guid.NewGuid().ToString("N");
        var archive = TempArchive();
        var snapshottedContent = Encoding.ASCII.GetBytes("PAYLOAD-FROM-SNAPSHOT-ARCHIVE");
        WriteGzippedSqlite(archive, body: snapshottedContent);

        var siteRoot = MakeTempDir("siteRoot");
        var docRoot = Path.Combine(siteRoot, "public");
        Directory.CreateDirectory(docRoot);
        var liveDir = Path.Combine(siteRoot, "data");
        Directory.CreateDirectory(liveDir);
        var liveDb = Path.Combine(liveDir, "app.sqlite");
        File.WriteAllText(liveDb, "ORIGINAL-LIVE-CONTENT");

        var row = MakeRow(deployId, "a.loc", backupPath: archive);
        var sites = RegistryWith("a.loc", new SiteConfig { Domain = "a.loc", DocumentRoot = docRoot });
        var runs = RunsWith(deployId, row);

        var result = await MakeRestorer(sites.Object, runs.Object).RestoreAsync("a.loc", deployId, default);

        Assert.Equal("sqlite", result.Mode);
        Assert.True(result.BytesProcessed > 0);
        // Live file now contains the snapshot's payload (header + body).
        var live = File.ReadAllBytes(liveDb);
        Assert.StartsWith("SQLite format 3", Encoding.ASCII.GetString(live));
        Assert.Contains("PAYLOAD-FROM-SNAPSHOT-ARCHIVE", Encoding.ASCII.GetString(live));
        // Safety .pre-restore.{ts}.bak exists with the original content.
        var safeties = Directory.EnumerateFiles(liveDir, "app.sqlite.pre-restore.*.bak").ToList();
        Assert.Single(safeties);
        Assert.Equal("ORIGINAL-LIVE-CONTENT", File.ReadAllText(safeties[0]));
    }

    [Fact]
    public async Task RestoreAsync_RefusesSqlMode_WhenEnvDiscoveryFails()
    {
        var deployId = Guid.NewGuid().ToString("N");
        var archive = TempArchive();
        // Archive starts with neither SQLite magic NOR the SCAFFOLD marker
        // → DetectArchiveMode returns "sql" → routes through .env discovery.
        using (var fs = File.Create(archive))
        using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
        {
            var sql = Encoding.UTF8.GetBytes("CREATE TABLE x (id INT);\n");
            gz.Write(sql, 0, sql.Length);
        }

        var siteRoot = MakeTempDir("siteRoot");
        var docRoot = Path.Combine(siteRoot, "public");
        Directory.CreateDirectory(docRoot);
        // No .env file written → resolver returns null → restore refuses.

        var row = MakeRow(deployId, "a.loc", backupPath: archive);
        var sites = RegistryWith("a.loc", new SiteConfig { Domain = "a.loc", DocumentRoot = docRoot });
        var runs = RunsWith(deployId, row);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MakeRestorer(sites.Object, runs.Object).RestoreAsync("a.loc", deployId, default));
        Assert.Contains(".env discovery failed", ex.Message);
    }
}
