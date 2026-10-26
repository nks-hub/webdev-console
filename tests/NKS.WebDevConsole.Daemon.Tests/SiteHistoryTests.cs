using Microsoft.Extensions.Logging;
using Moq;
using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Daemon.Config;
using NKS.WebDevConsole.Daemon.Sites;

namespace NKS.WebDevConsole.Daemon.Tests;

public class SiteHistoryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _sitesDir;
    private readonly SiteManager _manager;

    public SiteHistoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nks-site-history-" + Guid.NewGuid().ToString("N"));
        _sitesDir = Path.Combine(_tempDir, "sites");
        _manager = new SiteManager(
            new Mock<ILogger<SiteManager>>().Object,
            new TemplateEngine(),
            new AtomicWriter(),
            _sitesDir,
            Path.Combine(_tempDir, "generated"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public async Task Update_ArchivesPreviousToml_AndRestoreBringsItBack()
    {
        await _manager.CreateAsync(new SiteConfig { Domain = "hist.loc", DocumentRoot = "/srv/old", PhpVersion = "8.2" });
        await _manager.UpdateAsync(new SiteConfig { Domain = "hist.loc", DocumentRoot = "/srv/new", PhpVersion = "8.3" });

        var history = _manager.ListHistory("hist.loc");
        var entry = Assert.Single(history);
        Assert.NotEqual(DateTime.MinValue, entry.CreatedAt);

        var restored = await _manager.RestoreFromHistoryAsync("hist.loc", entry.Timestamp);

        Assert.NotNull(restored);
        Assert.Equal("/srv/old", _manager.Get("hist.loc")!.DocumentRoot);
        Assert.Equal("8.2", _manager.Get("hist.loc")!.PhpVersion);
        // The pre-rollback config is archived too, so the rollback can be undone.
        Assert.Equal(2, _manager.ListHistory("hist.loc").Count);
    }

    [Fact]
    public async Task Restore_UnknownTimestamp_ReturnsNull()
    {
        await _manager.CreateAsync(new SiteConfig { Domain = "none.loc", DocumentRoot = "/srv/x" });
        Assert.Null(await _manager.RestoreFromHistoryAsync("none.loc", "20200101_000000"));
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("20200101_000000/../../x")]
    [InlineData("latest")]
    public async Task Restore_RejectsMalformedTimestamp(string timestamp)
    {
        await _manager.CreateAsync(new SiteConfig { Domain = "bad.loc", DocumentRoot = "/srv/x" });
        await Assert.ThrowsAsync<ArgumentException>(() => _manager.RestoreFromHistoryAsync("bad.loc", timestamp));
    }

    [Fact]
    public async Task ConcurrentCreates_OfDifferentSites_AllLand()
    {
        var tasks = Enumerable.Range(0, 20).Select(i =>
            _manager.CreateAsync(new SiteConfig { Domain = $"c{i}.loc", DocumentRoot = $"/srv/c{i}" }));
        await Task.WhenAll(tasks);

        Assert.Equal(20, _manager.Sites.Count);
        _manager.LoadAll();
        Assert.Equal(20, _manager.Sites.Count);
    }
}
