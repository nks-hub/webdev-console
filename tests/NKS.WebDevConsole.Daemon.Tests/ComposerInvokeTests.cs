using NKS.WebDevConsole.Daemon.Sites;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// The daemon calls the Composer plugin by reflection. The published plugin
/// has no per-site PHP parameter, and passing one used to fail every call
/// with "Parameter count mismatch".
/// </summary>
public sealed class ComposerInvokeTests
{
    public sealed record FakeResult(int ExitCode, string Stdout, string Stderr);

    /// <summary>Same shape as ComposerInvoker in the published 1.0.x plugin.</summary>
    public sealed class PublishedInvoker
    {
        public List<string> Calls { get; } = [];

        public Task<FakeResult> InstallAsync(string siteRoot, CancellationToken ct = default)
        {
            Calls.Add($"install {siteRoot}");
            return Task.FromResult(new FakeResult(0, "ok", ""));
        }

        public Task<FakeResult> RunAsync(string siteRoot, IReadOnlyList<string> argv, CancellationToken ct = default)
        {
            Calls.Add($"run {siteRoot} {string.Join(' ', argv)}");
            return Task.FromResult(new FakeResult(0, "Composer version 2.8.12", ""));
        }
    }

    /// <summary>A build that accepts the per-site PHP path.</summary>
    public sealed class OverrideInvoker
    {
        public string? LastPhp { get; private set; }

        public Task<FakeResult> RunAsync(string siteRoot, IReadOnlyList<string> argv, string? phpPathOverride, CancellationToken ct = default)
        {
            LastPhp = phpPathOverride;
            return Task.FromResult(new FakeResult(3, "", "boom"));
        }
    }

    [Fact]
    public async Task PublishedPlugin_RunsWithoutThePhpOverride()
    {
        var invoker = new PublishedInvoker();

        var (ok, code, stdout, _) = await ComposerEndpoints.InvokeComposerAsync(invoker, "RunAsync",
            ["C:/site", new[] { "--version" }, "C:/php/8.4/php.exe", CancellationToken.None]);
        await ComposerEndpoints.InvokeComposerAsync(invoker, "InstallAsync", ["C:/site", null, CancellationToken.None]);

        Assert.True(ok);
        Assert.Equal(0, code);
        Assert.Equal("Composer version 2.8.12", stdout);
        Assert.Equal(["run C:/site --version", "install C:/site"], invoker.Calls);
    }

    [Fact]
    public async Task PluginWithOverride_GetsThePhpPath()
    {
        var invoker = new OverrideInvoker();

        var (ok, code, _, stderr) = await ComposerEndpoints.InvokeComposerAsync(invoker, "RunAsync",
            ["C:/site", new[] { "diagnose" }, "C:/php/7.4/php.exe", CancellationToken.None]);

        Assert.False(ok);
        Assert.Equal(3, code);
        Assert.Equal("boom", stderr);
        Assert.Equal("C:/php/7.4/php.exe", invoker.LastPhp);
    }
}
