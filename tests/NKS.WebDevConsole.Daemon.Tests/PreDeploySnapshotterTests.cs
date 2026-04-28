using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Core.Services;
using NKS.WebDevConsole.Daemon.Deploy;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Unit tests for <see cref="PreDeploySnapshotter"/>. Uses Moq to supply a
/// fake <see cref="ISiteRegistry"/>. The snapshotter writes output to
/// <c>{WdcPaths.BackupsRoot}/pre-deploy/{deployId}.sql.gz</c>; because
/// <see cref="WdcPaths"/> caches its root after first access, we cannot
/// redirect it per-test — instead tests track the output path returned by
/// <see cref="PreDeploySnapshotResult"/> and delete only that file in
/// cleanup. Temp site directories live in <see cref="Path.GetTempPath"/>.
/// </summary>
public sealed class PreDeploySnapshotterTests : IDisposable
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
        var dir = Path.Combine(Path.GetTempPath(), $"nks-snap-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static PreDeploySnapshotter MakeSnapshotter(ISiteRegistry registry) =>
        new PreDeploySnapshotter(registry, NullLogger<PreDeploySnapshotter>.Instance);

    private static string DeployId() => Guid.NewGuid().ToString("N");

    private static Mock<ISiteRegistry> RegistryWith(string domain, SiteConfig? config)
    {
        var mock = new Mock<ISiteRegistry>();
        mock.Setup(r => r.GetSite(domain)).Returns(config);
        mock.Setup(r => r.Sites).Returns(
            config is null
                ? new Dictionary<string, SiteConfig>()
                : new Dictionary<string, SiteConfig> { [domain] = config });
        return mock;
    }

    // --- Site not found ---

    [Fact]
    public async Task SiteNotFound_Throws_InvalidOperationException()
    {
        var registry = RegistryWith("unknown.loc", null);
        var snapshotter = MakeSnapshotter(registry.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            snapshotter.CreateAsync("unknown.loc", DeployId(), CancellationToken.None));
    }

    // --- SQLite detected ---

    [Fact]
    public async Task SqliteInDataSubdir_IsGzipped_ReturnsCorrectPathAndPositiveSize()
    {
        // Layout: siteRoot/www/ is DocumentRoot, siteRoot/data/app.sqlite exists
        var siteRoot = MakeTempDir("sqlitesite");
        var docRoot = Path.Combine(siteRoot, "www");
        Directory.CreateDirectory(docRoot);
        var dataDir = Path.Combine(siteRoot, "data");
        Directory.CreateDirectory(dataDir);
        var sqlitePath = Path.Combine(dataDir, "app.sqlite");

        // Write minimal valid SQLite content (the header magic bytes)
        var sqliteHeader = Encoding.UTF8.GetBytes("SQLite format 3\0 hello world data");
        File.WriteAllBytes(sqlitePath, sqliteHeader);

        var domain = "sqlitesite.loc";
        var deployId = DeployId();
        var config = new SiteConfig { Domain = domain, DocumentRoot = docRoot };
        var registry = RegistryWith(domain, config);
        var snapshotter = MakeSnapshotter(registry.Object);

        var result = await snapshotter.CreateAsync(domain, deployId, CancellationToken.None);
        _tempFiles.Add(result.Path);

        // Path should be under WdcPaths.BackupsRoot/pre-deploy/
        var expectedDir = Path.Combine(WdcPaths.BackupsRoot, "pre-deploy");
        Assert.StartsWith(expectedDir, result.Path, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith($"{deployId}.sql.gz", result.Path, StringComparison.OrdinalIgnoreCase);

        // File must exist and be non-empty
        Assert.True(File.Exists(result.Path));
        Assert.True(result.SizeBytes > 0);
        Assert.Equal(new FileInfo(result.Path).Length, result.SizeBytes);

        // Must be valid gzip (readable without exception)
        await using var fs = File.OpenRead(result.Path);
        await using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var reader = new StreamReader(gz, Encoding.UTF8);
        var content = await reader.ReadToEndAsync();
        Assert.NotEmpty(content);
    }

    [Fact]
    public async Task SqliteAtDocumentRoot_IsDetectedAndGzipped()
    {
        // Layout: DocumentRoot itself contains the sqlite file (no subdirectory)
        var siteRoot = MakeTempDir("flatsite");
        var docRoot = Path.Combine(siteRoot, "www");
        Directory.CreateDirectory(docRoot);
        var sqlitePath = Path.Combine(docRoot, "db.db");
        File.WriteAllBytes(sqlitePath, new byte[] { 0x53, 0x51, 0x4C, 0x69 }); // "SQLi"

        var domain = "flat.loc";
        var deployId = DeployId();
        var config = new SiteConfig { Domain = domain, DocumentRoot = docRoot };
        var registry = RegistryWith(domain, config);
        var snapshotter = MakeSnapshotter(registry.Object);

        var result = await snapshotter.CreateAsync(domain, deployId, CancellationToken.None);
        _tempFiles.Add(result.Path);

        Assert.True(File.Exists(result.Path));
        Assert.True(result.SizeBytes > 0);
    }

    // --- No SQLite → scaffold stub ---

    [Fact]
    public async Task NoSqliteAnywhere_WritesScaffoldStub_GzipReadable()
    {
        var siteRoot = MakeTempDir("nodb");
        var docRoot = Path.Combine(siteRoot, "www");
        Directory.CreateDirectory(docRoot);
        // Deliberately no sqlite files anywhere under siteRoot

        var domain = "nodb.loc";
        var deployId = DeployId();
        var config = new SiteConfig { Domain = domain, DocumentRoot = docRoot };
        var registry = RegistryWith(domain, config);
        var snapshotter = MakeSnapshotter(registry.Object);

        var result = await snapshotter.CreateAsync(domain, deployId, CancellationToken.None);
        _tempFiles.Add(result.Path);

        Assert.True(File.Exists(result.Path));
        Assert.True(result.SizeBytes > 0);

        // Decompress and verify scaffold markers
        await using var fs = File.OpenRead(result.Path);
        await using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var reader = new StreamReader(gz, Encoding.UTF8);
        var content = await reader.ReadToEndAsync();

        Assert.Contains("SCAFFOLD", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(deployId, content);
    }

    [Fact]
    public async Task NoSqliteAnywhere_ScaffoldContainsDomainInContent()
    {
        var siteRoot = MakeTempDir("nodob-dom");
        var docRoot = Path.Combine(siteRoot, "www");
        Directory.CreateDirectory(docRoot);

        var domain = "nodomain-check.loc";
        var deployId = DeployId();
        var config = new SiteConfig { Domain = domain, DocumentRoot = docRoot };
        var registry = RegistryWith(domain, config);
        var snapshotter = MakeSnapshotter(registry.Object);

        var result = await snapshotter.CreateAsync(domain, deployId, CancellationToken.None);
        _tempFiles.Add(result.Path);

        await using var fs = File.OpenRead(result.Path);
        await using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var reader = new StreamReader(gz, Encoding.UTF8);
        var content = await reader.ReadToEndAsync();

        Assert.Contains(domain, content);
    }

    // --- Output path shape ---

    [Fact]
    public async Task OutputPath_FollowsBackupsRootPreDeployShape()
    {
        var siteRoot = MakeTempDir("pathcheck");
        var docRoot = Path.Combine(siteRoot, "www");
        Directory.CreateDirectory(docRoot);

        var domain = "pathcheck.loc";
        var deployId = DeployId();
        var config = new SiteConfig { Domain = domain, DocumentRoot = docRoot };
        var registry = RegistryWith(domain, config);
        var snapshotter = MakeSnapshotter(registry.Object);

        var result = await snapshotter.CreateAsync(domain, deployId, CancellationToken.None);
        _tempFiles.Add(result.Path);

        var expectedDir = Path.Combine(WdcPaths.BackupsRoot, "pre-deploy");
        var expectedFile = $"{deployId}.sql.gz";
        Assert.Equal(Path.Combine(expectedDir, expectedFile), result.Path,
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ScaffoldResult_SizeBytesMatchesFileOnDisk()
    {
        var siteRoot = MakeTempDir("sizecheck");
        var docRoot = Path.Combine(siteRoot, "www");
        Directory.CreateDirectory(docRoot);

        var domain = "sizecheck.loc";
        var deployId = DeployId();
        var config = new SiteConfig { Domain = domain, DocumentRoot = docRoot };
        var registry = RegistryWith(domain, config);
        var snapshotter = MakeSnapshotter(registry.Object);

        var result = await snapshotter.CreateAsync(domain, deployId, CancellationToken.None);
        _tempFiles.Add(result.Path);

        var actualSize = new FileInfo(result.Path).Length;
        Assert.Equal(actualSize, result.SizeBytes);
    }

    [Fact]
    public async Task SqliteResult_DurationIsPositive()
    {
        var siteRoot = MakeTempDir("duration");
        var docRoot = Path.Combine(siteRoot, "www");
        Directory.CreateDirectory(docRoot);
        var dataDir = Path.Combine(siteRoot, "data");
        Directory.CreateDirectory(dataDir);
        File.WriteAllBytes(Path.Combine(dataDir, "db.sqlite"), new byte[512]);

        var domain = "duration.loc";
        var deployId = DeployId();
        var config = new SiteConfig { Domain = domain, DocumentRoot = docRoot };
        var registry = RegistryWith(domain, config);
        var snapshotter = MakeSnapshotter(registry.Object);

        var result = await snapshotter.CreateAsync(domain, deployId, CancellationToken.None);
        _tempFiles.Add(result.Path);

        Assert.True(result.Duration >= TimeSpan.Zero);
    }
}
