using System.IO.Compression;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

using NKS.WebDevConsole.Core.Models;

namespace NKS.WebDevConsole.Daemon.Binaries;

/// <summary>
/// Downloads a binary release from its source URL into a destination folder.
/// Supports zip extraction. Reports progress via callback.
/// </summary>
public sealed class BinaryDownloader
{
    private readonly HttpClient _http;
    private readonly ILogger<BinaryDownloader> _logger;

    public BinaryDownloader(IHttpClientFactory httpClientFactory, ILogger<BinaryDownloader> logger)
    {
        _http = httpClientFactory.CreateClient("binary-downloader");
        _http.Timeout = TimeSpan.FromMinutes(15); // big binaries
        _logger = logger;
    }

    public async Task<string> DownloadAsync(
        BinaryRelease release,
        string destinationDir,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(destinationDir);
        var fileName = Path.GetFileName(new Uri(release.Url).AbsolutePath);
        if (string.IsNullOrEmpty(fileName)) fileName = $"{release.App}-{release.Version}.{release.ArchiveType}";
        var archivePath = Path.Combine(destinationDir, fileName);

        if (File.Exists(archivePath))
        {
            _logger.LogInformation("Archive already cached: {Path}", archivePath);
            return archivePath;
        }

        _logger.LogInformation("Downloading {App} {Version} from {Url}", release.App, release.Version, release.Url);

        using var req = new HttpRequestMessage(HttpMethod.Get, release.Url);
        if (!string.IsNullOrEmpty(release.UserAgent))
            req.Headers.UserAgent.ParseAdd(release.UserAgent);
        else
            req.Headers.UserAgent.Add(new ProductInfoHeaderValue("NKS-WebDevConsole", "1.0"));

        using var response = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1;
        var tempPath = archivePath + ".tmp";

        await using (var contentStream = await response.Content.ReadAsStreamAsync(ct))
        await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920];
            long downloaded = 0;
            int read;
            while ((read = await contentStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                downloaded += read;
                progress?.Report(new DownloadProgress(release.App, release.Version, downloaded, totalBytes));
            }
        }

        File.Move(tempPath, archivePath, overwrite: true);
        _logger.LogInformation("Downloaded {App} {Version} ({Size} bytes)", release.App, release.Version, new FileInfo(archivePath).Length);
        return archivePath;
    }

    public async Task<string> ExtractAsync(
        string archivePath,
        string destinationDir,
        CancellationToken ct = default)
    {
        if (!File.Exists(archivePath))
            throw new FileNotFoundException("Archive not found", archivePath);

        Directory.CreateDirectory(destinationDir);
        _logger.LogInformation("Extracting {Archive} to {Dir}", Path.GetFileName(archivePath), destinationDir);

        var ext = Path.GetExtension(archivePath).ToLowerInvariant();
        if (ext != ".zip")
            throw new NotSupportedException($"Archive format not supported: {ext}");

        await Task.Run(() => ZipFile.ExtractToDirectory(archivePath, destinationDir, overwriteFiles: true), ct);
        _logger.LogInformation("Extraction complete: {Dir}", destinationDir);
        return destinationDir;
    }
}

public sealed record DownloadProgress(string App, string Version, long Downloaded, long Total)
{
    public double PercentComplete => Total > 0 ? (double)Downloaded / Total * 100 : 0;
}
