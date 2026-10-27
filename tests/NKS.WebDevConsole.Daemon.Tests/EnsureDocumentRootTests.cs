using NKS.WebDevConsole.Daemon.Sites;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// A new site's folder is created by the daemon when it is absolute and missing, so the site opens
/// without a manual mkdir. Everything runs inside a per-test temp directory.
/// </summary>
public sealed class EnsureDocumentRootTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), $"wdc-docroot-{Guid.NewGuid():N}");

    public EnsureDocumentRootTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void CreatesMissingAbsoluteFolder_IncludingParents_AndNothingElse()
    {
        var root = Path.Combine(_tmp, "a", "b", "www");

        Assert.True(SiteManager.EnsureDocumentRoot(root));

        Assert.True(Directory.Exists(root));
        Assert.Empty(Directory.GetFileSystemEntries(root));
    }

    [Fact]
    public void ExistingFolder_IsLeftAlone()
    {
        var marker = Path.Combine(_tmp, "keep.txt");
        File.WriteAllText(marker, "x");

        Assert.False(SiteManager.EnsureDocumentRoot(_tmp));

        Assert.True(File.Exists(marker));
    }

    [Fact]
    public void RelativePath_IsNotCreated()
    {
        var name = $"wdc-relative-{Guid.NewGuid():N}";

        Assert.False(SiteManager.EnsureDocumentRoot(name));

        Assert.False(Directory.Exists(Path.GetFullPath(name)));
    }

    [Fact]
    public void FileInTheWay_ThrowsInsteadOfPretendingSuccess()
    {
        var file = Path.Combine(_tmp, "www");
        File.WriteAllText(file, "x");

        Assert.ThrowsAny<IOException>(() => SiteManager.EnsureDocumentRoot(file));
    }
}
