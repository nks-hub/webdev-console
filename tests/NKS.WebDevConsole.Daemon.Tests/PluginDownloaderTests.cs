using NKS.WebDevConsole.Daemon.Plugin;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// F95 PluginDownloader: tests the pure filtering helpers
/// (SelectZipDownload, ClassifyEntry) so the download-selection contract
/// is exercised deterministically without touching HTTP or ~/.wdc/plugins.
/// The full SyncLatestAsync roundtrip (zip stream + disk extract) is
/// covered by manual verification + the smoke-e2e script.
/// </summary>
public sealed class PluginDownloaderTests
{
    [Fact]
    public void SelectZipDownload_NullDownloads_ReturnsNull()
    {
        var release = new PluginCatalogRelease { Version = "1.0.0", Downloads = null };
        Assert.Null(PluginDownloader.SelectZipDownload(release));
    }

    [Fact]
    public void SelectZipDownload_EmptyDownloads_ReturnsNull()
    {
        var release = new PluginCatalogRelease { Version = "1.0.0", Downloads = new() };
        Assert.Null(PluginDownloader.SelectZipDownload(release));
    }

    [Fact]
    public void SelectZipDownload_BlankArchiveType_TreatedAsZip()
    {
        var release = new PluginCatalogRelease
        {
            Version = "1.0.0",
            Downloads = new()
            {
                new PluginCatalogDownload { Url = "https://x/a.zip", ArchiveType = null },
            },
        };
        var d = PluginDownloader.SelectZipDownload(release);
        Assert.NotNull(d);
        Assert.Equal("https://x/a.zip", d!.Url);
    }

    [Fact]
    public void SelectZipDownload_RejectsNonZipArchive()
    {
        var release = new PluginCatalogRelease
        {
            Version = "1.0.0",
            Downloads = new()
            {
                new PluginCatalogDownload { Url = "https://x/a.tar.gz", ArchiveType = "tar.gz" },
            },
        };
        Assert.Null(PluginDownloader.SelectZipDownload(release));
    }

    [Fact]
    public void SelectZipDownload_PicksFirstZipWhenMixed()
    {
        var release = new PluginCatalogRelease
        {
            Version = "1.0.0",
            Downloads = new()
            {
                new PluginCatalogDownload { Url = "https://x/a.tar.gz", ArchiveType = "tar.gz" },
                new PluginCatalogDownload { Url = "https://x/b.zip", ArchiveType = "zip" },
                new PluginCatalogDownload { Url = "https://x/c.zip", ArchiveType = "ZIP" },
            },
        };
        var d = PluginDownloader.SelectZipDownload(release);
        Assert.Equal("https://x/b.zip", d!.Url);
    }

    [Fact]
    public void SelectZipDownload_SkipsEntryWithBlankUrl()
    {
        var release = new PluginCatalogRelease
        {
            Version = "1.0.0",
            Downloads = new()
            {
                new PluginCatalogDownload { Url = "", ArchiveType = "zip" },
                new PluginCatalogDownload { Url = "https://x/real.zip", ArchiveType = "zip" },
            },
        };
        var d = PluginDownloader.SelectZipDownload(release);
        Assert.Equal("https://x/real.zip", d!.Url);
    }

    [Fact]
    public void ClassifyEntry_EmptyId_SkipMissingId()
    {
        var entry = new PluginCatalogEntry { Id = "", Releases = null };
        Assert.Equal(PluginDownloader.EntryClassification.SkipMissingId,
            PluginDownloader.ClassifyEntry(entry));
    }

    [Fact]
    public void ClassifyEntry_NullReleases_SkipNoReleases()
    {
        var entry = new PluginCatalogEntry { Id = "nks.wdc.apache", Releases = null };
        Assert.Equal(PluginDownloader.EntryClassification.SkipNoReleases,
            PluginDownloader.ClassifyEntry(entry));
    }

    [Fact]
    public void ClassifyEntry_EmptyReleases_SkipNoReleases()
    {
        var entry = new PluginCatalogEntry { Id = "nks.wdc.apache", Releases = new() };
        Assert.Equal(PluginDownloader.EntryClassification.SkipNoReleases,
            PluginDownloader.ClassifyEntry(entry));
    }

    [Fact]
    public void ClassifyEntry_BlankVersion_SkipMissingVersion()
    {
        var entry = new PluginCatalogEntry
        {
            Id = "nks.wdc.apache",
            Releases = new() { new PluginCatalogRelease { Version = "" } },
        };
        Assert.Equal(PluginDownloader.EntryClassification.SkipMissingVersion,
            PluginDownloader.ClassifyEntry(entry));
    }

    [Fact]
    public void ClassifyEntry_NoZipDownload_SkipNoZip()
    {
        var entry = new PluginCatalogEntry
        {
            Id = "nks.wdc.apache",
            Releases = new()
            {
                new PluginCatalogRelease
                {
                    Version = "1.0.0",
                    Downloads = new()
                    {
                        new PluginCatalogDownload { Url = "https://x/a.tar.gz", ArchiveType = "tar.gz" },
                    },
                },
            },
        };
        Assert.Equal(PluginDownloader.EntryClassification.SkipNoZipDownload,
            PluginDownloader.ClassifyEntry(entry));
    }

    [Fact]
    public void ClassifyEntry_WellFormed_Installable()
    {
        var entry = new PluginCatalogEntry
        {
            Id = "nks.wdc.apache",
            Releases = new()
            {
                new PluginCatalogRelease
                {
                    Version = "1.0.0",
                    Downloads = new()
                    {
                        new PluginCatalogDownload { Url = "https://x/a.zip", ArchiveType = "zip" },
                    },
                },
            },
        };
        Assert.Equal(PluginDownloader.EntryClassification.Installable,
            PluginDownloader.ClassifyEntry(entry));
    }

    [Fact]
    public void CacheRoot_ResolvesInsideUserProfile()
    {
        var root = PluginDownloader.CacheRoot();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.StartsWith(home, root);
        Assert.EndsWith(Path.Combine(".wdc", "plugins"), root);
    }
}
