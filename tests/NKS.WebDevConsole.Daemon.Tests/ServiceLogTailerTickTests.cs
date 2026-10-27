using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Daemon.Services;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Drives <see cref="ServiceLogTailer"/> one tick at a time.
/// </summary>
public sealed class ServiceLogTailerTickTests
{
    private sealed class FakeModule : IServiceModule
    {
        public FakeModule(string id) => ServiceId = id;
        public string ServiceId { get; }
        public string DisplayName => ServiceId;
        public ServiceType Type => ServiceType.Other;

        public Func<IReadOnlyList<string>> Logs { get; set; } = () => [];
        public int LogReads;

        public Task<IReadOnlyList<string>> GetLogsAsync(int lines, CancellationToken ct)
        {
            Interlocked.Increment(ref LogReads);
            return Task.FromResult(Logs());
        }

        public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<ValidationResult> ValidateConfigAsync(CancellationToken ct) => Task.FromResult(new ValidationResult(true));
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        public Task ReloadAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<ServiceStatus> GetStatusAsync(CancellationToken ct) =>
            Task.FromResult(new ServiceStatus(ServiceId, DisplayName, ServiceState.Running, null, 0, 0, TimeSpan.Zero));
    }

    private sealed class CapturingLogger : ILogger<ServiceLogTailer>
    {
        public List<(LogLevel Level, string Message, Exception? Error)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception), exception));
        }
    }

    private static (ServiceLogTailer Tailer, CapturingLogger Logger) Build(SseService sse, WebSocketLogStreamer streamer, params IServiceModule[] modules)
    {
        var services = new ServiceCollection();
        foreach (var m in modules) services.AddSingleton(m);
        var logger = new CapturingLogger();
        return (new ServiceLogTailer(services.BuildServiceProvider(), sse, streamer, logger), logger);
    }

    [Fact]
    public async Task Tick_WithoutListeners_DoesNotReadLogs()
    {
        var module = new FakeModule("apache");
        using var streamer = new WebSocketLogStreamer();
        var (tailer, _) = Build(new SseService(), streamer, module);

        await tailer.TickAsync(CancellationToken.None);

        Assert.Equal(0, module.LogReads);
    }

    [Fact]
    public async Task Tick_WebSocketSubscriberOnOneService_ReadsOnlyThatService()
    {
        var apache = new FakeModule("apache");
        var mysql = new FakeModule("mysql");
        using var streamer = new WebSocketLogStreamer();
        using var cts = new CancellationTokenSource();
        var stream = streamer.StreamAsync("apache", new RecordingWebSocket(), cts.Token);
        var (tailer, _) = Build(new SseService(), streamer, apache, mysql);

        await tailer.TickAsync(CancellationToken.None);

        Assert.Equal(1, apache.LogReads);
        Assert.Equal(0, mysql.LogReads);

        cts.Cancel();
        await stream.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Tick_LogReadFailure_IsLoggedAtDebug()
    {
        var module = new FakeModule("redis") { Logs = () => throw new IOException("log file locked") };
        var sse = new SseService();
        sse.AddClient(new DefaultHttpContext().Response);
        using var streamer = new WebSocketLogStreamer();
        var (tailer, logger) = Build(sse, streamer, module);
        tailer.Watch("redis");

        await tailer.TickAsync(CancellationToken.None);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.IsType<IOException>(entry.Error);
        Assert.Contains("redis", entry.Message);
    }

    [Fact]
    public async Task Tick_SseClientWithoutWatch_DoesNotReadLogs()
    {
        // The app keeps /api/events open all the time; that alone is no reason to tail.
        var module = new FakeModule("apache");
        var sse = new SseService();
        sse.AddClient(new DefaultHttpContext().Response);
        using var streamer = new WebSocketLogStreamer();
        var (tailer, _) = Build(sse, streamer, module);

        await tailer.TickAsync(CancellationToken.None);
        Assert.Equal(0, module.LogReads);

        tailer.Watch("apache");
        await tailer.TickAsync(CancellationToken.None);
        Assert.Equal(1, module.LogReads);
    }

    [Fact]
    public async Task Tick_ForwardsNewLinesToWebSocketSubscriber()
    {
        var window = new List<string> { "a", "b" };
        var module = new FakeModule("php") { Logs = () => window.ToList() };
        using var streamer = new WebSocketLogStreamer();
        using var cts = new CancellationTokenSource();
        var ws = new RecordingWebSocket();
        var stream = streamer.StreamAsync("php", ws, cts.Token);
        var (tailer, _) = Build(new SseService(), streamer, module);

        await tailer.TickAsync(CancellationToken.None); // baseline
        window.Add("c");
        await tailer.TickAsync(CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (ws.Sent.IsEmpty && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.Contains("\"c\"", Assert.Single(ws.Sent));

        cts.Cancel();
        await stream.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
