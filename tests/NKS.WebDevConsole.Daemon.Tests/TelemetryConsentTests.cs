using NKS.WebDevConsole.Daemon.Services;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Tests for <see cref="TelemetryConsent"/>. The consent file lives at a
/// fixed path under <c>{WdcPaths.DataRoot}/telemetry-consent.json</c>.
/// These tests manipulate that file directly to exercise the Load path
/// without affecting the developer's real consent state — the Revoke()
/// call at the end of each test restores the clean "no consent" default.
/// </summary>
public class TelemetryConsentTests
{
    [Fact]
    public void Fresh_instance_has_all_flags_false()
    {
        var consent = new TelemetryConsent();
        consent.Revoke(); // ensure clean slate
        consent.Load();
        Assert.False(consent.Enabled);
        Assert.False(consent.CrashReports);
        Assert.False(consent.UsageMetrics);
        Assert.Null(consent.ConsentGivenUtc);
    }

    [Fact]
    public void Save_persists_all_three_flags()
    {
        var consent = new TelemetryConsent();
        try
        {
            consent.Save(enabled: true, crashReports: true, usageMetrics: true);
            Assert.True(consent.Enabled);
            Assert.True(consent.CrashReports);
            Assert.True(consent.UsageMetrics);
            Assert.NotNull(consent.ConsentGivenUtc);
        }
        finally
        {
            consent.Revoke();
        }
    }

    [Fact]
    public void Save_forces_subflags_off_when_enabled_is_false()
    {
        var consent = new TelemetryConsent();
        try
        {
            consent.Save(enabled: false, crashReports: true, usageMetrics: true);
            Assert.False(consent.Enabled);
            Assert.False(consent.CrashReports);  // gated off
            Assert.False(consent.UsageMetrics);  // gated off
        }
        finally
        {
            consent.Revoke();
        }
    }

    [Fact]
    public void Revoke_clears_all_flags_and_removes_file()
    {
        var consent = new TelemetryConsent();
        consent.Save(enabled: true, crashReports: true, usageMetrics: false);
        Assert.True(consent.Enabled);
        consent.Revoke();
        Assert.False(consent.Enabled);
        Assert.False(consent.CrashReports);
        Assert.False(consent.UsageMetrics);
        Assert.Null(consent.ConsentGivenUtc);
    }

    [Fact]
    public void Second_instance_reads_state_written_by_first()
    {
        var first = new TelemetryConsent();
        try
        {
            first.Save(enabled: true, crashReports: true, usageMetrics: false);
            var second = new TelemetryConsent();
            Assert.True(second.Enabled);
            Assert.True(second.CrashReports);
            Assert.False(second.UsageMetrics);
        }
        finally
        {
            first.Revoke();
        }
    }
}
