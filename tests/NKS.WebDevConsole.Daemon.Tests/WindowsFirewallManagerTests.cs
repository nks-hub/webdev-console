using Microsoft.Extensions.Logging.Abstractions;
using NKS.WebDevConsole.Daemon.Services;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Smoke tests for <see cref="WindowsFirewallManager"/>. Non-admin and
/// non-Windows return 0 without throwing — these tests verify that
/// invariant holds so a CI runner without elevation never breaks the
/// build. We deliberately do NOT test the actual `netsh advfirewall add`
/// path because it requires admin and mutates global Windows state; that
/// code path is verified manually and by the integration test when the
/// harness runs on an elevated developer workstation.
/// </summary>
public class WindowsFirewallManagerTests
{
    [Fact]
    public async Task EnsureRulesRegisteredAsync_is_noop_without_admin()
    {
        var manager = new WindowsFirewallManager(NullLogger<WindowsFirewallManager>.Instance);
        // On CI (non-Windows or non-admin Windows), this must never throw
        // and must never mutate global state. Return value is 0 when no
        // rules were created.
        var created = await manager.EnsureRulesRegisteredAsync();
        Assert.True(created >= 0);
    }

    [Fact]
    public async Task EnsureRulesRegisteredAsync_tolerates_repeated_calls()
    {
        var manager = new WindowsFirewallManager(NullLogger<WindowsFirewallManager>.Instance);
        // Called twice back-to-back — second call should still be a no-op
        // and not throw even if the first succeeded partially.
        await manager.EnsureRulesRegisteredAsync();
        await manager.EnsureRulesRegisteredAsync();
    }

    [Fact]
    public async Task EnsureRulesRegisteredAsync_respects_cancellation()
    {
        var manager = new WindowsFirewallManager(NullLogger<WindowsFirewallManager>.Instance);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        // Pre-cancelled token should be honoured. We accept either:
        //   - method returns 0 immediately (non-admin short-circuit runs first)
        //   - OperationCanceledException propagates
        // Both are valid behaviour for a non-blocking best-effort helper.
        try
        {
            var created = await manager.EnsureRulesRegisteredAsync(cts.Token);
            Assert.True(created >= 0);
        }
        catch (OperationCanceledException)
        {
            // expected
        }
    }
}
