using NKS.WebDevConsole.Daemon.Apache;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Phase 6.22 — unit tests for the boot-heal vhost stale-port scanner.
/// Each test seeds a temp sites-enabled directory with .conf files and
/// asserts which (if any) basenames the scanner reports as stale.
/// </summary>
public sealed class VhostStalePortScannerTests : IDisposable
{
    private readonly string _dir;

    public VhostStalePortScannerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"wdc-vhostscan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private void WriteConf(string name, string content)
    {
        File.WriteAllText(Path.Combine(_dir, name), content);
    }

    private static IReadOnlySet<int> Ports(params int[] ports) =>
        new HashSet<int>(ports);

    [Fact]
    public void FindStaleFiles_ReturnsEmpty_ForMissingDirectory()
    {
        var phantom = Path.Combine(Path.GetTempPath(), $"wdc-vhostscan-missing-{Guid.NewGuid():N}");
        var result = VhostStalePortScanner.FindStaleFiles(phantom, Ports(80, 443));
        Assert.Empty(result);
    }

    [Fact]
    public void FindStaleFiles_ReturnsEmpty_ForEmptyAcceptablePortsSet()
    {
        WriteConf("a.conf", "<VirtualHost *:80>\nServerName a.loc\n</VirtualHost>\n");
        var result = VhostStalePortScanner.FindStaleFiles(_dir, Ports());
        // Defensive: empty acceptable set means scanner has no ground
        // truth to compare against, so it returns nothing rather than
        // treating every port as stale.
        Assert.Empty(result);
    }

    [Fact]
    public void FindStaleFiles_ReturnsEmpty_WhenAllConfsMatchAcceptablePorts()
    {
        WriteConf("a.conf", "<VirtualHost *:80>\nServerName a.loc\n</VirtualHost>\n");
        WriteConf("b.conf", "<VirtualHost *:443>\nServerName b.loc\n</VirtualHost>\n");
        var result = VhostStalePortScanner.FindStaleFiles(_dir, Ports(80, 443));
        Assert.Empty(result);
    }

    [Fact]
    public void FindStaleFiles_DetectsSinglestaleFile()
    {
        // The exact production failure mode: site .conf written when
        // global Apache port was 8080, then Apache settings flipped to
        // 80 without regenerating per-site configs.
        WriteConf("blog.loc.conf", "<VirtualHost *:8080>\nServerName blog.loc\n</VirtualHost>\n");
        var result = VhostStalePortScanner.FindStaleFiles(_dir, Ports(80, 443));
        Assert.Single(result);
        Assert.Contains("blog.loc.conf", result);
    }

    [Fact]
    public void FindStaleFiles_DetectsMixedStaleAndFreshFiles()
    {
        // Real-world scenario: one site recently regenerated (eshop.loc
        // on port 80) while 17 others still reference the old port 8080.
        WriteConf("eshop.loc.conf", "<VirtualHost *:80>\nServerName eshop.loc\n</VirtualHost>\n");
        WriteConf("blog.loc.conf", "<VirtualHost *:8080>\nServerName blog.loc\n</VirtualHost>\n");
        WriteConf("chatujme.loc.conf", "<VirtualHost *:8080>\nServerName chatujme.loc\n</VirtualHost>\n");
        var result = VhostStalePortScanner.FindStaleFiles(_dir, Ports(80, 443));
        Assert.Equal(2, result.Count);
        Assert.Contains("blog.loc.conf", result);
        Assert.Contains("chatujme.loc.conf", result);
        Assert.DoesNotContain("eshop.loc.conf", result);
    }

    [Fact]
    public void FindStaleFiles_ReportsFileOnce_EvenWithMultipleVirtualHosts()
    {
        // Sites with both HTTP + HTTPS vhosts produce TWO matches per
        // file. Should still appear once in the result.
        WriteConf("dual.loc.conf",
            "<VirtualHost *:8080>\nServerName dual.loc\n</VirtualHost>\n" +
            "<VirtualHost *:8443>\nServerName dual.loc\nSSLEngine on\n</VirtualHost>\n");
        var result = VhostStalePortScanner.FindStaleFiles(_dir, Ports(80, 443));
        Assert.Single(result);
        Assert.Contains("dual.loc.conf", result);
    }

    [Fact]
    public void FindStaleFiles_KeepsFileWhenAtLeastOnePortMatches()
    {
        // EDGE CASE: a .conf with an HTTP block on the correct port but
        // an HTTPS block on a stale port should STILL be flagged as
        // stale (the HTTPS half is broken).
        WriteConf("partial.conf",
            "<VirtualHost *:80>\nServerName partial.loc\n</VirtualHost>\n" +
            "<VirtualHost *:8443>\nServerName partial.loc\nSSLEngine on\n</VirtualHost>\n");
        var result = VhostStalePortScanner.FindStaleFiles(_dir, Ports(80, 443));
        Assert.Single(result);
        Assert.Contains("partial.conf", result);
    }

    [Fact]
    public void FindStaleFiles_IgnoresFilesWithNoVirtualHostDirective()
    {
        // Apache include files (mod_security rules, custom snippets, etc.)
        // can sit in sites-enabled without VirtualHost blocks. Skip them.
        WriteConf("modsec.conf", "SecRuleEngine On\nSecRequestBodyAccess On\n");
        var result = VhostStalePortScanner.FindStaleFiles(_dir, Ports(80, 443));
        Assert.Empty(result);
    }

    [Fact]
    public void FindStaleFiles_IgnoresNonConfExtensions()
    {
        // .bak / .disabled / etc. files should NOT be scanned even if
        // they contain VirtualHost directives — Apache won't load them.
        File.WriteAllText(Path.Combine(_dir, "blog.loc.conf.bak"),
            "<VirtualHost *:8080>\n</VirtualHost>\n");
        File.WriteAllText(Path.Combine(_dir, "blog.loc.disabled"),
            "<VirtualHost *:8080>\n</VirtualHost>\n");
        var result = VhostStalePortScanner.FindStaleFiles(_dir, Ports(80, 443));
        Assert.Empty(result);
    }

    [Fact]
    public void FindStaleFiles_HandlesCaseInsensitiveVirtualHostKeyword()
    {
        // Apache config is case-insensitive; the regex uses IgnoreCase.
        WriteConf("loud.conf", "<VIRTUALHOST *:8080>\n</VIRTUALHOST>\n");
        WriteConf("quiet.conf", "<virtualhost *:8080>\n</virtualhost>\n");
        var result = VhostStalePortScanner.FindStaleFiles(_dir, Ports(80, 443));
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void FindStaleFiles_HandlesExtraWhitespaceInDirective()
    {
        // <VirtualHost   *:80   > — varied whitespace shouldn't break
        // the regex since it uses \s+ between keyword and host:port.
        WriteConf("spaced.conf", "<VirtualHost  *:8080  >\n</VirtualHost>\n");
        var result = VhostStalePortScanner.FindStaleFiles(_dir, Ports(80, 443));
        Assert.Single(result);
    }
}
