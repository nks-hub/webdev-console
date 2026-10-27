using NKS.WebDevConsole.Daemon.Sites;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// The default <c>localhost</c> site created when the first-run wizard finishes must start on a real
/// PHP version: an empty one means "no PHP" to the vhost generator, and the site's page is PHP.
/// </summary>
public class OnboardingSeedTests
{
    [Fact]
    public void PickDefaultPhp_UsesTheSavedChoiceWhenInstalled()
    {
        Assert.Equal("8.3", OnboardingEndpoints.PickDefaultPhp(["8.5.12", "8.3.25"], "8.3"));
    }

    [Fact]
    public void PickDefaultPhp_AcceptsAFullVersionAsTheSavedChoice()
    {
        Assert.Equal("8.4", OnboardingEndpoints.PickDefaultPhp(["8.5.12", "8.4.20"], "8.4.7"));
    }

    [Fact]
    public void PickDefaultPhp_FallsBackToTheNewestWhenTheSavedOneIsGone()
    {
        Assert.Equal("8.5", OnboardingEndpoints.PickDefaultPhp(["8.3.25", "8.5.12", "8.4.20"], "7.4"));
        Assert.Equal("8.10", OnboardingEndpoints.PickDefaultPhp(["8.9.1", "8.10.0"], null));
    }

    [Fact]
    public void PickDefaultPhp_IsEmptyWithoutAnyInstalledPhp()
    {
        Assert.Equal("", OnboardingEndpoints.PickDefaultPhp([], "8.4"));
        Assert.Equal("", OnboardingEndpoints.PickDefaultPhp(["not-a-version"], null));
    }
}
