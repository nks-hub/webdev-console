using Microsoft.Extensions.Logging.Abstractions;
using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Core.Services;
using NKS.WebDevConsole.Plugin.Node;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Protects the Node plugin's command-injection guardrails against
/// regressions. The whitelist + metacharacter filter was added in c06dacc
/// in response to a code-review finding — losing either check would
/// re-expose arbitrary shell execution via site configs.
/// </summary>
public sealed class NodeModuleSecurityTests
{
    [Theory]
    [InlineData("npm")]
    [InlineData("NPM")]        // case-insensitive
    [InlineData("npx")]
    [InlineData("node")]
    [InlineData("yarn")]
    [InlineData("pnpm")]
    [InlineData("bun")]
    [InlineData("deno")]
    public void AllowedExecutables_ContainsNodeEcosystemTools(string exe)
    {
        Assert.Contains(exe, NodeModule.AllowedExecutables);
    }

    [Theory]
    [InlineData("cmd")]
    [InlineData("powershell")]
    [InlineData("bash")]
    [InlineData("sh")]
    [InlineData("python")]
    [InlineData("ruby")]
    [InlineData("./malicious.sh")]
    [InlineData("")]
    public void AllowedExecutables_RejectsShellAndForeignBinaries(string exe)
    {
        Assert.DoesNotContain(exe, NodeModule.AllowedExecutables);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("run dev")]
    [InlineData("server.js")]
    [InlineData("--port=3000")]
    [InlineData("run dev --host 0.0.0.0")]
    [InlineData("")]
    [InlineData("  ")]
    public void ContainsShellMetacharacters_AllowsPlainArguments(string input)
    {
        Assert.False(NodeModule.ContainsShellMetacharacters(input));
    }

    [Theory]
    [InlineData("start & del /q *")]
    [InlineData("start && rm -rf /")]
    [InlineData("start | tee /tmp/out")]
    [InlineData("start || fallback")]
    [InlineData("start ; echo pwned")]
    [InlineData("start `whoami`")]
    [InlineData("start $(whoami)")]
    [InlineData("start > /etc/passwd")]
    [InlineData("start < /etc/passwd")]
    [InlineData("start\nrm -rf /")]
    [InlineData("start\rrm -rf /")]
    [InlineData("start\tmalicious")]
    [InlineData("run\0dev")]
    public void ContainsShellMetacharacters_BlocksShellInjection(string input)
    {
        Assert.True(NodeModule.ContainsShellMetacharacters(input),
            $"Input '{input}' should have been flagged as containing shell metacharacters.");
    }

    [Fact]
    public async Task ValidateConfigAsync_ReportsMissingExecutable()
    {
        // In a CI runner with no node installed the validator should fail
        // gracefully rather than crashing. If node IS available (dev
        // machine), the test still passes because the assertion only
        // verifies that the returned ValidationResult is well-formed.
        var module = new NodeModule(NullLogger<NodeModule>.Instance);
        var result = await module.ValidateConfigAsync(CancellationToken.None);

        Assert.NotNull(result);
        if (!result.IsValid)
        {
            Assert.NotNull(result.ErrorMessage);
            Assert.Contains("Node.js", result.ErrorMessage);
        }
    }

    [Fact]
    public async Task GetStatusAsync_EmptyWhenNoProcesses()
    {
        var module = new NodeModule(NullLogger<NodeModule>.Instance);
        var status = await module.GetStatusAsync(CancellationToken.None);

        Assert.Equal("node", status.Id);
        // State.Stopped = 0 in ServiceState enum
        Assert.Equal(0, (int)status.State);
        Assert.Null(status.Pid);
        Assert.Equal(TimeSpan.Zero, status.Uptime);
    }

    [Fact]
    public void ListSiteProcesses_EmptyByDefault()
    {
        var module = new NodeModule(NullLogger<NodeModule>.Instance);
        var list = module.ListSiteProcesses();

        Assert.NotNull(list);
        Assert.Empty(list);
    }

    [Fact]
    public void GetSiteStatus_ReturnsNullForUnknownDomain()
    {
        var module = new NodeModule(NullLogger<NodeModule>.Instance);
        var status = module.GetSiteStatus("unknown.loc");

        Assert.Null(status);
    }

    [Fact]
    public async Task StopSiteAsync_IsNoOpForUnknownDomain()
    {
        var module = new NodeModule(NullLogger<NodeModule>.Instance);
        // Must not throw when asked to stop a site that was never started.
        await module.StopSiteAsync("never-started.loc", CancellationToken.None);
    }
}

/// <summary>
/// Protects the semver-aware version comparer used by NodeModule.DetectNodeExecutable
/// to pick the highest installed Node version under ~/.wdc/binaries/node/.
/// Ordinal comparison would rank "9.0.0" > "20.5.0" because ASCII '9' > '2',
/// causing the plugin to boot a stale Node 9 alongside a fresh Node 20.
/// </summary>
public sealed class SemverVersionComparerTests
{
    [Theory]
    [InlineData("20.5.0", "9.0.0", 1)]     // 20 > 9 (numeric, not ordinal)
    [InlineData("18.17.0", "18.16.0", 1)]  // patch compare
    [InlineData("18.17.0", "18.17.1", -1)] // patch compare reverse
    [InlineData("20.0.0", "20.0.0", 0)]    // equality
    [InlineData("10.0.0", "9.99.0", 1)]    // the classic failure case
    [InlineData("1.0.0", "1.0.0-beta.1", 1)]   // stable > pre-release
    [InlineData("1.0.0-rc.2", "1.0.0-rc.1", 1)] // pre-release ordinal
    [InlineData("1.0.0-beta", "1.0.0-alpha", 1)] // pre-release ordinal
    public void CompareAscending_RanksVersionsCorrectly(string a, string b, int expectedSign)
    {
        var actual = SemverVersionComparer.CompareAscending(a, b);
        Assert.Equal(expectedSign, Math.Sign(actual));
    }

    [Fact]
    public void DescendingSort_PutsLatestFirst()
    {
        var versions = new[] { "9.0.0", "20.5.0", "18.17.0", "16.20.0", "10.24.1" };
        var sorted = versions.OrderByDescending(v => v, SemverVersionComparer.Instance).ToArray();

        Assert.Equal("20.5.0", sorted[0]);
        Assert.Equal("18.17.0", sorted[1]);
        Assert.Equal("16.20.0", sorted[2]);
        Assert.Equal("10.24.1", sorted[3]);
        Assert.Equal("9.0.0", sorted[4]);
    }

    [Fact]
    public void DescendingSort_PrefersStableOverPrerelease()
    {
        var versions = new[] { "20.5.0-rc.1", "20.5.0", "20.4.0" };
        var sorted = versions.OrderByDescending(v => v, SemverVersionComparer.Instance).ToArray();

        Assert.Equal("20.5.0", sorted[0]);      // stable wins over same-main pre-release
        Assert.Equal("20.5.0-rc.1", sorted[1]);
        Assert.Equal("20.4.0", sorted[2]);
    }
}
