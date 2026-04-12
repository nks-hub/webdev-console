using NKS.WebDevConsole.Plugin.Hosts;

namespace NKS.WebDevConsole.Core.Tests;

public class HostsManagerTests : IDisposable
{
    private readonly string _tempDir;

    public HostsManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nks-hosts-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public void GenerateHostsBlock_CreatesCorrectFormat()
    {
        var manager = new HostsManager(Path.Combine(_tempDir, "hosts"));
        var domains = new[] { "mysite.loc", "api.mysite.loc" };

        var block = manager.GenerateHostsBlock(domains);

        Assert.Contains("# BEGIN NKS WebDev Console", block);
        Assert.Contains("# END NKS WebDev Console", block);
        Assert.Contains("127.0.0.1\tmysite.loc", block);
        Assert.Contains("127.0.0.1\tapi.mysite.loc", block);
    }

    [Fact]
    public void GenerateHostsBlock_UsesCustomIp()
    {
        var manager = new HostsManager(Path.Combine(_tempDir, "hosts"));
        var domains = new[] { "test.loc" };

        var block = manager.GenerateHostsBlock(domains, "192.168.1.100");

        Assert.Contains("192.168.1.100\ttest.loc", block);
    }

    [Fact]
    public void GenerateHostsBlock_EmptyDomains_OnlyMarkers()
    {
        var manager = new HostsManager(Path.Combine(_tempDir, "hosts"));
        var domains = Array.Empty<string>();

        var block = manager.GenerateHostsBlock(domains);

        Assert.Contains("# BEGIN NKS WebDev Console", block);
        Assert.Contains("# END NKS WebDev Console", block);
        Assert.DoesNotContain("127.0.0.1", block);
    }

    [Fact]
    public void ParseHostsFile_SplitsCorrectly_WithMarkers()
    {
        var manager = new HostsManager(Path.Combine(_tempDir, "hosts"));
        var content = "# system hosts\r\n127.0.0.1 localhost\r\n\r\n" +
                      "# BEGIN NKS WebDev Console\r\n127.0.0.1\ttest.loc\r\n# END NKS WebDev Console\r\n\r\n" +
                      "# other entries\r\n";

        var (before, managed, after) = manager.ParseHostsFile(content);

        Assert.Contains("localhost", before);
        Assert.Contains("test.loc", managed);
        Assert.Contains("other entries", after);
    }

    [Fact]
    public void ParseHostsFile_NoMarkers_ReturnsContentAsBefore()
    {
        var manager = new HostsManager(Path.Combine(_tempDir, "hosts"));
        var content = "# plain hosts file\r\n127.0.0.1 localhost\r\n";

        var (before, managed, after) = manager.ParseHostsFile(content);

        Assert.Equal(content, before);
        Assert.Equal(string.Empty, managed);
        Assert.Equal(string.Empty, after);
    }

    [Fact]
    public void ParseHostsFile_MarkersOnly_NoExtraContent()
    {
        var manager = new HostsManager(Path.Combine(_tempDir, "hosts"));
        var content = "# BEGIN NKS WebDev Console\r\n127.0.0.1\tsite.loc\r\n# END NKS WebDev Console\r\n";

        var (before, managed, after) = manager.ParseHostsFile(content);

        Assert.Equal("", before);
        Assert.Contains("site.loc", managed);
    }

    [Fact]
    public void BuildUpdatedContent_InsertsBlock_WhenNoExistingMarkers()
    {
        var manager = new HostsManager(Path.Combine(_tempDir, "hosts"));
        var currentContent = "# system hosts\r\n127.0.0.1 localhost\r\n";
        var domains = new[] { "newsite.loc" };

        var result = manager.BuildUpdatedContent(currentContent, domains);

        Assert.Contains("# BEGIN NKS WebDev Console", result);
        Assert.Contains("127.0.0.1\tnewsite.loc", result);
        Assert.Contains("# END NKS WebDev Console", result);
        Assert.Contains("localhost", result);
    }

