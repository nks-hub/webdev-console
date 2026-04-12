using NKS.WebDevConsole.Daemon.Config;

namespace NKS.WebDevConsole.Daemon.Tests;

public class AtomicWriterTests : IDisposable
{
    private readonly AtomicWriter _writer = new();
    private readonly string _tempDir;

    public AtomicWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nks-atomic-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public async Task WriteAsync_CreatesNewFile()
    {
        var target = Path.Combine(_tempDir, "config.conf");

        await _writer.WriteAsync(target, "ServerName test.loc");

        Assert.True(File.Exists(target));
        Assert.Equal("ServerName test.loc", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task WriteAsync_OverwritesExistingFile()
    {
        var target = Path.Combine(_tempDir, "overwrite.conf");
        await File.WriteAllTextAsync(target, "old content");

        await _writer.WriteAsync(target, "new content");

        Assert.Equal("new content", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task WriteAsync_CreatesHistoryDirectory_WhenFileExists()
    {
        var target = Path.Combine(_tempDir, "history-test.conf");
        await File.WriteAllTextAsync(target, "version 1");

        await _writer.WriteAsync(target, "version 2");

        var historyDir = Path.Combine(_tempDir, "history");
        Assert.True(Directory.Exists(historyDir));
    }

    [Fact]
    public async Task WriteAsync_ArchivesPreviousVersion()
    {
        var target = Path.Combine(_tempDir, "archive.conf");
        await File.WriteAllTextAsync(target, "original content");

        await _writer.WriteAsync(target, "updated content");

        var historyDir = Path.Combine(_tempDir, "history");
        var historyFiles = Directory.GetFiles(historyDir);
        Assert.Single(historyFiles);

        var archivedContent = await File.ReadAllTextAsync(historyFiles[0]);
        Assert.Equal("original content", archivedContent);
    }

    [Fact]
    public async Task WriteAsync_PrunesOldHistory_BeyondMaxHistory()
    {
        var target = Path.Combine(_tempDir, "prune.conf");
        var maxHistory = 3;

        // Create initial file and write maxHistory+2 versions to generate enough history
        await File.WriteAllTextAsync(target, "v0");
        for (int i = 1; i <= maxHistory + 2; i++)
        {
            await _writer.WriteAsync(target, $"v{i}", maxHistory);
            // Small delay to ensure unique timestamps
            await Task.Delay(50);
        }

        var historyDir = Path.Combine(_tempDir, "history");
        var historyFiles = Directory.GetFiles(historyDir, "prune.conf.*");

        Assert.True(historyFiles.Length <= maxHistory,
            $"Expected at most {maxHistory} history files, found {historyFiles.Length}");
    }

    [Fact]
    public async Task WriteAsync_TmpFileIsRemoved_AfterWrite()
    {
        var target = Path.Combine(_tempDir, "tmp-check.conf");

        await _writer.WriteAsync(target, "content");

        Assert.False(File.Exists(target + ".tmp"));
    }

    [Fact]
    public async Task WriteAsync_MultipleWrites_AllArchived()
    {
        var target = Path.Combine(_tempDir, "multi.conf");

        await File.WriteAllTextAsync(target, "v1");
        await _writer.WriteAsync(target, "v2", maxHistory: 10);
        // Wait >1s to ensure different timestamp (yyyyMMdd_HHmmss)
        await Task.Delay(1100);
        await _writer.WriteAsync(target, "v3", maxHistory: 10);

        var historyDir = Path.Combine(_tempDir, "history");
        var historyFiles = Directory.GetFiles(historyDir, "multi.conf.*");
        Assert.Equal(2, historyFiles.Length);

        // Final file should have latest content
        Assert.Equal("v3", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task WriteAsync_FirstWrite_NoHistoryCreated()
    {
        var target = Path.Combine(_tempDir, "first-write.conf");

        await _writer.WriteAsync(target, "first content");

        var historyDir = Path.Combine(_tempDir, "history");
        Assert.False(Directory.Exists(historyDir));
    }

    [Fact]
    public async Task WriteAsync_CreatesParentDirectory_WhenMissing()
    {
        var deepPath = Path.Combine(_tempDir, "nested", "dir", "config.conf");
        Assert.False(Directory.Exists(Path.GetDirectoryName(deepPath)!));

        await _writer.WriteAsync(deepPath, "nested content");

        Assert.True(File.Exists(deepPath));
        Assert.Equal("nested content", await File.ReadAllTextAsync(deepPath));
    }

    [Fact]
    public async Task WriteAsync_LargeContent_WritesCorrectly()
    {
        var target = Path.Combine(_tempDir, "large.conf");
        var content = new string('x', 1024 * 100); // 100 KB

        await _writer.WriteAsync(target, content);

        Assert.Equal(content.Length, new FileInfo(target).Length);
    }
}
