using Moq;
using NKS.WebDevConsole.Plugin.Composer;
using NKS.WebDevConsole.Core.Services;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Unit tests for <see cref="ComposerInvoker"/>. All process execution is
/// intercepted via a Moq strict mock of <see cref="IComposerProcessRunner"/> so
/// no actual PHP or composer binary is required on the test host.
/// </summary>
public sealed class ComposerInvokerTests
{
    // ── Helpers ────────────────────────────────────────────────────────────────

    private static (ComposerInvoker invoker, ComposerConfig config, Mock<IComposerProcessRunner> runner)
        BuildPharInvoker(string phpPath = "php", string pharPath = "/opt/wdc/composer/2.8.0/composer.phar")
    {
        var config = new ComposerConfig
        {
            PhpPath = phpPath,
            ExecutablePath = pharPath,
        };
        var runner = new Mock<IComposerProcessRunner>(MockBehavior.Strict);
        return (new ComposerInvoker(config, runner.Object), config, runner);
    }

    private static (ComposerInvoker invoker, ComposerConfig config, Mock<IComposerProcessRunner> runner)
        BuildNativeBinaryInvoker(string composerBin = "composer")
    {
        var config = new ComposerConfig
        {
            PhpPath = "php",
            ExecutablePath = composerBin,
        };
        var runner = new Mock<IComposerProcessRunner>(MockBehavior.Strict);
        return (new ComposerInvoker(config, runner.Object), config, runner);
    }

    private static ComposerCommandResult OkResult(string stdout = "", string stderr = "")
        => new(0, stdout, stderr);

    // ── InstallAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task InstallAsync_ConstructsCorrectArgv()
    {
        var (invoker, config, runner) = BuildPharInvoker();
        const string siteRoot = @"C:\sites\myapp";

        IReadOnlyList<string>? capturedArgs = null;
        string? capturedExec = null;

        runner
            .Setup(r => r.RunAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                siteRoot,
                It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<string>, string, CancellationToken>(
                (exe, args, _, _) => { capturedExec = exe; capturedArgs = args; })
            .ReturnsAsync(OkResult());

        await invoker.InstallAsync(siteRoot);

        Assert.Equal(config.PhpPath, capturedExec);
        Assert.NotNull(capturedArgs);
        Assert.Equal(config.ExecutablePath, capturedArgs![0]);   // composer.phar as first arg
        Assert.Contains("install", capturedArgs);
        Assert.Contains("--no-interaction", capturedArgs);

        runner.VerifyAll();
    }

    [Fact]
    public async Task InstallAsync_ReturnsExitCodeAndOutput()
    {
        var (invoker, _, runner) = BuildPharInvoker();
        const string siteRoot = @"C:\sites\myapp";
        const string expectedStdout = "Installing dependencies from lock file\nNothing to install, update or remove\n";

        runner
            .Setup(r => r.RunAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                siteRoot,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComposerCommandResult(0, expectedStdout, ""));

        var result = await invoker.InstallAsync(siteRoot);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expectedStdout, result.Stdout);
        Assert.Empty(result.Stderr);
    }

    [Fact]
    public async Task InstallAsync_BubblesNonZeroExitCodeAsFailure()
    {
        var (invoker, _, runner) = BuildPharInvoker();
        const string siteRoot = @"C:\sites\myapp";
        const string errorOutput = "Your requirements could not be resolved to an installable set of packages.";

        runner
            .Setup(r => r.RunAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                siteRoot,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComposerCommandResult(2, "", errorOutput));

        var result = await invoker.InstallAsync(siteRoot);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(errorOutput, result.Stderr);
    }

    // ── RequireAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task RequireAsync_PassesPackageArgument()
    {
        var (invoker, config, runner) = BuildPharInvoker();
        const string siteRoot = @"C:\sites\myapp";
        const string package = "nette/application:^3.2";

        IReadOnlyList<string>? capturedArgs = null;

        runner
            .Setup(r => r.RunAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                siteRoot,
                It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<string>, string, CancellationToken>(
                (_, args, _, _) => capturedArgs = args)
            .ReturnsAsync(OkResult());

        await invoker.RequireAsync(siteRoot, package);

        Assert.NotNull(capturedArgs);
        Assert.Contains("require", capturedArgs!);
        Assert.Contains(package, capturedArgs!);
        Assert.Contains("--no-interaction", capturedArgs!);

        runner.VerifyAll();
    }

    // ── RunAsync ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_PassesThroughArgvVerbatim()
    {
        var (invoker, config, runner) = BuildPharInvoker();
        const string siteRoot = @"C:\sites\myapp";
        var argv = new[] { "update", "--prefer-dist", "--no-dev" };

        IReadOnlyList<string>? capturedArgs = null;

        runner
            .Setup(r => r.RunAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                siteRoot,
                It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<string>, string, CancellationToken>(
                (_, args, _, _) => capturedArgs = args)
            .ReturnsAsync(OkResult());

        await invoker.RunAsync(siteRoot, argv);

        Assert.NotNull(capturedArgs);
        // phar invocation: [composer.phar, update, --prefer-dist, --no-dev, --no-interaction]
        Assert.Contains("update", capturedArgs!);
        Assert.Contains("--prefer-dist", capturedArgs!);
        Assert.Contains("--no-dev", capturedArgs!);
        Assert.Contains("--no-interaction", capturedArgs!);

        runner.VerifyAll();
    }

    // ── Phar vs native binary dispatch ────────────────────────────────────────

    [Fact]
    public async Task InstallAsync_NativeBinary_InvokesComposerDirectly_NotPhp()
    {
        var (invoker, config, runner) = BuildNativeBinaryInvoker("composer");
        const string siteRoot = @"C:\sites\myapp";

        string? capturedExec = null;
        IReadOnlyList<string>? capturedArgs = null;

        runner
            .Setup(r => r.RunAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                siteRoot,
                It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<string>, string, CancellationToken>(
                (exe, args, _, _) => { capturedExec = exe; capturedArgs = args; })
            .ReturnsAsync(OkResult());

        await invoker.InstallAsync(siteRoot);

        // Native binary — executable must be the composer shim, NOT php
        Assert.Equal("composer", capturedExec);
        Assert.NotNull(capturedArgs);
        // phar path must NOT appear as first argument when using native binary
        Assert.DoesNotContain(".phar", capturedArgs![0]);
        Assert.Contains("install", capturedArgs!);

        runner.VerifyAll();
    }

    [Fact]
    public async Task InstallAsync_PharMode_PhpIsExecutable_PharIsFirstArg()
    {
        var (invoker, config, runner) = BuildPharInvoker("php8.3", "/home/user/.wdc/binaries/composer/2.8.0/composer.phar");
        const string siteRoot = "/var/www/mysite";

        string? capturedExec = null;
        IReadOnlyList<string>? capturedArgs = null;

        runner
            .Setup(r => r.RunAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                siteRoot,
                It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<string>, string, CancellationToken>(
                (exe, args, _, _) => { capturedExec = exe; capturedArgs = args; })
            .ReturnsAsync(OkResult());

        await invoker.InstallAsync(siteRoot);

        Assert.Equal("php8.3", capturedExec);
        Assert.NotNull(capturedArgs);
        Assert.Equal(config.ExecutablePath, capturedArgs![0]);

        runner.VerifyAll();
    }

    // ── Edge cases ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RequireAsync_WithVersionConstraint_PassesThroughVerbatim()
    {
        var (invoker, _, runner) = BuildPharInvoker();
        const string siteRoot = @"C:\sites\myapp";
        const string package = "vendor/pkg:^1.0";

        IReadOnlyList<string>? capturedArgs = null;

        runner
            .Setup(r => r.RunAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                siteRoot,
                It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<string>, string, CancellationToken>(
                (_, args, _, _) => capturedArgs = args)
            .ReturnsAsync(OkResult());

        await invoker.RequireAsync(siteRoot, package);

        Assert.NotNull(capturedArgs);
        // The package string must appear exactly as passed — no splitting on ':'
        Assert.Contains(package, capturedArgs!);
        Assert.Contains("--no-interaction", capturedArgs!);

        runner.VerifyAll();
    }

    [Fact]
    public async Task RequireAsync_NoInteractionAlwaysAppended()
    {
        // Verify --no-interaction appears after the package arg regardless of
        // how many other elements are in the argv list.
        var (invoker, _, runner) = BuildPharInvoker();
        const string siteRoot = @"C:\sites\myapp";
        const string package = "nette/application";

        IReadOnlyList<string>? capturedArgs = null;

        runner
            .Setup(r => r.RunAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                siteRoot,
                It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<string>, string, CancellationToken>(
                (_, args, _, _) => capturedArgs = args)
            .ReturnsAsync(OkResult());

        await invoker.RequireAsync(siteRoot, package);

        Assert.NotNull(capturedArgs);
        var pkgIdx = capturedArgs!.ToList().IndexOf(package);
        var niIdx  = capturedArgs!.ToList().IndexOf("--no-interaction");
        Assert.True(pkgIdx >= 0, "package arg must be present");
        Assert.True(niIdx > pkgIdx, "--no-interaction must follow the package arg");

        runner.VerifyAll();
    }

    [Fact]
    public async Task RunAsync_EmptyArgv_StillAppendsNoInteraction()
    {
        var (invoker, _, runner) = BuildPharInvoker();
        const string siteRoot = @"C:\sites\myapp";

        IReadOnlyList<string>? capturedArgs = null;

        runner
            .Setup(r => r.RunAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                siteRoot,
                It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<string>, string, CancellationToken>(
                (_, args, _, _) => capturedArgs = args)
            .ReturnsAsync(OkResult());

        await invoker.RunAsync(siteRoot, Array.Empty<string>());

        Assert.NotNull(capturedArgs);
        Assert.Contains("--no-interaction", capturedArgs!);

        runner.VerifyAll();
    }

    [Fact]
    public async Task InstallAsync_WorkingDir_MatchesSiteRoot()
    {
        var (invoker, _, runner) = BuildPharInvoker();
        const string siteRoot = @"C:\sites\myspecificapp";

        string? capturedWorkingDir = null;

        runner
            .Setup(r => r.RunAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<string>, string, CancellationToken>(
                (_, _, wd, _) => capturedWorkingDir = wd)
            .ReturnsAsync(OkResult());

        await invoker.InstallAsync(siteRoot);

        Assert.Equal(siteRoot, capturedWorkingDir);

        runner.VerifyAll();
    }
}

/// <summary>
/// Tests for <see cref="ComposerConfig.ApplyOwnBinaryDefaults"/> binary discovery
/// and PATH fallback behaviour.
/// </summary>
public sealed class ComposerConfigDefaultsTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"wdc-cfg-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    [Fact]
    public void ApplyOwnBinaryDefaults_EmptyBinariesDir_FallsBackToPath()
    {
        // Create the binaries/composer directory but leave it empty (no version
        // subdirectories, no composer.phar files).
        var binariesRoot = Path.Combine(_tempRoot, "binaries", "composer");
        Directory.CreateDirectory(binariesRoot);

        var cfg = new ComposerConfig { BinariesRoot = binariesRoot };
        var managed = cfg.ApplyOwnBinaryDefaults();

        Assert.False(managed, "Should return false when no phar is found");
        Assert.NotNull(cfg.ExecutablePath);
        // Fallback must be a PATH-resolved shim, not a .phar file
        Assert.False(cfg.ExecutablePath!.EndsWith(".phar", StringComparison.OrdinalIgnoreCase),
            "PATH fallback must not be a .phar path");
        // Must be non-empty (i.e. "composer" or "composer.bat")
        Assert.NotEmpty(cfg.ExecutablePath!);
    }

    [Fact]
    public void ApplyOwnBinaryDefaults_BinariesDirMissing_FallsBackToPath()
    {
        // BinariesRoot points to a directory that does not exist at all.
        var cfg = new ComposerConfig
        {
            BinariesRoot = Path.Combine(_tempRoot, "nonexistent", "composer")
        };
        var managed = cfg.ApplyOwnBinaryDefaults();

        Assert.False(managed);
        Assert.NotNull(cfg.ExecutablePath);
        Assert.False(cfg.ExecutablePath!.EndsWith(".phar", StringComparison.OrdinalIgnoreCase));
    }
}
