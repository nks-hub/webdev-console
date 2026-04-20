using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace NKS.WebDevConsole.Daemon.Plugin;

/// <summary>
/// Fetches the WDC plugins release catalog from catalog-api
/// (<c>/api/v1/plugins/catalog</c>). Mirror of <see cref="Binaries.CatalogClient"/>
/// for plugin artifacts. Shape matches <c>plugins_catalog.py</c> generator.
/// </summary>
public sealed class PluginCatalogClient
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly ILogger<PluginCatalogClient> _logger;
    private readonly Func<string>? _baseUrlProvider;
    private readonly object _cacheLock = new();
    private List<PluginCatalogEntry> _cache = new();
    private DateTime _lastFetch = DateTime.MinValue;

    public PluginCatalogClient(
        IHttpClientFactory httpClientFactory,
        ILogger<PluginCatalogClient> logger,
        Func<string>? baseUrlProvider = null)
    {
        _http = httpClientFactory.CreateClient("plugin-catalog");
        _http.Timeout = TimeSpan.FromSeconds(15);
        _baseUrlProvider = baseUrlProvider;
        _logger = logger;
    }

    public IReadOnlyList<PluginCatalogEntry> Cached
    {
        get { lock (_cacheLock) return _cache.ToList(); }
    }

    public DateTime LastFetch
    {
        get { lock (_cacheLock) return _lastFetch; }
    }

    private string CurrentBaseUrl()
    {
        string? url = null;
        try { url = _baseUrlProvider?.Invoke(); }
        catch (Exception ex)
        {
            _logger.LogWarning("Plugin catalog URL provider threw: {Error}", ex.Message);
        }
        if (string.IsNullOrWhiteSpace(url)) url = "https://wdc.nks-hub.cz";
        return url.TrimEnd('/');
    }

    public async Task<int> RefreshAsync(CancellationToken ct = default)
    {
        var baseUrl = CurrentBaseUrl();
        List<PluginCatalogEntry>? list = null;
        try
        {
            _logger.LogInformation("Fetching plugin catalog from {Url}/api/v1/plugins/catalog", baseUrl);
            var resp = await _http.GetAsync($"{baseUrl}/api/v1/plugins/catalog", ct);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct);
            var doc = JsonSerializer.Deserialize<PluginCatalogDocument>(json, JsonOpts);
            list = doc?.Plugins is not null
                ? new List<PluginCatalogEntry>(doc.Plugins)
                : new List<PluginCatalogEntry>();
            _logger.LogInformation("Plugin catalog refreshed: {Count} plugins", list.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Plugin catalog fetch failed from {Url}: {Error}", baseUrl, ex.Message);
        }

        // Only mutate the cache on a successful fetch. The previous code
        // replaced _cache with the empty list + updated _lastFetch even
        // when the HTTP call threw, so a single transient outage wiped
        // the last known-good catalog and made LastFetch advance as if
        // the refresh had succeeded.
        if (list is null)
        {
            lock (_cacheLock) return _cache.Count;
        }

        lock (_cacheLock)
        {
            _cache = list;
            _lastFetch = DateTime.UtcNow;
            return list.Count;
        }
    }

    public PluginCatalogRelease? LatestRelease(string pluginId)
    {
        lock (_cacheLock)
        {
            var entry = _cache.FirstOrDefault(p =>
                p.Id?.Equals(pluginId, StringComparison.OrdinalIgnoreCase) == true);
            return entry?.Releases?.FirstOrDefault();
        }
    }
}

public sealed class PluginCatalogDocument
{
    public string? Schema { get; set; }
    public string? Source { get; set; }
    [JsonPropertyName("plugin_count")]
    public int PluginCount { get; set; }
    public List<PluginCatalogEntry>? Plugins { get; set; }
}

public sealed class PluginCatalogEntry
{
    public string? Id { get; set; }
    public List<PluginCatalogRelease>? Releases { get; set; }
}

public sealed class PluginCatalogRelease
{
    public string? Version { get; set; }
    [JsonPropertyName("major_minor")]
    public string? MajorMinor { get; set; }
    [JsonPropertyName("released_at")]
    public string? ReleasedAt { get; set; }
    public List<PluginCatalogDownload>? Downloads { get; set; }
}

public sealed class PluginCatalogDownload
{
    public string? Url { get; set; }
    public string? Os { get; set; }
    public string? Arch { get; set; }
    [JsonPropertyName("archive_type")]
    public string? ArchiveType { get; set; }
    public string? Source { get; set; }
    public string? Sha256 { get; set; }
}
