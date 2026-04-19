using NKS.WebDevConsole.Plugin.Composer;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Tests for <see cref="ComposerConfig.ApplyOwnBinaryDefaults"/> PHP-path detection.
/// Each test provisions its own isolated temp directory tree and cleans up after itself.
/// The PHP scan derives its root from the parent of <see cref="ComposerConfig.BinariesRoot"/>,
/// so overriding that property redirects the whole tree without touching WdcPaths.
/// </summary>
public sealed class ComposerConfigTests : IDisposable
{
    private readonly string _tempRoot;

    public ComposerConfigTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"wdc-composer-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best effort */ }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="ComposerConfig"/> whose BinariesRoot points into
    /// the test-local temp tree, redirecting both the composer and PHP scans.
    /// </summary>
    private ComposerConfig BuildConfig()
    {
        return new ComposerConfig
        {
            // BinariesRoot = {_tempRoot}/composer  →  PHP root = {_tempRoot}/php
            BinariesRoot = Path.Combine(_tempRoot, "composer"),
        };
    }

    /// <summary>
    /// Creates a versioned PHP binary under {_tempRoot}/php/{version}/.
    /// Returns the full path to the created executable file.
    /// </summary>
    private string CreatePhpExe(string version)
    {
        var vdir = Path.Combine(_tempRoot, "php", version);
        Directory.CreateDirectory(vdir);
        var exe = OperatingSystem.IsWindows()
            ? Path.Combine(vdir, "php.exe")
            : Path.Combine(vdir, "php");
        File.WriteAllText(exe, string.Empty);
        return exe;
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ApplyOwnBinaryDefaults_WhenPhpDirMissing_KeepsDefaultPhpPath()
    {
        // {_tempRoot}/php/ does NOT exist.
        var config = BuildConfig();

        config.ApplyOwnBinaryDefaults();

        Assert.Equal("php", config.PhpPath);
    }

    [Fact]
    public void ApplyOwnBinaryDefaults_WhenPhpDirHasVersions_PicksNewestSemver()
    {
        // Two installed versions; semver ordering should prefer 8.3.0 over 8.2.1.
        CreatePhpExe("8.2.1");
        var newer = CreatePhpExe("8.3.0");

        var config = BuildConfig();
        config.ApplyOwnBinaryDefaults();

        Assert.Equal(newer, config.PhpPath);
    }

    [Fact]
    public void ApplyOwnBinaryDefaults_WhenPhpDirEmpty_KeepsDefaultPhpPath()
    {
        // {_tempRoot}/php/ exists but has no version subdirectories.
        Directory.CreateDirectory(Path.Combine(_tempRoot, "php"));

        var config = BuildConfig();
        config.ApplyOwnBinaryDefaults();

        Assert.Equal("php", config.PhpPath);
    }
}
