using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

using NKS.WebDevConsole.Core.Models;

namespace NKS.WebDevConsole.Daemon.Binaries;

public sealed class CatalogClientOptions
{
    /// <summary>
    /// Base URL of the binary catalog API. Defaults to the public NKS
    /// catalog at https://wdc.nks-hub.cz — matches SettingsStore.CatalogUrl
    /// fallback chain, so end-users get working binaries installs without
    /// editing config. Override via <c>NKS_WDC_CATALOG_URL</c> env var or
    /// the Settings page when running a self-hosted catalog-api.
    /// </summary>
    public string BaseUrl { get; set; } = "https://wdc.nks-hub.cz";
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
    private readonly Func<string>? _baseUrlProvider;
    private readonly object _cacheLock = new();
    private List<BinaryRelease> _cache = new();
    private DateTime _lastFetch = DateTime.MinValue;

    public CatalogClient(
        IHttpClientFactory httpClientFactory,
        ILogger<CatalogClient> logger,
        CatalogClientOptions? options = null,
        Func<string>? baseUrlProvider = null)
    {
        _options = options ?? new CatalogClientOptions();
        // Don't bake BaseAddress into the HttpClient — it's immutable after
        // the first request, so changing the catalog URL via Settings would
        // require a daemon restart. We build absolute URIs per request and
        // read the current base URL from the provider (which in turn hits
        // SettingsStore on every call), so editing the URL in the Settings
        // page + POST /api/binaries/catalog/refresh actually redirects to
        // the new endpoint immediately.
        _http = httpClientFactory.CreateClient("catalog-client");
        _http.Timeout = _options.Timeout;
        _baseUrlProvider = baseUrlProvider;
        _logger = logger;
    }

    private string CurrentBaseUrl()
    {
        string? url = null;
        try
        {
            url = _baseUrlProvider?.Invoke();
        }
        catch (Exception ex)
        {
            // A broken provider (SettingsStore read fail, DB locked, …) must
            // never wedge the binary catalog — fall through to the seed URL
            // instead of throwing out of the HTTP pipeline.
            _logger.LogWarning("Catalog URL provider threw: {Error}", ex.Message);
        }
        if (string.IsNullOrWhiteSpace(url))
            url = _options.BaseUrl;
        // Last-resort fallback so GetAsync() never sees an empty relative URI
        // (HttpClient has no BaseAddress on this instance, so an empty base
        // + "/api/v1/catalog" would throw "invalid request URI"). Matches
        // SettingsStore.CatalogUrl default — public NKS catalog.
        if (string.IsNullOrWhiteSpace(url))
            url = "https://wdc.nks-hub.cz";
        return url.TrimEnd('/');
    }

    public IReadOnlyList<BinaryRelease> CachedReleases
    {
        get { lock (_cacheLock) return _cache.ToList(); }
    }

    /// <summary>
    /// Current daemon OS in the lowercase string the catalog uses
    /// ("windows", "macos", "linux"). Matches <see cref="BinaryRelease.Os"/>.
    /// </summary>
    public static string CurrentOs()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "windows";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "macos";
        return "linux";
    }

    /// <summary>
    /// Current daemon architecture in the catalog's lowercase string
    /// ("x64", "arm64"). Matches <see cref="BinaryRelease.Arch"/>.
    /// </summary>
    public static string CurrentArch()
        => RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => "x64",
        };

    public DateTime LastFetch
    {
        get { lock (_cacheLock) return _lastFetch; }
    }

    /// <summary>Fetch the full catalog from the API and update the cache.</summary>
    public async Task<int> RefreshAsync(CancellationToken ct = default)
    {
        var flattened = new List<BinaryRelease>();
        var baseUrl = CurrentBaseUrl();
        var externalOk = false;
        try
        {
            _logger.LogInformation("Fetching binary catalog from {BaseUrl}/api/v1/catalog", baseUrl);
            using var resp = await _http.GetAsync($"{baseUrl}/api/v1/catalog", ct);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct);
            var doc = JsonSerializer.Deserialize<CatalogDocument>(json, JsonOpts)
                ?? throw new InvalidOperationException("Catalog API returned empty body");

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

            externalOk = true;
            _logger.LogInformation("Catalog refreshed: {Count} releases across {Apps} apps",
                flattened.Count, doc.Apps?.Count ?? 0);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to fetch catalog from {BaseUrl}: {Error}", baseUrl, ex.Message);
        }

        // Always merge the built-in fallback list so apps that the external
        // catalog doesn't know about (e.g. cloudflared) remain installable
        // from the Binaries page even when the catalog server is unreachable.
        // Merge strategy: external wins on (app, version, os, arch) conflicts.
        foreach (var fb in BuiltInFallback())
        {
            var existing = flattened.Any(r =>
                r.App.Equals(fb.App, StringComparison.OrdinalIgnoreCase) &&
                r.Version == fb.Version && r.Os == fb.Os && r.Arch == fb.Arch);
            if (!existing) flattened.Add(fb);
        }

        // On fetch failure, preserve the previous external results — a
        // single transient error used to collapse the Binaries UI back to
        // the fallback-only list, which made it look like the external
        // catalog had dropped half its apps. Only overwrite when the
        // external fetch actually succeeded, or when we had nothing to
        // preserve yet (first-run bootstrap must show the fallback).
        lock (_cacheLock)
        {
            if (externalOk || _cache.Count == 0)
            {
                _cache = flattened;
                _lastFetch = DateTime.UtcNow;
                return flattened.Count;
            }
            return _cache.Count;
        }
    }

    /// <summary>
    /// Hard-coded minimum catalogue used as a fallback when the external
    /// catalog service is unreachable, AND as the only source for apps that
    /// the external catalog doesn't ship (e.g. cloudflared). Keeping this
    /// list short — we only track the latest stable release per app here,
    /// users who want older versions should point at a catalog server.
    /// </summary>
    private static IEnumerable<BinaryRelease> BuiltInFallback()
    {
        // ── Cloudflared (all platforms, direct GitHub release downloads) ──
        yield return new BinaryRelease(
            App: "cloudflared",
            Version: "2026.3.0",
            MajorMinor: "2026.3",
            Url: "https://github.com/cloudflare/cloudflared/releases/download/2026.3.0/cloudflared-windows-amd64.exe",
            Os: "windows",
            Arch: "x64",
            ArchiveType: "exe",
            Source: "github",
            UserAgent: null);
        yield return new BinaryRelease(
            App: "cloudflared",
            Version: "2026.3.0",
            MajorMinor: "2026.3",
            Url: "https://github.com/cloudflare/cloudflared/releases/download/2026.3.0/cloudflared-linux-amd64",
            Os: "linux",
            Arch: "x64",
            ArchiveType: "bin",
            Source: "github",
            UserAgent: null);
        yield return new BinaryRelease(
            App: "cloudflared",
            Version: "2026.3.0",
            MajorMinor: "2026.3",
            Url: "https://github.com/cloudflare/cloudflared/releases/download/2026.3.0/cloudflared-darwin-amd64.tgz",
            Os: "macos",
            Arch: "x64",
            ArchiveType: "tgz",
            Source: "github",
            UserAgent: null);
    }

    public IEnumerable<BinaryRelease> ForApp(string app, string? os = null, string? arch = null)
    {
        lock (_cacheLock)
        {
            return _cache.Where(r =>
                r.App.Equals(app, StringComparison.OrdinalIgnoreCase) &&
                (os is null || r.Os == os) &&
                (arch is null || r.Arch == arch)).ToList();
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
