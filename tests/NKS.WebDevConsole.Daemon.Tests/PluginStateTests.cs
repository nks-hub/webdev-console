using NKS.WebDevConsole.Daemon.Services;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Tests for <see cref="PluginState"/>. Persists disabled plugin IDs to
/// <c>{WdcPaths.DataRoot}/plugin-state.json</c>. Tests use uniquely-named
/// marker plugin IDs so they don't collide with real plugins on the dev
/// machine and can always clean up via <see cref="PluginState.SetEnabled"/>
/// with <c>enabled: true</c>.
/// </summary>
public class PluginStateTests
{
    [Fact]
    public void Fresh_plugin_is_enabled_by_default()
    {
        var state = new PluginState();
        var marker = "nks.wdc.test." + Guid.NewGuid().ToString("N")[..8];
        Assert.True(state.IsEnabled(marker));
    }

    [Fact]
    public void SetEnabled_false_persists_disabled_state()
    {
        var state = new PluginState();
        var marker = "nks.wdc.test." + Guid.NewGuid().ToString("N")[..8];
        try
        {
            state.SetEnabled(marker, false);
            Assert.False(state.IsEnabled(marker));
        }
        finally
        {
            state.SetEnabled(marker, true); // cleanup
        }
    }

    [Fact]
    public void SetEnabled_true_removes_from_disabled_set()
    {
        var state = new PluginState();
        var marker = "nks.wdc.test." + Guid.NewGuid().ToString("N")[..8];
        state.SetEnabled(marker, false);
        state.SetEnabled(marker, true);
        Assert.True(state.IsEnabled(marker));
        Assert.DoesNotContain(marker, state.DisabledIds);
    }

    [Fact]
    public void DisabledIds_lists_disabled_plugins()
    {
        var state = new PluginState();
        var marker = "nks.wdc.test." + Guid.NewGuid().ToString("N")[..8];
        try
        {
            state.SetEnabled(marker, false);
            Assert.Contains(marker, state.DisabledIds);
        }
        finally
        {
            state.SetEnabled(marker, true);
        }
    }

    [Fact]
    public void Second_instance_reads_state_written_by_first()
    {
        var first = new PluginState();
        var marker = "nks.wdc.test." + Guid.NewGuid().ToString("N")[..8];
        try
        {
            first.SetEnabled(marker, false);
            var second = new PluginState();
            Assert.False(second.IsEnabled(marker));
        }
        finally
        {
            first.SetEnabled(marker, true);
        }
    }
}