    [Fact]
    public void BuildUpdatedContent_ReplacesExistingBlock()
    {
        var manager = new HostsManager(Path.Combine(_tempDir, "hosts"));
        var currentContent = "127.0.0.1 localhost\r\n\r\n" +
                             "# BEGIN NKS WebDev Console\r\n127.0.0.1\told.loc\r\n# END NKS WebDev Console\r\n";
        var domains = new[] { "new.loc", "api.new.loc" };

        var result = manager.BuildUpdatedContent(currentContent, domains);

        Assert.Contains("127.0.0.1\tnew.loc", result);
        Assert.Contains("127.0.0.1\tapi.new.loc", result);
        Assert.DoesNotContain("old.loc", result);
        Assert.Contains("localhost", result);
    }

    [Fact]
    public void BuildUpdatedContent_EmptyDomains_RemovesManagedBlock()
    {
        var manager = new HostsManager(Path.Combine(_tempDir, "hosts"));
        var currentContent = "127.0.0.1 localhost\r\n\r\n" +
                             "# BEGIN NKS WebDev Console\r\n127.0.0.1\tsite.loc\r\n# END NKS WebDev Console\r\n";

        var result = manager.BuildUpdatedContent(currentContent, Array.Empty<string>());

        Assert.DoesNotContain("BEGIN NKS WebDev Console", result);
        Assert.DoesNotContain("site.loc", result);
        Assert.Contains("localhost", result);
    }

    [Fact]
    public void BuildUpdatedContent_PreservesContentBeforeAndAfterBlock()
    {
        var manager = new HostsManager(Path.Combine(_tempDir, "hosts"));
        var currentContent = "# header\r\n127.0.0.1 localhost\r\n\r\n" +
                             "# BEGIN NKS WebDev Console\r\n127.0.0.1\told.loc\r\n# END NKS WebDev Console\r\n\r\n" +
                             "# footer\r\n::1 localhost\r\n";
        var domains = new[] { "updated.loc" };

        var result = manager.BuildUpdatedContent(currentContent, domains);

        Assert.Contains("# header", result);
        Assert.Contains("127.0.0.1 localhost", result);
        Assert.Contains("127.0.0.1\tupdated.loc", result);
        Assert.Contains("# footer", result);
        Assert.Contains("::1 localhost", result);
    }

    [Fact]
    public void GetManagedEntries_ReturnsEmpty_WhenFileNotExists()
    {
        var manager = new HostsManager(Path.Combine(_tempDir, "nonexistent-hosts"));

        var entries = manager.GetManagedEntries();

        Assert.Empty(entries);
    }

    [Fact]
    public void GetManagedEntries_ParsesEntriesFromFile()
    {
        var hostsPath = Path.Combine(_tempDir, "hosts");
        File.WriteAllText(hostsPath,
            "127.0.0.1 localhost\r\n" +
            "# BEGIN NKS WebDev Console\r\n" +
            "127.0.0.1\tsite1.loc\r\n" +
            "127.0.0.1\tsite2.loc\r\n" +
            "# END NKS WebDev Console\r\n");

        var manager = new HostsManager(hostsPath);
        var entries = manager.GetManagedEntries();

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Domain == "site1.loc" && e.Ip == "127.0.0.1");
        Assert.Contains(entries, e => e.Domain == "site2.loc" && e.Ip == "127.0.0.1");
    }

    [Fact]
    public void HostsPath_ReturnsCustomPath()
    {
        var customPath = Path.Combine(_tempDir, "custom-hosts");
        var manager = new HostsManager(customPath);

        Assert.Equal(customPath, manager.HostsPath);
    }

    [Fact]
    public void BuildUpdatedContent_HandlesUnixLineEndings()
    {
        var manager = new HostsManager(Path.Combine(_tempDir, "hosts"));
        var content = "127.0.0.1 localhost\n# other\n";

        var result = manager.BuildUpdatedContent(content, new[] { "unix.loc" });

        Assert.Contains("127.0.0.1\tunix.loc", result);
        Assert.Contains("localhost", result);
    }

    [Fact]
    public void GenerateHostsBlock_MultipleDomains_OnePerLine()
    {
        var manager = new HostsManager(Path.Combine(_tempDir, "hosts"));
        var block = manager.GenerateHostsBlock(new[] { "a.loc", "b.loc", "c.loc" });

        Assert.Contains("127.0.0.1\ta.loc", block);
        Assert.Contains("127.0.0.1\tb.loc", block);
        Assert.Contains("127.0.0.1\tc.loc", block);
    }
}
