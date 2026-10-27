using NKS.WebDevConsole.Daemon.Hosts;

namespace NKS.WebDevConsole.Daemon.Tests;

public class HostsRestorePathTests
{
    private static readonly string BackupsDir = Path.Combine(Path.GetTempPath(), "wdc-hosts-restore-tests", "backups", "hosts");

    [Theory]
    [InlineData("hosts-20260101T000000Z.bak")]
    [InlineData("HOSTS-20260101T000000Z.BAK")]
    public void A_hosts_backup_inside_the_backup_folder_is_accepted(string name)
    {
        var full = Path.Combine(BackupsDir, name);
        Assert.Equal(full, HostsEndpoints.ResolveBackupPath(full, BackupsDir));
    }

    [Fact]
    public void A_bare_backup_file_name_resolves_into_the_backup_folder()
    {
        Assert.Equal(Path.Combine(BackupsDir, "hosts-1.bak"), HostsEndpoints.ResolveBackupPath("hosts-1.bak", BackupsDir));
    }

    [Theory]
    [InlineData("/etc/shadow")]
    [InlineData("/home/victim/.ssh/authorized_keys")]
    [InlineData("../../state.db")]
    [InlineData("../hosts-escape.bak")]
    [InlineData("sub/hosts-nested.bak")]
    [InlineData("notes.txt")]
    [InlineData("hosts-1.bak.txt")]
    public void Anything_else_is_refused(string path)
    {
        Assert.Null(HostsEndpoints.ResolveBackupPath(path, BackupsDir));
    }

    [Fact]
    public void A_sibling_folder_sharing_the_prefix_is_refused()
    {
        var sibling = BackupsDir + "-old";
        Assert.Null(HostsEndpoints.ResolveBackupPath(Path.Combine(sibling, "hosts-1.bak"), BackupsDir));
    }
}
