using NKS.WebDevConsole.Daemon.Hosts;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Pure-string tests for hosts parsing and the apply rewrite. Nothing here
/// touches a real hosts file.
/// </summary>
public sealed class HostsApplyContentTests
{
    private const string StockWindowsHosts =
        "# Copyright (c) 1993-2009 Microsoft Corp.\r\n" +
        "#\r\n" +
        "#      102.54.94.97     rhino.acme.com          # source server\r\n" +
        "#       38.25.63.10     x.acme.com              # x client host\r\n" +
        "\r\n" +
        "# localhost name resolution is handled within DNS itself.\r\n" +
        "#\t127.0.0.1       localhost\r\n" +
        "#\t::1             localhost\r\n" +
        "10.0.0.7 intranet.loc api.intranet.loc\r\n" +
        "\r\n" +
        "# BEGIN NKS WebDev Console\r\n" +
        "127.0.0.1\tshop.loc\r\n" +
        "# 127.0.0.1\told.loc\r\n" +
        "# END NKS WebDev Console\r\n";

    [Fact]
    public void Parse_IgnoresCommentedSampleLinesOutsideTheWdcBlock()
    {
        var entries = HostsEndpoints.ParseHostsEntries(StockWindowsHosts);

        Assert.DoesNotContain(entries, e => e.Hostname.EndsWith("acme.com"));
        Assert.DoesNotContain(entries, e => e.Hostname == "localhost");
        Assert.Equal(new[] { "intranet.loc", "api.intranet.loc" },
            entries.Where(e => e.Source == "custom").Select(e => e.Hostname));
        var wdc = entries.Where(e => e.Source == "wdc").ToList();
        Assert.Equal(2, wdc.Count);
        Assert.False(wdc.Single(e => e.Hostname == "old.loc").Enabled);
    }

    private static List<HostsApplyEntry> RoundTrip(string content) =>
        HostsEndpoints.ParseHostsEntries(content)
            .Select(e => new HostsApplyEntry(e.Enabled, e.Ip, e.Hostname, e.Source, e.Comment, e.LineNumber))
            .ToList();

    [Fact]
    public void Apply_EditedExistingRow_IsReportedNotAppendedAsADuplicate()
    {
        var entries = RoundTrip(StockWindowsHosts);
        var i = entries.FindIndex(e => e.Hostname == "intranet.loc");
        entries[i] = entries[i] with { Ip = "10.0.0.8" };

        var (content, added, notApplied) = HostsEndpoints.BuildAppliedContent(StockWindowsHosts, entries);

        Assert.Equal(0, added);
        Assert.Equal(2, notApplied); // the untouched old line plus the edit that was not written
        Assert.DoesNotContain("10.0.0.8", content);
        Assert.Single(content.Split('\n'), l => l.Contains("intranet.loc api.intranet.loc"));
    }

    [Fact]
    public void Apply_NewRow_IsAppendedOnceAndAgainIsANoOp()
    {
        var entries = RoundTrip(StockWindowsHosts);
        entries.Add(new HostsApplyEntry(true, "127.0.0.1", "tool.loc", "custom", null));

        var (first, added, notApplied) = HostsEndpoints.BuildAppliedContent(StockWindowsHosts, entries);
        var (second, addedAgain, _) = HostsEndpoints.BuildAppliedContent(first, RoundTrip(first));

        Assert.Equal(1, added);
        Assert.Equal(0, notApplied);
        Assert.Equal(0, addedAgain);
        Assert.Single(second.Split('\n'), l => l.Contains("tool.loc"));
        Assert.Contains("rhino.acme.com", second); // stock comments survive untouched
    }

    [Fact]
    public void Apply_RewritesOnlyTheWdcBlock()
    {
        var entries = RoundTrip(StockWindowsHosts)
            .Select(e => e.Hostname == "shop.loc" ? e with { Enabled = false } : e)
            .ToList();

        var (content, _, notApplied) = HostsEndpoints.BuildAppliedContent(StockWindowsHosts, entries);

        Assert.Equal(0, notApplied);
        Assert.Contains("# 127.0.0.1\tshop.loc", content);
        Assert.Single(content.Split('\n'), l => l.Contains("# BEGIN NKS WebDev Console"));
    }
}
