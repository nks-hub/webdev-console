using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

using NKS.WebDevConsole.Core.Models;

using XZStream = SharpCompress.Compressors.Xz.XZStream;

namespace NKS.WebDevConsole.Daemon.Binaries;

/// <summary>
/// Downloads a binary release from its source URL into a destination folder.
/// Supports zip, tar.gz and tar.xz extraction plus single-file binaries.
/// Reports progress via callback.
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
        CancellationToken ct = default,
        string? archiveType = null)
    {
        if (!File.Exists(archivePath))
            throw new FileNotFoundException("Archive not found", archivePath);

        Directory.CreateDirectory(destinationDir);
        _logger.LogInformation("Extracting {Archive} to {Dir} (type={Type})", Path.GetFileName(archivePath), destinationDir, archiveType ?? "auto");

        var ext = Path.GetExtension(archivePath).ToLowerInvariant();
        var hint = (archiveType ?? "").ToLowerInvariant();

        // Single-file binaries (cloudflared, caddy releases that ship as a
        // bare .exe; mkcert macOS/Linux releases that ship as a bare binary
        // with no extension at all — Path.GetExtension on
        // "mkcert-v1.4.4-darwin-arm64" returns ".4-darwin-arm64", which is
        // why we also accept the explicit catalog hint "bin"/"exe").
        if (hint is "bin" or "exe" || ext is ".exe" or ".bin" or "")
        {
            var guessedName = Path.GetFileName(archivePath);
            // Re-name single-file binaries to their canonical executable name so
            // ResolveExecutable in BinaryManager can find them by looking for
            // `{app}.exe` / `{app}` in the install dir. Guess from the filename
            // stem — cloudflared-windows-amd64.exe → cloudflared.exe.
            var stem = Path.GetFileNameWithoutExtension(guessedName);
            var canonical = stem.Contains("cloudflared", StringComparison.OrdinalIgnoreCase)
                ? "cloudflared.exe"
                : guessedName;
            var dest = Path.Combine(destinationDir, canonical);
            await Task.Run(() => File.Copy(archivePath, dest, overwrite: true), ct);
            return destinationDir;
        }

        // Tar-family archives are the canonical distribution format for the
        // Linux/macOS binaries our CI produces (apache/nginx/php/redis/mailpit/
        // caddy all ship either tar.gz or tar.xz). Route before the zip path
        // since .tar.gz / .tar.xz have compound extensions that Path.GetExtension
        // only sees as .gz / .xz.
        var fileName = Path.GetFileName(archivePath).ToLowerInvariant();
        var isTarGz = hint is "tar.gz" or "tgz"
            || fileName.EndsWith(".tar.gz", StringComparison.Ordinal)
            || fileName.EndsWith(".tgz", StringComparison.Ordinal);
        var isTarXz = hint is "tar.xz" or "txz"
            || fileName.EndsWith(".tar.xz", StringComparison.Ordinal)
            || fileName.EndsWith(".txz", StringComparison.Ordinal);

        if (isTarGz || isTarXz)
        {
            _logger.LogInformation(
                "Extracting tar archive via {Path}: {Archive}",
                isTarGz ? "System.Formats.Tar+GZipStream" : "System.Formats.Tar+SharpCompress.XZ",
                Path.GetFileName(archivePath));
            await Task.Run(() => ExtractTar(archivePath, destinationDir, isTarXz, ct), ct);
            _logger.LogInformation("Extraction complete: {Dir}", destinationDir);
            return destinationDir;
        }

        if (ext != ".zip" && hint != "zip")
            throw new NotSupportedException($"Archive format not supported: ext='{ext}' hint='{hint}'");

        _logger.LogInformation("Extracting zip archive via System.IO.Compression: {Archive}", Path.GetFileName(archivePath));

        // Explicit zip-slip defense on top of .NET 9's built-in `ExtractRelativeToDirectory`
        // check — the managed binary catalog is network-sourced and mirrors Apache
        // Lounge / PHP.net / mysql.com, all of which are high-value targets. Better to
        // fail loudly on a suspicious entry than trust a single layer of defense.
        await Task.Run(() =>
        {
            var destFull = Path.GetFullPath(destinationDir).TrimEnd(Path.DirectorySeparatorChar)
                         + Path.DirectorySeparatorChar;
            using var zip = ZipFile.OpenRead(archivePath);
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();

                // Skip anything containing a parent-directory segment, regardless of
                // separator style. .NET 9 already does this, but we keep the guard
                // explicit for defense-in-depth and so the intent is obvious in code.
                if (entry.FullName.Contains(".."))
                {
                    _logger.LogWarning(
                        "Skipping suspicious zip entry with parent-dir segment: {Entry}",
                        entry.FullName);
                    continue;
                }

                var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                var destPath = Path.GetFullPath(Path.Combine(destinationDir, relative));

                // Resolved path must live strictly inside the destination root.
                if (!destPath.StartsWith(destFull, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "Skipping zip entry that would escape destination root: {Entry} → {Path}",
                        entry.FullName, destPath);
                    continue;
                }

                if (string.IsNullOrEmpty(entry.Name))
                {
                    // Directory entry (trailing slash) — create it.
                    Directory.CreateDirectory(destPath);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                entry.ExtractToFile(destPath, overwrite: true);
            }
        }, ct);

        _logger.LogInformation("Extraction complete: {Dir}", destinationDir);
        return destinationDir;
    }

    /// <summary>
    /// Extract a tar.gz or tar.xz archive using System.Formats.Tar. The
    /// decompression stream is GZipStream (BCL) for .tar.gz or SharpCompress
    /// <see cref="XZStream"/> for .tar.xz — xz is not in the BCL. Applies
    /// the same zip-slip defense as the zip path: any entry containing a
    /// parent-dir segment or resolving outside the destination root is
    /// skipped with a warning, never extracted. On Unix, executable bits
    /// from the tar header are preserved so the extracted binary is
    /// launchable without an explicit chmod +x.
    /// </summary>
    private void ExtractTar(string archivePath, string destinationDir, bool isXz, CancellationToken ct)
    {
        var destFull = Path.GetFullPath(destinationDir).TrimEnd(Path.DirectorySeparatorChar)
                     + Path.DirectorySeparatorChar;

        using var fileStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using Stream decompressed = isXz
            ? new XZStream(fileStream)
            : new GZipStream(fileStream, CompressionMode.Decompress);
        using var tar = new TarReader(decompressed, leaveOpen: false);

        TarEntry? entry;
        while ((entry = tar.GetNextEntry()) is not null)
        {
            ct.ThrowIfCancellationRequested();

            var name = entry.Name;

            if (name.Contains(".."))
            {
                _logger.LogWarning(
                    "Skipping suspicious tar entry with parent-dir segment: {Entry}",
                    name);
                continue;
            }

            var relative = name.Replace('/', Path.DirectorySeparatorChar);
            var destPath = Path.GetFullPath(Path.Combine(destinationDir, relative));

            if (!destPath.StartsWith(destFull, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Skipping tar entry that would escape destination root: {Entry} -> {Path}",
                    name, destPath);
                continue;
            }

            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                case TarEntryType.DirectoryList:
                    Directory.CreateDirectory(destPath);
                    break;

                case TarEntryType.RegularFile:
                case TarEntryType.V7RegularFile:
                case TarEntryType.ContiguousFile:
                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                    // Use ExtractToFile with overwrite so reinstalling a binary
                    // over an existing installation succeeds.
                    entry.ExtractToFile(destPath, overwrite: true);
                    TrySetUnixExecutableBit(destPath, entry.Mode);
                    break;

                case TarEntryType.SymbolicLink:
                case TarEntryType.HardLink:
                    // Cross-platform symlink creation inside the zip-slip
                    // sandbox is a can of worms we don't need — none of our
                    // catalog binaries rely on intra-archive links. Log and
                    // skip so extraction doesn't abort on the first link.
                    _logger.LogDebug("Skipping tar link entry: {Entry} -> {Target}", name, entry.LinkName);
                    break;

                default:
                    _logger.LogDebug("Skipping tar entry of type {Type}: {Entry}", entry.EntryType, name);
                    break;
            }
        }
    }

    /// <summary>
    /// On Linux/macOS, preserve the Unix executable bits carried by the tar
    /// entry so the extracted binary is launchable without a subsequent
    /// chmod +x. No-op on Windows (NTFS has no Unix mode). We only honour
    /// the execute bits — setting arbitrary modes from a network-sourced
    /// archive is a foot-gun.
    /// </summary>
    private static void TrySetUnixExecutableBit(string path, UnixFileMode mode)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        const UnixFileMode executeBits =
            UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

        if ((mode & executeBits) == 0)
            return;

        try
        {
            var current = File.GetUnixFileMode(path);
            File.SetUnixFileMode(path, current | (mode & executeBits));
        }
        catch
        {
            // Best-effort — mismatched filesystem (e.g. tmpfs mounted noexec)
            // or permission errors shouldn't abort extraction.
        }
    }
}

public sealed record DownloadProgress(string App, string Version, long Downloaded, long Total)
{
    public double PercentComplete => Total > 0 ? (double)Downloaded / Total * 100 : 0;
}
