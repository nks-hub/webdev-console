using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace NKS.WebDevConsole.Daemon.Plugin;

/// <summary>
/// Downloads plugin release artifacts referenced by
/// <see cref="PluginCatalogClient"/> into the on-disk plugin cache at
/// <c>~/.wdc/plugins/&lt;id&gt;/&lt;version&gt;/</c>. Idempotent — if the target
/// directory already contains the unpacked plugin, the download is
/// skipped. Safe to call on every daemon startup.
/// </summary>
public sealed class PluginDownloader
{
    private readonly HttpClient _http;
    private readonly ILogger<PluginDownloader> _logger;

    public PluginDownloader(
        IHttpClientFactory httpClientFactory,
        ILogger<PluginDownloader> logger)
    {
        _http = httpClientFactory.CreateClient("plugin-downloader");
        _http.Timeout = TimeSpan.FromMinutes(2);
        _logger = logger;
    }

    /// <summary>
    /// Resolves <c>~/.wdc/plugins/</c> — the root of the on-disk plugin cache.
    /// Matches the convention used by the binaries cache at <c>~/.wdc/binaries/</c>.
    /// </summary>
    public static string CacheRoot()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".wdc", "plugins");
    }

    /// <summary>
    /// Downloads all plugins advertised in the catalog (latest release of each)
    /// into the on-disk cache. Plugins already present in the cache are not
    /// re-downloaded. Returns the number of plugins successfully installed
    /// during this call (zero when everything was cached).
    /// </summary>
    public async Task<int> SyncLatestAsync(
        IReadOnlyList<PluginCatalogEntry> entries,
        CancellationToken ct = default)
    {
        var root = CacheRoot();
        Directory.CreateDirectory(root);

        var installed = 0;
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Id) || entry.Releases is null || entry.Releases.Count == 0)
                continue;

            var release = entry.Releases[0];
            if (string.IsNullOrWhiteSpace(release.Version))
                continue;

            var download = SelectZipDownload(release);
            if (download is null || string.IsNullOrWhiteSpace(download.Url))
            {
                _logger.LogDebug("Plugin {Id} v{Version} has no .zip download — skipping",
                    entry.Id, release.Version);
                continue;
            }

            var targetDir = Path.Combine(root, entry.Id!, release.Version!);
            if (Directory.Exists(targetDir) && HasPluginContent(targetDir))
            {
                _logger.LogDebug("Plugin {Id} v{Version} already cached at {Path}",
                    entry.Id, release.Version, targetDir);
                continue;
            }

            try
            {
                await InstallAsync(download.Url!, targetDir, download.Sha256, ct);
                installed++;
                _logger.LogInformation("Installed plugin {Id} v{Version} → {Path}",
                    entry.Id, release.Version, targetDir);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to install plugin {Id} v{Version} from {Url}: {Error}",
                    entry.Id, release.Version, download.Url, ex.Message);
                // Best-effort cleanup so a partially-extracted folder doesn't
                // fool HasPluginContent on the next run.
                try { if (Directory.Exists(targetDir)) Directory.Delete(targetDir, recursive: true); }
                catch { /* ignore */ }
            }
        }
        return installed;
    }

    private async Task InstallAsync(string url, string targetDir, string? expectedSha256, CancellationToken ct)
    {
        Directory.CreateDirectory(targetDir);

        var tempFile = Path.Combine(Path.GetTempPath(), $"wdc-plugin-{Guid.NewGuid():N}.zip");
        try
        {
            _logger.LogDebug("Downloading {Url} → {Temp}", url, tempFile);
            using (var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(tempFile);
                await resp.Content.CopyToAsync(fs, ct);
            }

            // SHA256 is now MANDATORY. Earlier this branch ran only when the
            // catalog supplied a hash — entries without sha256 (an oversight
            // upstream, or a malicious catalog mirror that strips the field)
            // were extracted and loaded as a CLR plugin assembly with full
            // user privileges. Refuse to install anything we can't verify so
            // a MITM/cache-poisoning attack on wdc.nks-hub.cz cannot smuggle
            // arbitrary code into the daemon process.
            if (string.IsNullOrWhiteSpace(expectedSha256))
            {
                throw new InvalidOperationException(
                    $"Plugin download from {url} has no SHA256 in catalog metadata — refusing install. " +
                    "Update the catalog entry with a sha256 field or contact the plugin author.");
            }
            var actual = await ComputeSha256Async(tempFile, ct);
            if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"SHA256 mismatch: expected {expectedSha256}, got {actual}");

            // Extract into the version directory. ExtractToDirectory with
            // overwriteFiles: true so a partial re-install completes cleanly.
            ZipFile.ExtractToDirectory(tempFile, targetDir, overwriteFiles: true);
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); }
            catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Picks the first `.zip` download entry on a release, treating a
    /// missing/blank <c>ArchiveType</c> as implicitly zip (the plugins
    /// catalog currently ships only zip assets so omitted type == zip).
    /// Returns null if no entry carries a non-empty URL.
    /// </summary>
    public static PluginCatalogDownload? SelectZipDownload(PluginCatalogRelease release)
    {
        if (release.Downloads is null || release.Downloads.Count == 0) return null;
        return release.Downloads.FirstOrDefault(d =>
            !string.IsNullOrWhiteSpace(d.Url) &&
            (string.IsNullOrWhiteSpace(d.ArchiveType) ||
             d.ArchiveType.Equals("zip", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Skip-reason enum returned by <see cref="ClassifyEntry"/>.</summary>
    public enum EntryClassification
    {
        Installable,
        SkipMissingId,
        SkipNoReleases,
        SkipMissingVersion,
        SkipNoZipDownload,
    }

    /// <summary>
    /// Classifies a catalog entry against the same rules SyncLatestAsync
    /// uses for filtering, without touching disk or network. Exposed so
    /// tests can assert the skip-reason taxonomy directly.
    /// </summary>
    public static EntryClassification ClassifyEntry(PluginCatalogEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Id)) return EntryClassification.SkipMissingId;
        if (entry.Releases is null || entry.Releases.Count == 0) return EntryClassification.SkipNoReleases;
        var release = entry.Releases[0];
        if (string.IsNullOrWhiteSpace(release.Version)) return EntryClassification.SkipMissingVersion;
        if (SelectZipDownload(release) is null) return EntryClassification.SkipNoZipDownload;
        return EntryClassification.Installable;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(fs, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// A plugin directory is considered valid content if it contains at
    /// least one DLL matching the plugin naming convention. Guards against
    /// corrupt/incomplete extractions on prior runs.
    /// </summary>
    private static bool HasPluginContent(string dir)
    {
        try
        {
            return Directory.GetFiles(dir, "NKS.WebDevConsole.Plugin.*.dll").Length > 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Enumerates installed plugin version directories under the cache root
    /// for <see cref="PluginLoader"/> consumption. Returns the latest version
    /// directory per plugin id — daemon loads only one version at a time.
    /// </summary>
    public static IEnumerable<string> EnumerateLatestVersionDirs()
    {
        var root = CacheRoot();
        if (!Directory.Exists(root)) yield break;

        foreach (var pluginDir in Directory.GetDirectories(root))
        {
            var versions = Directory.GetDirectories(pluginDir);
            if (versions.Length == 0) continue;
            // Lexical max is close enough for SemVer when pad comparison would
            // over-engineer a greenfield path. PluginLoader emits a warning
            // anyway for non-SemVer versions.
            var latest = versions.OrderByDescending(v => Path.GetFileName(v), StringComparer.OrdinalIgnoreCase).First();
            if (HasPluginContent(latest)) yield return latest;
        }
    }
}
