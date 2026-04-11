using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

using NKS.WebDevConsole.Core.Models;

namespace NKS.WebDevConsole.Daemon.Binaries;

public sealed class CatalogClientOptions
{
    /// <summary>
    /// Base URL of the binary catalog API. Default points at a locally-hosted
    /// reference implementation — any HTTP service that returns the expected
    /// JSON shape works (see docs/catalog-api.md). Override via
    /// <c>NKS_WDC_CATALOG_URL</c> env var to point at an upstream service.
    /// </summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:8765";
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Fetches the binary release catalog from the NKS catalog API. Caches the
/// response in memory for the lifetime of the daemon — refreshable via
/// <see cref="RefreshAsync"/>. Falls back to an empty catalog if the server
/// is unreachable; the daemon then logs a warning and the user can install
/// binaries via the built-in marketplace tab instead.
/// </summary>
public sealed class CatalogClient
{
    private readonly HttpClient _http;
    private readonly ILogger<CatalogClient> _logger;
    private readonly CatalogClientOptions _options;
    private readonly object _cacheLock = new();
    private List<BinaryRelease> _cache = new();
    private DateTime _lastFetch = DateTime.MinValue;

    public CatalogClient(IHttpClientFactory httpClientFactory, ILogger<CatalogClient> logger, CatalogClientOptions? options = null)
    {
        _options = options ?? new CatalogClientOptions();
        _http = httpClientFactory.CreateClient("catalog-client");
        _http.BaseAddress = new Uri(_options.BaseUrl);
        _http.Timeout = _options.Timeout;
        _logger = logger;
    }

    public IReadOnlyList<BinaryRelease> CachedReleases
    {
        get { lock (_cacheLock) return _cache.ToList(); }
    }

    public DateTime LastFetch
    {
        get { lock (_cacheLock) return _lastFetch; }
    }

    /// <summary>Fetch the full catalog from the API and update the cache.</summary>
    public async Task<int> RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("Fetching binary catalog from {BaseUrl}/api/v1/catalog", _options.BaseUrl);
            var resp = await _http.GetAsync("/api/v1/catalog", ct);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct);
            var doc = JsonSerializer.Deserialize<CatalogDocument>(json, JsonOpts)
                ?? throw new InvalidOperationException("Catalog API returned empty body");

            var flattened = new List<BinaryRelease>();
            foreach (var (appName, appData) in doc.Apps ?? new())
            {
                foreach (var release in appData.Releases ?? new())
                {
                    foreach (var dl in release.Downloads ?? new())
                    {
                        flattened.Add(new BinaryRelease(
                            App: appName,
                            Version: release.Version ?? "",
                            MajorMinor: release.MajorMinor ?? "",
                            Url: dl.Url ?? "",
                            Os: dl.Os ?? "windows",
                            Arch: dl.Arch ?? "x64",
                            ArchiveType: dl.ArchiveType ?? "zip",
                            Source: dl.Source ?? "unknown",
                            UserAgent: dl.Headers?.GetValueOrDefault("User-Agent")
                        ));
                    }
                }
            }

            lock (_cacheLock)
            {
                _cache = flattened;
                _lastFetch = DateTime.UtcNow;
            }

            _logger.LogInformation("Catalog refreshed: {Count} releases across {Apps} apps",
                flattened.Count, doc.Apps?.Count ?? 0);
            return flattened.Count;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to fetch catalog from {BaseUrl}: {Error}", _options.BaseUrl, ex.Message);
            return 0;
        }
    }

    public IEnumerable<BinaryRelease> ForApp(string app, string os = "windows", string arch = "x64")
    {
        lock (_cacheLock)
        {
            return _cache.Where(r =>
                r.App.Equals(app, StringComparison.OrdinalIgnoreCase) &&
                r.Os == os && r.Arch == arch).ToList();
        }
    }

    public BinaryRelease? Find(string app, string version, string os = "windows", string arch = "x64")
        => ForApp(app, os, arch).FirstOrDefault(r => r.Version == version);

    public BinaryRelease? FindLatest(string app, string? majorMinor = null, string os = "windows", string arch = "x64")
    {
        var releases = ForApp(app, os, arch);
        if (majorMinor is not null)
            releases = releases.Where(r => r.MajorMinor == majorMinor);
        return releases.FirstOrDefault();
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ── DTOs that match the catalog API JSON shape ─────────────────────────
    private sealed class CatalogDocument
    {
        [JsonPropertyName("schema_version")] public string? SchemaVersion { get; set; }
        public Dictionary<string, AppDoc>? Apps { get; set; }
    }
    private sealed class AppDoc
    {
        public string? Name { get; set; }
        [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
        public string? Category { get; set; }
        public List<ReleaseDoc>? Releases { get; set; }
    }
    private sealed class ReleaseDoc
    {
        public string? Version { get; set; }
        [JsonPropertyName("major_minor")] public string? MajorMinor { get; set; }
        public string? Channel { get; set; }
        public List<DownloadDoc>? Downloads { get; set; }
    }
    private sealed class DownloadDoc
    {
        public string? Url { get; set; }
        public string? Os { get; set; }
        public string? Arch { get; set; }
        [JsonPropertyName("archive_type")] public string? ArchiveType { get; set; }
        public string? Source { get; set; }
        public Dictionary<string, string>? Headers { get; set; }
    }
}
