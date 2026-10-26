using NKS.WebDevConsole.Core.Services;

namespace NKS.WebDevConsole.Core.Tests;

/// <summary>
/// Credential files (daemon port files with the bearer token, the MySQL root
/// password store) must never be readable by another user, not even for a
/// moment, and must not reuse a file someone else created.
/// </summary>
public sealed class OwnerOnlyFileTests : IDisposable
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("wdc-owner-only-");

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch { /* best effort */ }
    }

    private string PathFor(string name) => Path.Combine(_dir.FullName, name);

    [Fact]
    public void Writes_the_content_and_leaves_no_temp_file()
    {
        var path = PathFor("nks-wdc-daemon.port");
        OwnerOnlyFile.WriteAllTextAtomic(path, "17280\ntoken");
        Assert.Equal("17280\ntoken", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(_dir.FullName));
    }

    [Fact]
    public void The_file_is_owner_only_on_unix()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = PathFor("nks-wdc-daemon.port");
        OwnerOnlyFile.WriteAllTextAtomic(path, "17280\ntoken");
        Assert.Equal(OwnerOnly, File.GetUnixFileMode(path));
    }

    [Fact]
    public void A_world_readable_file_at_the_target_is_replaced_not_reused()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = PathFor("nks-wdc-daemon.port");
        File.WriteAllText(path, "old");
        File.SetUnixFileMode(path, OwnerOnly | UnixFileMode.GroupRead | UnixFileMode.OtherRead | UnixFileMode.OtherWrite);

        OwnerOnlyFile.WriteAllTextAtomic(path, "17280\nnew-token");

        Assert.Equal("17280\nnew-token", File.ReadAllText(path));
        Assert.Equal(OwnerOnly, File.GetUnixFileMode(path));
    }

    [Fact]
    public void A_pre_created_legacy_tmp_file_never_receives_the_token()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = PathFor("nks-wdc-daemon.port");
        var legacyTmp = path + ".tmp";
        File.WriteAllText(legacyTmp, "");
        File.SetUnixFileMode(legacyTmp, OwnerOnly | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite);

        OwnerOnlyFile.WriteAllTextAtomic(path, "17280\nsecret");

        Assert.Equal("", File.ReadAllText(legacyTmp));
    }

    [Fact]
    public void A_link_at_the_target_is_replaced_and_its_target_left_alone()
    {
        if (OperatingSystem.IsWindows()) return;
        var victim = PathFor("victim.txt");
        File.WriteAllText(victim, "untouched");
        var path = PathFor("nks-wdc-mcp.port");
        File.CreateSymbolicLink(path, victim);

        OwnerOnlyFile.WriteAllTextAtomic(path, "17280\nsecret");

        Assert.Equal("untouched", File.ReadAllText(victim));
        Assert.Null(new FileInfo(path).LinkTarget);
        Assert.Equal("17280\nsecret", File.ReadAllText(path));
    }

    [Fact]
    public void CreateNew_refuses_an_existing_file()
    {
        var path = PathFor("existing");
        File.WriteAllText(path, "x");
        Assert.Throws<IOException>(() => OwnerOnlyFile.CreateNew(path).Dispose());
        Assert.Equal("x", File.ReadAllText(path));
    }

    [Fact]
    public void A_failed_replace_throws_and_removes_the_temp_file()
    {
        // A directory at the target makes the final rename fail.
        var path = PathFor("nks-wdc-daemon.port");
        Directory.CreateDirectory(path);

        Assert.ThrowsAny<IOException>(() => OwnerOnlyFile.WriteAllTextAtomic(path, "17280\nsecret"));
        Assert.Empty(Directory.GetFiles(_dir.FullName));
    }
}
