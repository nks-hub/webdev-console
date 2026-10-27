using NKS.WebDevConsole.Daemon.Config;

namespace NKS.WebDevConsole.Daemon.Tests;

public class PathContainmentTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "wdc-containment", "backups");

    [Fact]
    public void FileInsideRoot_IsUnder()
    {
        Assert.True(PathContainment.IsUnder(Root, Path.Combine(Root, "wdc-backup-1.zip")));
    }

    [Fact]
    public void NestedFile_IsUnder()
    {
        Assert.True(PathContainment.IsUnder(Root, Path.Combine(Root, "nested", "a.zip")));
    }

    [Fact]
    public void SiblingFolderSharingThePrefix_IsNotUnder()
    {
        Assert.False(PathContainment.IsUnder(Root, Root + "-old" + Path.DirectorySeparatorChar + "a.zip"));
    }

    [Fact]
    public void TraversalOutOfRoot_IsNotUnder()
    {
        Assert.False(PathContainment.IsUnder(Root, Path.Combine(Root, "..", "state.db")));
    }

    [Fact]
    public void RootItself_IsNotUnder()
    {
        Assert.False(PathContainment.IsUnder(Root, Root));
    }

    [Fact]
    public void RootWithTrailingSeparator_StillMatchesChildren()
    {
        Assert.True(PathContainment.IsUnder(Root + Path.DirectorySeparatorChar, Path.Combine(Root, "a.zip")));
    }
}
