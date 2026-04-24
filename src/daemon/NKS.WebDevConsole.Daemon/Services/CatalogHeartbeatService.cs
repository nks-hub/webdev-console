using System.Net.Http.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NKS.WebDevConsole.Daemon.Services;

/// <summary>
/// Keeps the catalog's view of this device's last_seen_at fresh so the
/// cloud admin UI shows "online" instead of "offline". The catalog
/// updates last_seen_at inside POST /api/v1/sync/config (api_sync.py:86)
/// and considers a device online when last_seen_at is less than 5 min
/// old. A heartbeat every 60 s keeps the device comfortably inside that
/// window with room for one missed beat + network jitter.
/// </summary>
public sealed class CatalogHeartbeatService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private readonly IHttpClientFactory _http;
    private readonly SettingsStore _settings;
    private readonly ILogger<CatalogHeartbeatService> _logger;

    public CatalogHeartbeatService(
        IHttpClientFactory http,
        SettingsStore settings,
        ILogger<CatalogHeartbeatService> logger)
    {
        _http = http;
        _settings = settings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Short initial delay so bootstrap (DB migrate, plugin init,
        // catalog sync) settles before we open an outbound HTTP call.
        // 3 s is fast enough that a user who just launched the app sees
        // their device appear "online" in the cloud admin UI within a
        // few seconds — not 15 s later.
        try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); }
        catch (OperationCanceledException) { return; }

        // First heartbeat acts as the "device register on boot" — if the
        // user was signed in on a previous session the row already exists;
        // if they just logged in, this tick creates it. Failure is not
        // surfaced: the next-tick retry is our queue, and the catalog
        // being offline is not a user-visible concern.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SendHeartbeatAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Debug log only — don't let a flaky catalog spam Error
                // and don't let TaskCanceledException during host shutdown
                // escape up to the BackgroundService exception handler
                // (which would StopHost and kill the daemon).
                _logger.LogDebug(ex, "Catalog heartbeat skipped");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Expected on app shutdown. Swallow so BackgroundService
                // doesn't get an unhandled exception and trigger StopHost
                // cascade — we want the host to shut down cleanly.
                return;
            }
        }
    }

    private async Task SendHeartbeatAsync(CancellationToken ct)
    {
        var token = _settings.GetString("sync", "accountToken");
        if (string.IsNullOrWhiteSpace(token)) return;  // not signed in

        var deviceId = _settings.GetString("sync", "deviceId");
        if (string.IsNullOrWhiteSpace(deviceId)) return;  // first-run guard

        var catalogUrl = (_settings.CatalogUrl ?? "https://wdc.nks-hub.cz").TrimEnd('/');
        using var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        // Minimal payload — catalog-api accepts an empty config and still
        // bumps last_seen_at. Builds a valid SnapshotPush body shape
        // without expensive daemon-wide config enumeration.
        var body = new
        {
            device_id = deviceId,
            payload = new
            {
                settings = new Dictionary<string, object?>(),
                sites = Array.Empty<object>(),
            },
        };

        var resp = await client.PostAsJsonAsync(
            $"{catalogUrl}/api/v1/sync/config",
            body,
            ct);

        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized
         || resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            _logger.LogDebug("Catalog heartbeat got {Status}: token expired, pausing", (int)resp.StatusCode);
            return;
        }

        // Non-2xx (other than auth) → surface at Information so a path
        // regression (e.g. someone changes the catalog route again) is
        // visible without the operator having to lower log level.
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogInformation(
                "Catalog heartbeat → {Status} {Url}",
                (int)resp.StatusCode,
                $"{catalogUrl}/api/v1/sync/config");
            return;
        }

        _logger.LogDebug("Catalog heartbeat → {Status}", (int)resp.StatusCode);
    }
}
