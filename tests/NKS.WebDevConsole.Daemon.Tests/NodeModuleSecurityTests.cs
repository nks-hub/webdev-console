using Microsoft.Extensions.Logging.Abstractions;
using NKS.WebDevConsole.Core.Models;
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
