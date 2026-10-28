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
    public void ComposerAvailable_IsUnknownWhenTheInvokerHidesItsConfig()
    {
        Assert.Null(ComposerEndpoints.ComposerAvailable(new OldInvoker()));
    }
}
