using System.Net;
using System.Net.Sockets;
using NKS.WebDevConsole.Daemon.Deploy;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Phase 6.9b — TCP probe used by <see cref="PreDeploySnapshotter"/>
/// before spawning mysqldump/pg_dump. Verifies the fast-fail behaviour
/// against a known-closed port (probe should reject quickly with a clear
/// message) and against a real listening socket (probe should succeed).
/// </summary>
public sealed class PreDeploySnapshotterProbeTests
{
    [Fact]
    public async Task ProbeTcp_Succeeds_AgainstListeningSocket()
    {
        // Bind a TcpListener on an OS-chosen ephemeral port, then probe it.
        // The listener is enough — Connect succeeds at the kernel level
        // even without our test calling Accept.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            await PreDeploySnapshotter.ProbeTcpAsync("127.0.0.1", port, "test", default);
            // No throw → success.
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ProbeTcp_ThrowsInvalidOperation_AgainstClosedPort()
    {
        // Bind, capture the port, immediately Stop. The port is now closed.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PreDeploySnapshotter.ProbeTcpAsync("127.0.0.1", port, "mysql", default));
        // Error message must surface the host:port + the db type label so
        // operators can grep deploy_runs.error_message and find the
        // exact (host, port, type) tuple that failed.
        Assert.Contains("127.0.0.1", ex.Message);
        Assert.Contains(port.ToString(), ex.Message);
        Assert.Contains("mysql", ex.Message);
    }

    [Fact]
    public async Task ProbeTcp_PassesThroughCallerCancellation_WithoutWrapping()
    {
        // When the CALLER's CT cancels (not the 3s timeout), the probe
        // must NOT wrap into InvalidOperationException — let the
        // OperationCanceledException propagate so the deploy task's
        // cancellation handler sees it as cancellation, not a probe fail.
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // cancel BEFORE the call
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PreDeploySnapshotter.ProbeTcpAsync("198.51.100.1", 9999, "pgsql", cts.Token));
    }
}
