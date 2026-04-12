using NKS.WebDevConsole.Daemon.Services;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Tests for <see cref="WebSocketLogStreamer"/>. The StreamAsync loop requires
/// a live WebSocket and is covered by integration tests elsewhere — these
/// tests exercise the synchronous / deterministic surface:
///   - Push with no subscribers is a safe no-op
///   - SubscriberCount returns 0 for unknown / never-seen service ids
///   - Service ids are matched case-insensitively (ToLowerInvariant keying)
///   - Dispose doesn't throw on fresh or empty instances
/// Regressions here would break log streaming for the UI and be diagnostically
/// hard to trace because failures only surface on reconnect.
/// </summary>
public sealed class WebSocketLogStreamerTests
{
    [Fact]
    public void Push_WithNoSubscribers_DoesNotThrow()
    {
        using var streamer = new WebSocketLogStreamer();
        streamer.Push("apache", "line1");
        streamer.Push("apache", "line2");
        streamer.Push("mysql", "line3");
    }

    [Fact]
    public void Push_EmptyServiceId_DoesNotThrow()
    {
        using var streamer = new WebSocketLogStreamer();
        streamer.Push("", "orphaned line");
    }

    [Fact]
    public void SubscriberCount_UnknownService_ReturnsZero()
    {
        using var streamer = new WebSocketLogStreamer();
        Assert.Equal(0, streamer.SubscriberCount("never-subscribed"));
    }

    [Fact]
    public void SubscriberCount_CaseInsensitive_BeforeAnySubscription()
    {
        using var streamer = new WebSocketLogStreamer();
        // Key normalization is ToLowerInvariant — even with no subscribers,
        // calling with different casings should return consistent 0.
        Assert.Equal(0, streamer.SubscriberCount("APACHE"));
        Assert.Equal(0, streamer.SubscriberCount("apache"));
        Assert.Equal(0, streamer.SubscriberCount("Apache"));
    }

    [Fact]
    public void Dispose_Fresh_DoesNotThrow()
    {
        var streamer = new WebSocketLogStreamer();
        streamer.Dispose();
    }

    [Fact]
    public void Dispose_AfterPush_DoesNotThrow()
    {
        var streamer = new WebSocketLogStreamer();
        streamer.Push("apache", "line1");
        streamer.Dispose();
    }

    [Fact]
    public void Dispose_Idempotent()
    {
        var streamer = new WebSocketLogStreamer();
        streamer.Dispose();
        streamer.Dispose();
    }

    [Fact]
    public void Push_ManyLinesManyServices_NoExceptions()
    {
        using var streamer = new WebSocketLogStreamer();
        for (int i = 0; i < 1000; i++)
        {
            streamer.Push($"svc{i % 10}", $"line {i}");
        }
    }
}
