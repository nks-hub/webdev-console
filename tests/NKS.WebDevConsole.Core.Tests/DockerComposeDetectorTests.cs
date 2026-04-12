using NKS.WebDevConsole.Core.Services;

namespace NKS.WebDevConsole.Core.Tests;

/// <summary>
/// Protects <see cref="DockerComposeDetector"/> against regressions.
/// This is the foothold for the Phase 11 "Docker Compose integration"
/// roadmap item — the frontend shows a Compose badge based on this
/// detection, and future iterations will wire up lifecycle control.
/// </summary>
public sealed class DockerComposeDetectorTests : IDisposable
{
    private readonly string _tempRoot;

    public DockerComposeDetectorTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"nks-wdc-compose-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void FindComposeFile_ReturnsNull_WhenDirectoryMissing()
    {
        var missing = Path.Combine(_tempRoot, "does-not-exist");
        Assert.Null(DockerComposeDetector.FindComposeFile(missing));
    }

    [Fact]
    public void FindComposeFile_ReturnsNull_WhenDirectoryEmpty()
    {
        Assert.Null(DockerComposeDetector.FindComposeFile(_tempRoot));
    }

    [Fact]
    public void FindComposeFile_ReturnsNull_ForNullInput()
    {
        Assert.Null(DockerComposeDetector.FindComposeFile(null));
    }

    [Fact]
    public void FindComposeFile_ReturnsNull_ForEmptyInput()
    {
        Assert.Null(DockerComposeDetector.FindComposeFile(""));
    }

    [Theory]
    [InlineData("compose.yaml")]
    [InlineData("compose.yml")]
    [InlineData("docker-compose.yaml")]
    [InlineData("docker-compose.yml")]
    public void FindComposeFile_DetectsEachCanonicalName(string fileName)
    {
        var path = Path.Combine(_tempRoot, fileName);
        File.WriteAllText(path, "services: {}\n");

        var detected = DockerComposeDetector.FindComposeFile(_tempRoot);
        Assert.Equal(path, detected);
        Assert.True(DockerComposeDetector.HasCompose(_tempRoot));
    }

    [Fact]
    public void FindComposeFile_PrefersModernCanonicalNameOverLegacy()
    {
        // When both compose.yaml and docker-compose.yml exist, the modern
        // canonical form wins — matches Compose v2's own resolution order.
        File.WriteAllText(Path.Combine(_tempRoot, "compose.yaml"), "");
        File.WriteAllText(Path.Combine(_tempRoot, "docker-compose.yml"), "");

        var detected = DockerComposeDetector.FindComposeFile(_tempRoot);
        Assert.Equal(Path.Combine(_tempRoot, "compose.yaml"), detected);
    }

    [Fact]
    public void HasCompose_ReturnsFalse_ForUnrelatedFiles()
    {
        File.WriteAllText(Path.Combine(_tempRoot, "README.md"), "");
        File.WriteAllText(Path.Combine(_tempRoot, "package.json"), "{}");
        File.WriteAllText(Path.Combine(_tempRoot, "composer.json"), "{}");
        Assert.False(DockerComposeDetector.HasCompose(_tempRoot));
    }

    [Fact]
    public void ComposeFileNames_ListsExpectedCandidates()
    {
        Assert.Equal(4, DockerComposeDetector.ComposeFileNames.Length);
        Assert.Contains("compose.yaml", DockerComposeDetector.ComposeFileNames);
        Assert.Contains("docker-compose.yml", DockerComposeDetector.ComposeFileNames);
    }
}
