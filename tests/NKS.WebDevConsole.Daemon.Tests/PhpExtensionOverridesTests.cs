using NKS.WebDevConsole.Daemon.Services;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Tests for <see cref="PhpExtensionOverrides"/>. Persists to
/// <c>{WdcPaths.DataRoot}/php-extensions.json</c>. These tests trample that
/// file so they run against a temp working directory via WDC_DATA_DIR.
/// </summary>
public class PhpExtensionOverridesTests : IDisposable
{
    private readonly string _tempDataDir;
    private readonly string? _origEnv;

    public PhpExtensionOverridesTests()
    {
        _tempDataDir = Path.Combine(Path.GetTempPath(), "nks-ext-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDataDir);
        _origEnv = Environment.GetEnvironmentVariable("WDC_DATA_DIR");
        // Note: WdcPaths caches its root via Lazy<string> on first access,
        // so setting WDC_DATA_DIR here only matters if WdcPaths hasn't been
        // touched yet in this test process. We instead avoid relying on
        // WdcPaths entirely by writing to the real path the service uses
        // (which is resolved at first call) and cleaning up after.
    }

    public void Dispose()
    {
        if (_origEnv is null)
            Environment.SetEnvironmentVariable("WDC_DATA_DIR", null);
        else
            Environment.SetEnvironmentVariable("WDC_DATA_DIR", _origEnv);
        try { if (Directory.Exists(_tempDataDir)) Directory.Delete(_tempDataDir, true); } catch { }
    }

    [Fact]
    public void GetOverrides_returns_empty_for_unknown_version()
    {
        var overrides = new PhpExtensionOverrides();
        var result = overrides.GetOverrides("99.99");
        Assert.Empty(result);
    }

    [Fact]
    public void SetOverride_then_GetOverrides_returns_stored_value()
    {
        var overrides = new PhpExtensionOverrides();
        var marker = "e2e_test_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            overrides.SetOverride("99.99", marker, true);
            var result = overrides.GetOverrides("99.99");
            Assert.True(result.ContainsKey(marker));
            Assert.True(result[marker]);
        }
        finally
        {
            overrides.ClearOverride("99.99", marker);
        }
    }

    [Fact]
    public void ApplyOverrides_returns_defaults_when_no_overrides()
    {
        var overrides = new PhpExtensionOverrides();
        var defaults = new List<(string, bool)>
        {
            ("curl", true),
            ("gd", true),
            ("mysqli", true),
        };
        var merged = overrides.ApplyOverrides("88.88", defaults);
        Assert.Equal(defaults.Count, merged.Count);
    }

    [Fact]
    public void ApplyOverrides_flips_overridden_entry()
    {
        var overrides = new PhpExtensionOverrides();
        var marker = "applyflip_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            var defaults = new List<(string Name, bool Enabled)> { (marker, true) };
            overrides.SetOverride("77.77", marker, false);
            var merged = overrides.ApplyOverrides("77.77", defaults);
            Assert.Single(merged);
            Assert.False(merged[0].Enabled);
        }
        finally
        {
            overrides.ClearOverride("77.77", marker);
        }
    }

    [Fact]
    public void ApplyOverrides_adds_extensions_not_in_defaults()
    {
        var overrides = new PhpExtensionOverrides();
        var marker = "newext_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            overrides.SetOverride("66.66", marker, true);
            var merged = overrides.ApplyOverrides("66.66", new List<(string, bool)>());
            Assert.Contains(merged, x => x.Name.Equals(marker, StringComparison.OrdinalIgnoreCase) && x.Enabled);
        }
        finally
        {
            overrides.ClearOverride("66.66", marker);
        }
    }

    [Fact]
    public void ClearOverride_removes_entry()
    {
        var overrides = new PhpExtensionOverrides();
        var marker = "clearme_" + Guid.NewGuid().ToString("N")[..8];
        overrides.SetOverride("55.55", marker, true);
        Assert.NotEmpty(overrides.GetOverrides("55.55"));
        overrides.ClearOverride("55.55", marker);
        Assert.False(overrides.GetOverrides("55.55").ContainsKey(marker));
    }
}
