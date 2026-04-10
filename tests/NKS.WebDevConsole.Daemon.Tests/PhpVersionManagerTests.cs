using Microsoft.Extensions.Logging;
using Moq;
using NKS.WebDevConsole.Plugin.PHP;
using Xunit.Abstractions;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Integration tests for PHP version detection.
/// These tests scan the actual MAMP installation on the machine.
/// </summary>
public class PhpVersionManagerTests
{
    private readonly ITestOutputHelper _output;
    private readonly PhpVersionManager _sut;

    public PhpVersionManagerTests(ITestOutputHelper output)
    {
        _output = output;
        var logger = new Mock<ILogger<PhpVersionManager>>();
        _sut = new PhpVersionManager(logger.Object);
    }

    [Fact]
    public async Task DetectAllAsync_FindsMampVersions()
    {
        if (!Directory.Exists(@"C:\MAMP")) return; // Skip on machines without MAMP

        // Act
        var installations = await _sut.DetectAllAsync(AppContext.BaseDirectory);

        // Assert — MAMP is present on this machine so we expect at least 1
        Assert.NotEmpty(installations);

        _output.WriteLine($"Detected {installations.Count} PHP version(s):");
        _output.WriteLine(new string('-', 80));
        foreach (var php in installations)
        {
            _output.WriteLine($"  PHP {php.Version} ({php.MajorMinor})");
            _output.WriteLine($"    exe:  {php.ExecutablePath}");
            _output.WriteLine($"    cgi:  {php.FpmExecutable ?? "(none)"}");
            _output.WriteLine($"    dir:  {php.Directory}");
            _output.WriteLine($"    port: {php.FcgiPort}");
            _output.WriteLine($"    ext:  {php.Extensions.Length} ({string.Join(", ", php.Extensions.Take(10))}{(php.Extensions.Length > 10 ? "..." : "")})");
            _output.WriteLine("");
        }
        _output.WriteLine(new string('-', 80));
        _output.WriteLine($"Active version: {_sut.ActiveVersion}");
    }

    [Fact]
    public async Task DetectAllAsync_VersionsAreSortedDescending()
    {
        if (!Directory.Exists(@"C:\MAMP")) return; // Skip on machines without MAMP

        var installations = await _sut.DetectAllAsync(AppContext.BaseDirectory);

        for (int i = 1; i < installations.Count; i++)
        {
            Assert.True(
                string.Compare(installations[i - 1].MajorMinor, installations[i].MajorMinor, StringComparison.Ordinal) >= 0,
                $"Versions not sorted: {installations[i - 1].MajorMinor} should be >= {installations[i].MajorMinor}");
        }
    }

    [Fact]
    public async Task DetectAllAsync_EachVersionHasValidFields()
    {
        if (!Directory.Exists(@"C:\MAMP")) return; // Skip on machines without MAMP

        var installations = await _sut.DetectAllAsync(AppContext.BaseDirectory);

        foreach (var php in installations)
        {
            Assert.Matches(@"^\d+\.\d+\.\d+", php.Version);
            Assert.Matches(@"^\d+\.\d+$", php.MajorMinor);
            Assert.True(File.Exists(php.ExecutablePath), $"php.exe not found: {php.ExecutablePath}");
            Assert.True(Directory.Exists(php.Directory), $"Directory not found: {php.Directory}");
            Assert.True(php.FcgiPort > 0, $"Invalid port: {php.FcgiPort}");
            Assert.NotNull(php.Extensions);
        }
    }

    [Fact]
    public async Task DetectAllAsync_MampVersionsHavePhpCgi()
    {
        if (!Directory.Exists(@"C:\MAMP")) return; // Skip on machines without MAMP

        var installations = await _sut.DetectAllAsync(AppContext.BaseDirectory);
        var mampInstalls = installations
            .Where(p => p.Directory.Contains("MAMP", StringComparison.OrdinalIgnoreCase))
            .ToList();

        _output.WriteLine($"MAMP installations: {mampInstalls.Count}");

        foreach (var php in mampInstalls)
        {
            Assert.NotNull(php.FpmExecutable);
            Assert.True(File.Exists(php.FpmExecutable), $"php-cgi.exe not found: {php.FpmExecutable}");
            _output.WriteLine($"  PHP {php.Version}: cgi={php.FpmExecutable}");
        }
    }

    [Fact]
    public async Task DetectAllAsync_MampVersionsHaveExtensions()
    {
        if (!Directory.Exists(@"C:\MAMP")) return; // Skip on machines without MAMP

        var installations = await _sut.DetectAllAsync(AppContext.BaseDirectory);
        var mampInstalls = installations
            .Where(p => p.Directory.Contains("MAMP", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var php in mampInstalls)
        {
            Assert.True(php.Extensions.Length > 0,
                $"PHP {php.Version} at {php.Directory} should have extensions");
            _output.WriteLine($"  PHP {php.Version}: {php.Extensions.Length} extensions");
        }
    }

    [Fact]
    public async Task DetectAllAsync_SetsActiveVersionToHighest()
    {
        if (!Directory.Exists(@"C:\MAMP")) return; // Skip on machines without MAMP

        var installations = await _sut.DetectAllAsync(AppContext.BaseDirectory);
        Assert.NotNull(_sut.ActiveVersion);

        if (installations.Count > 0)
            Assert.Equal(installations[0].MajorMinor, _sut.ActiveVersion);
    }

    [Fact]
    public void SetActiveVersion_ValidVersion_Updates()
    {
        _sut.SetActiveVersion("8.2");
        Assert.Equal("8.2", _sut.ActiveVersion);
    }

    [Fact]
    public void SetActiveVersion_InvalidVersion_Throws()
    {
        Assert.Throws<ArgumentException>(() => _sut.SetActiveVersion("99.99"));
    }
}
