using NKS.WebDevConsole.Core.Models;

namespace NKS.WebDevConsole.Core.Tests;

public sealed class BinaryCatalogTests
{
    [Fact]
    public void All_ContainsExpectedApps()
    {
        var apps = BinaryCatalog.All.Select(r => r.App).Distinct().ToHashSet();
        Assert.Contains("apache", apps);
        Assert.Contains("php", apps);
        Assert.Contains("mysql", apps);
        Assert.Contains("redis", apps);
        Assert.Contains("mailpit", apps);
        Assert.Contains("nginx", apps);
    }

    [Fact]
    public void All_EveryRelease_HasRequiredFields()
    {
        foreach (var r in BinaryCatalog.All)
        {
            Assert.False(string.IsNullOrEmpty(r.App), $"App is empty for {r}");
            Assert.False(string.IsNullOrEmpty(r.Version), $"Version is empty for {r.App}");
            Assert.False(string.IsNullOrEmpty(r.MajorMinor), $"MajorMinor is empty for {r.App} {r.Version}");
            Assert.False(string.IsNullOrEmpty(r.Url), $"Url is empty for {r.App} {r.Version}");
            Assert.False(string.IsNullOrEmpty(r.Os), $"Os is empty for {r.App} {r.Version}");
            Assert.False(string.IsNullOrEmpty(r.Arch), $"Arch is empty for {r.App} {r.Version}");
            Assert.False(string.IsNullOrEmpty(r.ArchiveType), $"ArchiveType is empty for {r.App} {r.Version}");
            Assert.False(string.IsNullOrEmpty(r.Source), $"Source is empty for {r.App} {r.Version}");
        }
    }

    [Fact]
    public void All_MajorMinor_IsPrefixOfVersion()
    {
        foreach (var r in BinaryCatalog.All)
            Assert.StartsWith(r.MajorMinor, r.Version);
    }

    [Fact]
    public void ForApp_ReturnsOnlyMatchingApp()
    {
        var phpReleases = BinaryCatalog.ForApp("php").ToList();
        Assert.True(phpReleases.Count > 0);
        Assert.All(phpReleases, r => Assert.Equal("php", r.App));
    }

    [Fact]
    public void ForApp_CaseInsensitive()
    {
        var upper = BinaryCatalog.ForApp("PHP").ToList();
        var lower = BinaryCatalog.ForApp("php").ToList();
        Assert.Equal(lower.Count, upper.Count);
    }

    [Fact]
    public void ForApp_UnknownApp_ReturnsEmpty()
    {
        Assert.Empty(BinaryCatalog.ForApp("nonexistent-app"));
    }

    [Fact]
    public void FindLatest_ReturnsFirstMatch()
    {
        var latest = BinaryCatalog.FindLatest("apache");
        Assert.NotNull(latest);
        Assert.Equal("apache", latest!.App);
    }

    [Fact]
    public void FindLatest_WithMajorMinor_FiltersCorrectly()
    {
        var php84 = BinaryCatalog.FindLatest("php", "8.4");
        Assert.NotNull(php84);
        Assert.Equal("8.4", php84!.MajorMinor);
    }

    [Fact]
    public void FindLatest_UnknownApp_ReturnsNull()
    {
        Assert.Null(BinaryCatalog.FindLatest("totally-fake"));
    }

    [Fact]
    public void Find_ExactVersion_ReturnsMatch()
    {
        var result = BinaryCatalog.Find("mysql", "8.4.8");
        Assert.NotNull(result);
        Assert.Equal("8.4.8", result!.Version);
        Assert.Equal("mysql", result.App);
    }

    [Fact]
    public void Find_WrongVersion_ReturnsNull()
    {
        Assert.Null(BinaryCatalog.Find("mysql", "99.99.99"));
    }

    [Fact]
    public void All_UrlsAreHttps()
    {
        foreach (var r in BinaryCatalog.All)
            Assert.StartsWith("https://", r.Url);
    }

    [Fact]
    public void All_NoDuplicateAppVersionPairs()
    {
        var pairs = BinaryCatalog.All.Select(r => $"{r.App}/{r.Version}/{r.Os}/{r.Arch}").ToList();
        Assert.Equal(pairs.Count, pairs.Distinct().Count());
    }
}
