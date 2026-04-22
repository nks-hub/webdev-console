using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Daemon.Services;

namespace NKS.WebDevConsole.Daemon.Plugin;

/// <summary>
/// Background service that refreshes the plugin catalog and syncs any
/// missing plugin artifacts into the on-disk cache at
/// <c>~/.wdc/plugins/&lt;id&gt;/&lt;version&gt;/</c> shortly after the daemon
/// starts, then re-checks every <see cref="RefreshInterval"/>. Opt-in via
/// the <c>NKS_WDC_PLUGIN_AUTOSYNC</c> environment variable (set to <c>1</c>
/// / <c>true</c>) so dev environments running a local build of
/// <c>src/plugins</c> do not unnecessarily hit the catalog API.
/// </summary>
public sealed class PluginCatalogSyncService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(6);

    private readonly PluginCatalogClient _catalog;
    private readonly PluginDownloader _downloader;
    private readonly SettingsStore _settings;
    private readonly ILogger<PluginCatalogSyncService> _logger;

    public PluginCatalogSyncService(
        PluginCatalogClient catalog,
        PluginDownloader downloader,
        SettingsStore settings,
        ILogger<PluginCatalogSyncService> logger)
    {
        _catalog = catalog;
        _downloader = downloader;
        _settings = settings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!IsEnabled())
        {
            _logger.LogDebug(
                "Plugin auto-sync disabled (toggle in Settings → Plugins, "
                + "or set NKS_WDC_PLUGIN_AUTOSYNC=1, to enable)");
            return;
        }

        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var catalogCount = await _catalog.RefreshAsync(stoppingToken);
                if (catalogCount > 0)
                {
                    var installed = await _downloader.SyncLatestAsync(_catalog.Cached, stoppingToken);
                    if (installed > 0)
                        _logger.LogInformation("Plugin auto-sync installed {Count} new plugin(s)", installed);
                    else
                        _logger.LogDebug("Plugin auto-sync: catalog has {C} entries, all cached", catalogCount);
                }

                // Task 36: record lastSyncAt so Settings > About can display
                // "Last synced: <relative>" instead of an indefinite
                // "nesynchronizováno" after the first successful refresh.
                _settings.Set("catalog", "plugin.lastSyncAt", DateTime.UtcNow.ToString("o"));
                _settings.Set("catalog", "plugin.lastSyncOk", "true");
                _settings.Set("catalog", "plugin.lastError", "");
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // Never let a transient catalog outage crash the daemon — the
                // next tick will retry. Log at warning so self-hosted users
                // notice persistent failures in their logs.
                _logger.LogWarning("Plugin auto-sync tick failed: {Error}", ex.Message);
                _settings.Set("catalog", "plugin.lastSyncAt", DateTime.UtcNow.ToString("o"));
                _settings.Set("catalog", "plugin.lastSyncOk", "false");
                _settings.Set("catalog", "plugin.lastError", ex.Message);
            }

            try { await Task.Delay(RefreshInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private bool IsEnabled()
    {
        // SettingsStore row wins so the Settings page can toggle auto-sync
        // at runtime without a daemon restart. Env var remains as the
        // legacy escape hatch for CI / headless deployments that don't
        // want to write a persistent row.
        if (_settings.PluginAutoSyncEnabled) return true;
        return EnvFlags.IsTruthy(Environment.GetEnvironmentVariable("NKS_WDC_PLUGIN_AUTOSYNC"));
    }
}
