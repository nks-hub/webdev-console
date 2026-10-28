using System.Text.Json;
using Microsoft.AspNetCore.Http;
using NKS.WebDevConsole.Daemon.Sites;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// The site page asks for outdated packages on its own, so "nothing to check" and "no Composer" must
/// be empty answers, not a 500.
/// </summary>
public sealed class ComposerOutdatedTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"nks-wdc-composer-outdated-{Guid.NewGuid():N}");

    public ComposerOutdatedTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort */ }
    }

    private void Project() => File.WriteAllText(Path.Combine(_dir, "composer.json"), "{}");

    [Fact]
    public void NoComposerJson_HasNothingToCheck_EvenWithComposerInstalled()
    {
        Assert.True(ComposerEndpoints.OutdatedHasNothingToCheck(_dir, composerAvailable: true));
    }

    [Fact]
    public void ProjectWithoutComposer_HasNothingToCheck()
    {
        Project();

        Assert.True(ComposerEndpoints.OutdatedHasNothingToCheck(_dir, composerAvailable: false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public void ProjectWithComposerOrAnUnknownInvoker_IsChecked(bool? available)
    {
        Project();

        Assert.False(ComposerEndpoints.OutdatedHasNothingToCheck(_dir, available));
    }

    private sealed class FakeConfig { public string? ExecutablePath { get; set; } }
    private sealed class FakeInvoker(string? path) { private readonly FakeConfig _config = new() { ExecutablePath = path }; public FakeConfig Config => _config; }
    private sealed class OldInvoker { }

    [Fact]
    public void ComposerAvailable_FollowsTheExecutablePathOfTheInvokersConfig()
    {
        Assert.True(ComposerEndpoints.ComposerAvailable(new FakeInvoker(@"C:\wdc\composer.phar")));
        Assert.False(ComposerEndpoints.ComposerAvailable(new FakeInvoker(null)));
        Assert.False(ComposerEndpoints.ComposerAvailable(new FakeInvoker("  ")));
    }

    [Fact]
    public void ComposerAvailable_BareFallbackName_CountsOnlyWhenItIsOnPath()
    {
        // The plugin's fallback when no composer.phar is managed; a clean machine has nothing by this name.
        Assert.False(ComposerEndpoints.ComposerAvailable(new FakeInvoker($"composer-{Guid.NewGuid():N}.bat")));

        File.WriteAllText(Path.Combine(_dir, "composer.bat"), "@echo off");
        Assert.True(ComposerEndpoints.OnPath("composer.bat", $"C:\\nowhere{Path.PathSeparator}{_dir}"));
        Assert.False(ComposerEndpoints.OnPath("composer.bat", "C:\\nowhere"));
        Assert.False(ComposerEndpoints.OnPath("composer.bat", null));
    }

    [Fact]
    public void ComposerAvailable_IsUnknownWhenTheInvokerHidesItsConfig()
    {
        Assert.Null(ComposerEndpoints.ComposerAvailable(new OldInvoker()));
    }

    // ── Actions without Composer ─────────────────────────────────────────────

    [Fact]
    public void ComposerNotInstalled_WithoutAnExecutable_Is409WithAMachineReadableError()
    {
        var result = ComposerEndpoints.ComposerNotInstalled(new FakeInvoker(null));

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(409, status.StatusCode);
        var body = JsonSerializer.Serialize(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
        Assert.Contains("\"error\":\"composer_not_installed\"", body);
        Assert.Contains("\"code\":\"composer_not_installed\"", body);
    }

    [Fact]
    public void ComposerNotInstalled_WithComposer_OrWhenUnknown_LetsTheActionRun()
    {
        Assert.Null(ComposerEndpoints.ComposerNotInstalled(new FakeInvoker(@"C:\wdc\composer.phar")));
        Assert.Null(ComposerEndpoints.ComposerNotInstalled(new OldInvoker()));
    }
}
