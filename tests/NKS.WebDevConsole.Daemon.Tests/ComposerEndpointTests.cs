using System.Text.Json;
using Moq;
using NKS.WebDevConsole.Plugin.Composer;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Unit tests for the Composer REST endpoint helpers.
/// Tests validate package-name validation regex, composer.json parsing logic,
/// and error response shapes — without standing up a real HTTP server.
/// </summary>
public sealed class ComposerEndpointTests
{
    // ── Package name validation ────────────────────────────────────────────────

    [Theory]
    [InlineData("vendor/package", true)]
    [InlineData("nette/application:^3.2", true)]
    [InlineData("symfony/console:~6.0", true)]
    [InlineData("laravel/framework:*", true)]
    [InlineData("php-http/client-common:@stable", true)]
    [InlineData("my_package/foo.bar:1.0.0", true)]
    [InlineData("", false)]
    [InlineData("vendor/package; rm -rf /", false)]
    [InlineData("vendor/package && evil", false)]
    [InlineData("vendor/package|pipe", false)]
    [InlineData("vendor/package`cmd`", false)]
    public void PackageNameRegex_AcceptsValidRejectsInvalid(string package, bool expected)
    {
        var isValid = System.Text.RegularExpressions.Regex.IsMatch(
            package, @"^[A-Za-z0-9/_.:\-\^~*@]+$");
        Assert.Equal(expected, isValid);
    }

    // ── composer.json parsing ──────────────────────────────────────────────────

    [Fact]
    public void ParseComposerJson_ExtractsPackagesAndPhpVersion()
    {
        const string composerJson = """
            {
              "name": "myvendor/myapp",
              "require": {
                "php": "^8.1",
                "nette/application": "^3.2",
                "nette/database": "^3.1"
              }
            }
            """;

        using var doc = JsonDocument.Parse(composerJson);
        var packages = new List<string>();
        string? phpVersion = null;

        if (doc.RootElement.TryGetProperty("require", out var require))
        {
            foreach (var pkg in require.EnumerateObject())
                if (!pkg.Name.Equals("php", StringComparison.OrdinalIgnoreCase))
                    packages.Add($"{pkg.Name}:{pkg.Value.GetString() ?? "*"}");
            if (require.TryGetProperty("php", out var phpConstraint))
                phpVersion = phpConstraint.GetString();
        }

        Assert.Equal("^8.1", phpVersion);
        Assert.Equal(2, packages.Count);
        Assert.Contains("nette/application:^3.2", packages);
        Assert.Contains("nette/database:^3.1", packages);
        Assert.DoesNotContain(packages, p => p.StartsWith("php:"));
    }

    [Fact]
    public void ParseComposerJson_NoRequireSection_ReturnsEmpty()
    {
        const string composerJson = """{ "name": "myvendor/myapp" }""";
        using var doc = JsonDocument.Parse(composerJson);
        var packages = new List<string>();
        string? phpVersion = null;

        if (doc.RootElement.TryGetProperty("require", out var require))
        {
            foreach (var pkg in require.EnumerateObject())
                if (!pkg.Name.Equals("php", StringComparison.OrdinalIgnoreCase))
                    packages.Add(pkg.Name);
            if (require.TryGetProperty("php", out var php))
                phpVersion = php.GetString();
        }

        Assert.Empty(packages);
        Assert.Null(phpVersion);
    }

    [Fact]
    public void ParseComposerJson_PhpOnlyRequire_PackagesListEmpty()
    {
        const string composerJson = """{ "require": { "php": ">=8.0" } }""";
        using var doc = JsonDocument.Parse(composerJson);
        var packages = new List<string>();
        string? phpVersion = null;

        if (doc.RootElement.TryGetProperty("require", out var require))
        {
            foreach (var pkg in require.EnumerateObject())
                if (!pkg.Name.Equals("php", StringComparison.OrdinalIgnoreCase))
                    packages.Add(pkg.Name);
            if (require.TryGetProperty("php", out var php))
                phpVersion = php.GetString();
        }

        Assert.Empty(packages);
        Assert.Equal(">=8.0", phpVersion);
    }

    // ── ComposerInvoker wiring (smoke tests via mock) ─────────────────────────

    [Fact]
    public async Task InstallAsync_ThroughMock_ReturnsExpectedShape()
    {
        var config = new ComposerConfig { ExecutablePath = "composer", PhpPath = "php" };
        var runner = new Mock<IComposerProcessRunner>(MockBehavior.Strict);
        runner
            .Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComposerCommandResult(0, "Installing packages...", ""));

        var invoker = new ComposerInvoker(config, runner.Object);
        var result = await invoker.InstallAsync(@"C:\sites\myapp");

        Assert.Equal(0, result.ExitCode);
        Assert.False(string.IsNullOrEmpty(result.Stdout));
        Assert.Empty(result.Stderr);
    }

    [Fact]
    public async Task RequireAsync_ThroughMock_PackageInArgv()
    {
        const string pkg = "nette/application:^3.2";
        const string siteRoot = @"C:\sites\myapp";

        var config = new ComposerConfig { ExecutablePath = "composer", PhpPath = "php" };
        var runner = new Mock<IComposerProcessRunner>(MockBehavior.Strict);

        IReadOnlyList<string>? captured = null;
        runner
            .Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
                siteRoot, It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<string>, string, CancellationToken>((_, args, _, _) => captured = args)
            .ReturnsAsync(new ComposerCommandResult(0, "", ""));

        var invoker = new ComposerInvoker(config, runner.Object);
        await invoker.RequireAsync(siteRoot, pkg);

        Assert.NotNull(captured);
        Assert.Contains("require", captured!);
        Assert.Contains(pkg, captured!);
    }
}
