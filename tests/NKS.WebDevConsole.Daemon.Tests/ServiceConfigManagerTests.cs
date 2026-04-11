using NKS.WebDevConsole.Daemon.Config;
using NKS.WebDevConsole.Daemon.Services;

namespace NKS.WebDevConsole.Daemon.Tests;

public sealed class ServiceConfigManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ServiceConfigManager _manager;

    public ServiceConfigManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nks-service-config-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _manager = new ServiceConfigManager(new AtomicWriter(), _tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public async Task SaveAsync_WritesManagedApacheConfig_AndArchivesHistory()
    {
        var path = Path.Combine(_tempDir, "binaries", "apache", "2.4.63", "conf", "httpd.conf");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "ServerName old.loc");

        await _manager.SaveAsync("apache", path, "ServerName new.loc");

        Assert.Equal("ServerName new.loc", await File.ReadAllTextAsync(path));
        var historyDir = Path.Combine(Path.GetDirectoryName(path)!, "history");
        Assert.Single(Directory.GetFiles(historyDir, "httpd.conf.*"));
    }

    [Fact]
    public async Task SaveAsync_RejectsPathOutsideManagedRoots()
    {
        var roguePath = Path.Combine(_tempDir, "..", "..", "rogue.conf");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _manager.SaveAsync("apache", roguePath, "ServerName nope"));

        Assert.Contains("not a managed apache config file", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetFilesAsync_ReturnsManagedPhpIniFiles()
    {
        var php82 = Path.Combine(_tempDir, "binaries", "php", "8.2.18", "php.ini");
        var php83 = Path.Combine(_tempDir, "binaries", "php", "8.3.6", "php.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(php82)!);
        Directory.CreateDirectory(Path.GetDirectoryName(php83)!);
        await File.WriteAllTextAsync(php82, "memory_limit=256M");
        await File.WriteAllTextAsync(php83, "memory_limit=512M");

        var files = await _manager.GetFilesAsync("php");

        Assert.Equal(2, files.Count);
        Assert.Contains(files, f => f.Name.Contains("8.3.6", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(files, f => f.Name.Contains("8.2.18", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SaveAsync_AcceptsHttpdAlias_ForApache()
    {
        var path = Path.Combine(_tempDir, "binaries", "apache", "2.4.63", "conf", "httpd.conf");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "ServerName old.loc");

        await _manager.SaveAsync("httpd", path, "ServerName alias.loc");

        Assert.Equal("ServerName alias.loc", await File.ReadAllTextAsync(path));
    }
}
