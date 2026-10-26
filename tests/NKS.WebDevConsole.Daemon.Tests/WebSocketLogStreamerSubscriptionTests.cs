using NKS.WebDevConsole.Daemon.Services;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Subscriber bookkeeping of <see cref="WebSocketLogStreamer"/> under
/// concurrent connects and disconnects.
/// </summary>
public sealed class WebSocketLogStreamerSubscriptionTests
{
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not reached");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Disconnect_RemovesOnlyThatSubscriber_AndDropsEmptyKey()
    {
        using var streamer = new WebSocketLogStreamer();
        using var ctsA = new CancellationTokenSource();
        using var ctsB = new CancellationTokenSource();
        var a = streamer.StreamAsync("Apache", new RecordingWebSocket(), ctsA.Token);
        var b = streamer.StreamAsync("apache", new RecordingWebSocket(), ctsB.Token);
        Assert.Equal(2, streamer.SubscriberCount("APACHE"));

        ctsA.Cancel();
        await a.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, streamer.SubscriberCount("apache"));

        ctsB.Cancel();
        await b.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, streamer.SubscriberCount("apache"));
        Assert.Equal(0, streamer.ServiceKeyCount);
    }

    [Fact]
    public async Task ConcurrentConnectsAndDisconnects_KeepLiveSubscribersOnly()
    {
        using var streamer = new WebSocketLogStreamer();
        const int total = 440; // every 11th connection stays open

        using var liveCts = new CancellationTokenSource();
        var liveSockets = new List<RecordingWebSocket>();
        var liveTasks = new List<Task>();
        var churnTasks = new List<Task>();

        // Interleave long-lived connects with short-lived connect/disconnect
        // pairs on the same service from many threads.
        await Parallel.ForEachAsync(Enumerable.Range(0, total),
            new ParallelOptions { MaxDegreeOfParallelism = 16 },
            async (i, _) =>
            {
                if (i % 11 == 0)
                {
                    var ws = new RecordingWebSocket();
                    var t = streamer.StreamAsync("mysql", ws, liveCts.Token);
                    lock (liveTasks) { liveSockets.Add(ws); liveTasks.Add(t); }
                }
                else
                {
                    using var cts = new CancellationTokenSource();
                    var t = streamer.StreamAsync("mysql", new RecordingWebSocket(), cts.Token);
                    await Task.Yield();
                    cts.Cancel();
                    await t;
                    lock (churnTasks) churnTasks.Add(t);
                }
            });

        Assert.Equal(total / 11, liveTasks.Count);
        Assert.Equal(liveTasks.Count, streamer.SubscriberCount("mysql"));

        streamer.Push("mysql", "hello");
        await WaitUntilAsync(() => liveSockets.All(s => s.Sent.Count == 1));
        Assert.All(liveSockets, s => Assert.Contains("hello", s.Sent.Single()));

        liveCts.Cancel();
        await Task.WhenAll(liveTasks).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, streamer.SubscriberCount("mysql"));
        Assert.Equal(0, streamer.ServiceKeyCount);
    }

    [Fact]
    public async Task Reconnect_AfterLastSubscriberLeft_ReceivesPushes()
    {
        using var streamer = new WebSocketLogStreamer();
        using (var cts = new CancellationTokenSource())
        {
            var first = streamer.StreamAsync("redis", new RecordingWebSocket(), cts.Token);
            cts.Cancel();
            await first.WaitAsync(TimeSpan.FromSeconds(10));
        }

        using var cts2 = new CancellationTokenSource();
        var ws = new RecordingWebSocket();
        var second = streamer.StreamAsync("redis", ws, cts2.Token);
        streamer.Push("redis", "after-reconnect");
        await WaitUntilAsync(() => ws.Sent.Count == 1);
        Assert.Contains("after-reconnect", ws.Sent.Single());

        cts2.Cancel();
        await second.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
