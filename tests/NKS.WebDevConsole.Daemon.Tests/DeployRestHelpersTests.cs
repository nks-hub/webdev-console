using NKS.WebDevConsole.Daemon.Deploy;
using Xunit;

namespace NKS.WebDevConsole.Daemon.Tests;

public sealed class DeployRestHelpersTests
{
    // --- MapStatusToPhase ---

    [Theory]
    [InlineData("queued", "Queued")]
    [InlineData("running", "Building")]
    [InlineData("awaiting_soak", "AwaitingSoak")]
    [InlineData("completed", "Done")]
    [InlineData("failed", "Failed")]
    [InlineData("cancelled", "Cancelled")]
    [InlineData("rolling_back", "RollingBack")]
    [InlineData("rolled_back", "RolledBack")]
    public void MapStatusToPhase_KnownStatuses_MapsToFrontendPhase(string input, string expected)
    {
        Assert.Equal(expected, DeployRestHelpers.MapStatusToPhase(input));
    }

    [Theory]
    [InlineData("RUNNING", "Building")]   // case-insensitive
    [InlineData("Completed", "Done")]
    [InlineData("AWAITING_SOAK", "AwaitingSoak")]
    public void MapStatusToPhase_CaseInsensitive(string input, string expected)
    {
        Assert.Equal(expected, DeployRestHelpers.MapStatusToPhase(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("preparing")]   // intermediate phase the daemon never writes
    public void MapStatusToPhase_UnknownOrEmpty_ReturnsUnknown(string? input)
    {
        // Conservative fallback so the frontend renders "Unknown" tag
        // instead of crashing when daemon adds a new status without
        // the frontend learning about it yet.
        Assert.Equal("Unknown", DeployRestHelpers.MapStatusToPhase(input!));
    }

    // --- SanitiseDomainForFilename ---

    [Theory]
    [InlineData("blog.loc", "blog.loc")]                 // valid as-is
    [InlineData("shop.loc", "shop.loc")]
    [InlineData("api-staging.example.com", "api-staging.example.com")]
    [InlineData("with_under_score", "with_under_score")]
    [InlineData("UPPERCASE.LOC", "UPPERCASE.LOC")]       // case preserved
    [InlineData("digit123", "digit123")]
    public void SanitiseDomainForFilename_ValidChars_PreservedAsIs(string input, string expected)
    {
        Assert.Equal(expected, DeployRestHelpers.SanitiseDomainForFilename(input));
    }

    [Theory]
    [InlineData("evil/../etc/passwd", "evil_.._etc_passwd")]
    [InlineData("a b c", "a_b_c")]                       // spaces
    [InlineData("with:colons", "with_colons")]
    [InlineData("path\\sep", "path_sep")]                // backslash
    [InlineData("question?mark", "question_mark")]
    [InlineData("$rm.loc", "_rm.loc")]                   // shell metachar
    public void SanitiseDomainForFilename_InvalidChars_ReplacedWithUnderscore(string input, string expected)
    {
        // Defence in depth — ASP.NET routing already rejects '/' in route
        // values, but if a different caller (CLI, future API) passes a
        // string with path traversal chars we don't want it escaping the
        // settings directory.
        Assert.Equal(expected, DeployRestHelpers.SanitiseDomainForFilename(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void SanitiseDomainForFilename_EmptyOrNull_ReturnsUnderscore(string? input)
    {
        // Caller should have validated, but null/empty must not produce
        // an empty filename (which would write to ".json" and collide
        // across all empty-domain calls).
        Assert.Equal("_", DeployRestHelpers.SanitiseDomainForFilename(input!));
    }
}
