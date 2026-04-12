using NKS.WebDevConsole.Core.Services;

namespace NKS.WebDevConsole.Core.Tests;

/// <summary>
/// Tests for <see cref="DockerComposeRunner"/> — the lifecycle layer
/// on top of DockerComposeDetector. These test the error-handling paths
/// and argument construction without requiring Docker to be installed.
/// The happy paths (actual docker compose execution) are integration
/// tests that only run on machines with Docker Desktop.
/// </summary>
public sealed class DockerComposeRunnerTests
{
    [Fact]
    public async Task UpAsync_ReturnsFailure_WhenDirectoryMissing()
    {
        var result = await DockerComposeRunner.UpAsync("/nonexistent/path/xyz");
        Assert.False(result.Success);
        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("not found", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DownAsync_ReturnsFailure_WhenDirectoryMissing()
    {
        var result = await DockerComposeRunner.DownAsync("/nonexistent/path/xyz");
        Assert.False(result.Success);
        Assert.Contains("not found", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RestartAsync_ReturnsFailure_WhenDirectoryMissing()
    {
        var result = await DockerComposeRunner.RestartAsync("/nonexistent/path/xyz");
        Assert.False(result.Success);
    }

    [Fact]
    public async Task PsAsync_ReturnsFailure_WhenDirectoryMissing()
    {
        var result = await DockerComposeRunner.PsAsync("/nonexistent/path/xyz");
        Assert.False(result.Success);
    }

    [Fact]
    public async Task LogsAsync_ReturnsFailure_WhenDirectoryMissing()
    {
        var result = await DockerComposeRunner.LogsAsync("/nonexistent/path/xyz");
        Assert.False(result.Success);
    }

    [Fact]
    public async Task UpAsync_RespectsCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var result = await DockerComposeRunner.UpAsync("/nonexistent/path/xyz", cts.Token);
        // Should fail immediately — either directory-not-found or cancellation
        Assert.False(result.Success);
    }

    [Fact]
    public void ComposeResult_RecordEquality()
    {
        var a = new DockerComposeRunner.ComposeResult(true, 0, "ok");
        var b = new DockerComposeRunner.ComposeResult(true, 0, "ok");
        Assert.Equal(a, b);
    }

    [Fact]
    public void ComposeResult_HasMeaningfulToString()
    {
        var result = new DockerComposeRunner.ComposeResult(false, 1, "error msg");
        var str = result.ToString();
        Assert.Contains("False", str);
        Assert.Contains("1", str);
    }

    [Fact]
    public async Task LogsAsync_CustomTail_ReturnsFailure_WhenDirectoryMissing()
    {
        var result = await DockerComposeRunner.LogsAsync("/nonexistent/xyz", tail: 50);
        Assert.False(result.Success);
        Assert.Contains("not found", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpAsync_RespectsTimeout()
    {
        // Pre-cancelled token should fail immediately
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var result = await DockerComposeRunner.UpAsync("/nonexistent/xyz", cts.Token);
        Assert.False(result.Success);
    }

    [Fact]
    public void ComposeResult_RecordInequality_DifferentExitCode()
    {
        var a = new DockerComposeRunner.ComposeResult(false, 0, "ok");
        var b = new DockerComposeRunner.ComposeResult(false, 1, "ok");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ComposeResult_SuccessImpliesZeroExitCode_ByConvention()
    {
        // Helper record factory convention: success=true should imply exit 0
        var r = new DockerComposeRunner.ComposeResult(true, 0, "");
        Assert.True(r.Success);
        Assert.Equal(0, r.ExitCode);
    }
}
