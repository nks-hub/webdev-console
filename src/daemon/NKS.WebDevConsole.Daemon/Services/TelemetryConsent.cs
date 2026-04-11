using System.Text.Json;
using NKS.WebDevConsole.Core.Services;

namespace NKS.WebDevConsole.Daemon.Services;

/// <summary>
/// Per-user opt-in consent storage for Sentry crash reporting and anonymous
/// telemetry. Phase 7 plan item "#111 Sentry/telemetry opt-in with explicit consent".
///
/// The wdc daemon **never** sends any telemetry unless the user has actively
/// opted in via the Settings page. Consent is persisted to
/// <c>~/.wdc/data/telemetry-consent.json</c> and read on every startup. The
/// file is a simple JSON doc with three boolean flags and an ISO timestamp:
///
/// <code>
/// { "enabled": true, "crashReports": true, "usageMetrics": false,
///   "consentGivenUtc": "2026-04-11T01:23:45Z" }
/// </code>
///
/// Absence of the file is treated as "no consent", same as <c>enabled: false</c>.
///
/// What we would collect (wire up in a later iteration):
///   - Crash reports: stack trace, .NET version, OS version, daemon version. No
///     file paths, no source code, no site data, no passwords.
///   - Usage metrics: counts of operations (service starts, site creates), no
///     identifiers of any kind.
///
/// What we **never** collect: hostnames, IPs, domain names, site names, hosts
/// file contents, SSL certs, database contents, source code.
/// </summary>
public sealed class TelemetryConsent
{
    private static readonly string ConsentFilePath = Path.Combine(
        WdcPaths.DataRoot, "telemetry-consent.json");

    private static readonly object _lock = new();

    public bool Enabled { get; private set; }
    public bool CrashReports { get; private set; }
    public bool UsageMetrics { get; private set; }
    public DateTime? ConsentGivenUtc { get; private set; }

    public TelemetryConsent()
    {
        Load();
    }

    public void Load()
    {
        lock (_lock)
        {
            if (!File.Exists(ConsentFilePath))
            {
                Enabled = false;
                CrashReports = false;
                UsageMetrics = false;
                ConsentGivenUtc = null;
                return;
            }
            try
            {
                var json = File.ReadAllText(ConsentFilePath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                Enabled = root.TryGetProperty("enabled", out var e) && e.GetBoolean();
                CrashReports = root.TryGetProperty("crashReports", out var cr) && cr.GetBoolean();
                UsageMetrics = root.TryGetProperty("usageMetrics", out var um) && um.GetBoolean();
                if (root.TryGetProperty("consentGivenUtc", out var t) && t.ValueKind == JsonValueKind.String)
                    ConsentGivenUtc = DateTime.Parse(t.GetString()!, null, System.Globalization.DateTimeStyles.RoundtripKind);
            }
            catch
            {
                // Corrupted file — treat as no consent, safest default
                Enabled = false;
                CrashReports = false;
                UsageMetrics = false;
                ConsentGivenUtc = null;
            }
        }
    }

    public void Save(bool enabled, bool crashReports, bool usageMetrics)
    {
        lock (_lock)
        {
            Enabled = enabled;
            CrashReports = enabled && crashReports;
            UsageMetrics = enabled && usageMetrics;
            ConsentGivenUtc = DateTime.UtcNow;

            Directory.CreateDirectory(Path.GetDirectoryName(ConsentFilePath)!);
            var payload = new
            {
                enabled = Enabled,
                crashReports = CrashReports,
                usageMetrics = UsageMetrics,
                consentGivenUtc = ConsentGivenUtc?.ToString("O"),
            };
            File.WriteAllText(
                ConsentFilePath,
                JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    public void Revoke()
    {
        lock (_lock)
        {
            try { if (File.Exists(ConsentFilePath)) File.Delete(ConsentFilePath); } catch { /* ignore */ }
            Enabled = false;
            CrashReports = false;
            UsageMetrics = false;
            ConsentGivenUtc = null;
        }
    }
}
