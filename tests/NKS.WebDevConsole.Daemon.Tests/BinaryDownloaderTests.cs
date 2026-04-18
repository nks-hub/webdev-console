using System.Formats.Tar;
using System.IO.Compression;
using Microsoft.Extensions.Logging;
using Moq;
using NKS.WebDevConsole.Daemon.Binaries;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Tests for <see cref="BinaryDownloader"/>, focusing on <c>ExtractAsync</c> —
/// the critical path where a network-sourced zip is unpacked into the local
/// filesystem. Two layers of concern:
///
///   1. **Security — zip-slip defense.** The binary catalog mirrors external
///      sources (Apache Lounge, PHP.net, mysql.com). A compromised mirror
///      could serve a zip with entries like <c>../../Windows/System32/evil.dll</c>.
///      We verify that entries containing <c>..</c> or resolving outside the
///      destination root are silently skipped (fail-safe, never fail-closed —
///      the rest of the archive still extracts).
///
///   2. **Single-file cloudflared rename.** Cloudflared releases ship as a
///      bare <c>cloudflared-windows-amd64.exe</c> which must be renamed to
///      <c>cloudflared.exe</c> so <c>BinaryManager.ResolveExecutable</c> can
///      find it by canonical name.
///
/// Also covers <see cref="DownloadProgress.PercentComplete"/> arithmetic.
/// </summary>
public sealed class BinaryDownloaderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly BinaryDownloader _sut;

    public BinaryDownloaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"nks-wdc-bindl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var httpFactory = new Mock<IHttpClientFactory>();
        httpFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient());
        var logger = new Mock<ILogger<BinaryDownloader>>();
        _sut = new BinaryDownloader(httpFactory.Object, logger.Object);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task ExtractAsync_NonexistentArchive_Throws()
    {
        var missing = Path.Combine(_tempDir, "nope.zip");
        var dest = Path.Combine(_tempDir, "out");

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => _sut.ExtractAsync(missing, dest));
    }

    [Fact]
    public async Task ExtractAsync_ValidZip_ExtractsAllFiles()
    {
        var zipPath = Path.Combine(_tempDir, "valid.zip");
        var destDir = Path.Combine(_tempDir, "out");

        using (var zs = new FileStream(zipPath, FileMode.Create))
        using (var zip = new ZipArchive(zs, ZipArchiveMode.Create))
        {
            AddTextEntry(zip, "bin/apache.exe", "binary content");
            AddTextEntry(zip, "conf/httpd.conf", "config content");
            AddTextEntry(zip, "readme.txt", "notes");
        }

        await _sut.ExtractAsync(zipPath, destDir);

        Assert.True(File.Exists(Path.Combine(destDir, "bin", "apache.exe")));
        Assert.True(File.Exists(Path.Combine(destDir, "conf", "httpd.conf")));
        Assert.True(File.Exists(Path.Combine(destDir, "readme.txt")));
    }

    [Fact]
    public async Task ExtractAsync_ZipSlip_ParentDirSegment_Skipped()
    {
        var zipPath = Path.Combine(_tempDir, "evil.zip");
        var destDir = Path.Combine(_tempDir, "out");

        using (var zs = new FileStream(zipPath, FileMode.Create))
        using (var zip = new ZipArchive(zs, ZipArchiveMode.Create))
        {
            AddTextEntry(zip, "../../escaped.txt", "pwned");
            AddTextEntry(zip, "safe.txt", "ok");
        }

        await _sut.ExtractAsync(zipPath, destDir);

        // Safe file must have been extracted, evil entry silently skipped.
        Assert.True(File.Exists(Path.Combine(destDir, "safe.txt")));
        // The escape target must NOT exist anywhere under the temp root.
        Assert.False(File.Exists(Path.Combine(_tempDir, "escaped.txt")));
    }

    [Fact]
    public async Task ExtractAsync_UnsupportedFormat_Throws()
    {
        var fakeRar = Path.Combine(_tempDir, "payload.rar");
        await File.WriteAllBytesAsync(fakeRar, new byte[] { 0x52, 0x61, 0x72, 0x21 });

        await Assert.ThrowsAsync<NotSupportedException>(
            () => _sut.ExtractAsync(fakeRar, Path.Combine(_tempDir, "out")));
    }

    [Theory]
    [InlineData(".exe")]
    [InlineData(".bin")]
    public async Task ExtractAsync_SingleFileBinary_CopiedToDestination(string ext)
    {
        var srcName = $"cloudflared-windows-amd64{ext}";
        var srcPath = Path.Combine(_tempDir, srcName);
        await File.WriteAllBytesAsync(srcPath, new byte[] { 0x4D, 0x5A }); // MZ header

        var dest = Path.Combine(_tempDir, "out");
        await _sut.ExtractAsync(srcPath, dest);

        // Cloudflared filename is detected and canonicalized to cloudflared.exe
        Assert.True(File.Exists(Path.Combine(dest, "cloudflared.exe")));
    }

    [Fact]
    public async Task ExtractAsync_SingleFileNonCloudflared_KeepsOriginalName()
    {
        var srcPath = Path.Combine(_tempDir, "caddy_2.7.4_windows_amd64.exe");
        await File.WriteAllBytesAsync(srcPath, new byte[] { 0x4D, 0x5A });

        var dest = Path.Combine(_tempDir, "out");
        await _sut.ExtractAsync(srcPath, dest);

        Assert.True(File.Exists(Path.Combine(dest, "caddy_2.7.4_windows_amd64.exe")));
    }

    [Fact]
    public async Task ExtractAsync_ValidTarGz_ExtractsAllFiles()
    {
        // Build a tiny tar.gz on disk: two files under a top-level
        // `httpd-2.4.66-linux-x64/` directory, matching the layout our
        // build-apache.yml workflow produces for Linux binaries. The test
        // exercises the new .NET 9 System.Formats.Tar + GZipStream path.
        var tarGzPath = Path.Combine(_tempDir, "httpd-linux.tar.gz");
        var destDir = Path.Combine(_tempDir, "out-targz");

        BuildTarGz(tarGzPath, new (string entryName, string content)[]
        {
            ("httpd-2.4.66-linux-x64/bin/httpd", "ELF-like payload"),
            ("httpd-2.4.66-linux-x64/conf/httpd.conf", "ServerRoot /opt/httpd"),
            ("httpd-2.4.66-linux-x64/README", "Apache httpd"),
        });

        await _sut.ExtractAsync(tarGzPath, destDir, archiveType: "tar.gz");

        Assert.True(File.Exists(Path.Combine(destDir, "httpd-2.4.66-linux-x64", "bin", "httpd")));
        Assert.True(File.Exists(Path.Combine(destDir, "httpd-2.4.66-linux-x64", "conf", "httpd.conf")));
        Assert.True(File.Exists(Path.Combine(destDir, "httpd-2.4.66-linux-x64", "README")));
        Assert.Equal(
            "ServerRoot /opt/httpd",
            await File.ReadAllTextAsync(Path.Combine(destDir, "httpd-2.4.66-linux-x64", "conf", "httpd.conf")));
    }

    [Fact]
    public async Task ExtractAsync_TarGz_ZipSlip_ParentDirSegment_Skipped()
    {
        var tarGzPath = Path.Combine(_tempDir, "evil.tar.gz");
        var destDir = Path.Combine(_tempDir, "out-evil-tar");

        BuildTarGz(tarGzPath, new (string entryName, string content)[]
        {
            ("../../escaped-from-tar.txt", "pwned"),
            ("safe/ok.txt", "ok"),
        });

        await _sut.ExtractAsync(tarGzPath, destDir, archiveType: "tar.gz");

        Assert.True(File.Exists(Path.Combine(destDir, "safe", "ok.txt")));
        Assert.False(File.Exists(Path.Combine(_tempDir, "escaped-from-tar.txt")));
    }

    [Fact]
    public async Task ExtractAsync_DirectoryEntryInZip_CreatesDirectory()
    {
        var zipPath = Path.Combine(_tempDir, "dirs.zip");
        var destDir = Path.Combine(_tempDir, "out");

        using (var zs = new FileStream(zipPath, FileMode.Create))
        using (var zip = new ZipArchive(zs, ZipArchiveMode.Create))
        {
            // Explicit directory entry — trailing slash, empty Name
            zip.CreateEntry("emptydir/");
            AddTextEntry(zip, "emptydir/file.txt", "inside");
        }

        await _sut.ExtractAsync(zipPath, destDir);

        Assert.True(Directory.Exists(Path.Combine(destDir, "emptydir")));
        Assert.True(File.Exists(Path.Combine(destDir, "emptydir", "file.txt")));
    }

    [Fact]
    public async Task ExtractAsync_CreatesDestinationDir_WhenMissing()
    {
        var zipPath = Path.Combine(_tempDir, "simple.zip");
        var destDir = Path.Combine(_tempDir, "nested", "deeper", "out");

        using (var zs = new FileStream(zipPath, FileMode.Create))
        using (var zip = new ZipArchive(zs, ZipArchiveMode.Create))
        {
            AddTextEntry(zip, "hello.txt", "hi");
        }

        await _sut.ExtractAsync(zipPath, destDir);

        Assert.True(Directory.Exists(destDir));
        Assert.True(File.Exists(Path.Combine(destDir, "hello.txt")));
    }

    [Fact]
    public void DownloadProgress_Zero_Total_Gives_Zero_Percent()
    {
        var p = new DownloadProgress("php", "8.3", 100, 0);
        Assert.Equal(0, p.PercentComplete);
    }

    [Fact]
    public void DownloadProgress_NegativeTotal_Gives_Zero_Percent()
    {
        var p = new DownloadProgress("php", "8.3", 100, -1);
        Assert.Equal(0, p.PercentComplete);
    }

    [Fact]
    public void DownloadProgress_HalfComplete_Gives_50_Percent()
    {
        var p = new DownloadProgress("php", "8.3", 500, 1000);
        Assert.Equal(50, p.PercentComplete);
    }

    [Fact]
    public void DownloadProgress_FullyComplete_Gives_100_Percent()
    {
        var p = new DownloadProgress("php", "8.3", 1000, 1000);
        Assert.Equal(100, p.PercentComplete);
    }

    [Fact]
    public void DownloadProgress_IsRecord_EqualByValue()
    {
        var a = new DownloadProgress("php", "8.3.10", 500, 1000);
        var b = new DownloadProgress("php", "8.3.10", 500, 1000);
        Assert.Equal(a, b);
    }

    private static void AddTextEntry(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path);
        using var w = new StreamWriter(entry.Open());
        w.Write(content);
    }

    /// <summary>
    /// Build a minimal tar.gz archive on disk via System.Formats.Tar wrapped
    /// in GZipStream — the same pair the production code uses to read it,
    /// which keeps this test honest about the round-trip rather than relying
    /// on an external fixture binary checked in to the repo.
    /// </summary>
    private static void BuildTarGz(string path, (string entryName, string content)[] entries)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var gzip = new GZipStream(fs, CompressionLevel.Fastest);
        using var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false);
        foreach (var (name, content) in entries)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(content);
            var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
            {
                DataStream = new MemoryStream(bytes),
            };
            writer.WriteEntry(entry);
        }
    }
}
