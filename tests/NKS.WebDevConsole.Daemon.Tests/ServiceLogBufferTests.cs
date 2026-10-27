using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Daemon.Services;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// <see cref="ServiceLogBuffer"/> gives every reader the same history, whether
/// the plugin drains a queue on each read or returns a repeating tail window.
/// </summary>
public class ServiceLogBufferTests
{
    private sealed class FakeModule(string id, Func<int, IReadOnlyList<string>> read) : IServiceModule
    {
        public string ServiceId => id;
        public string DisplayName => id;
        public ServiceType Type => ServiceType.Other;
        public Task<IReadOnlyList<string>> GetLogsAsync(int lines, CancellationToken ct) => Task.FromResult(read(lines));
        public Task<ValidationResult> ValidateConfigAsync(CancellationToken ct) => Task.FromResult(new ValidationResult(true));
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        public Task ReloadAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<ServiceStatus> GetStatusAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    // Mailpit, Redis, PostgreSQL, … : every read takes the lines out of the queue.
    private static FakeModule Draining(string id, Queue<string> queue) => new(id, max =>
    {
        var lines = new List<string>();
        while (lines.Count < max && queue.TryDequeue(out var line)) lines.Add(line);
        return lines;
    });

    [Fact]
    public async Task DrainingModule_SecondReaderStillGetsTheHistory()
    {
        var queue = new Queue<string>(["a", "b"]);
        var module = Draining("mailpit", queue);
        var buffer = new ServiceLogBuffer();

        Assert.Equal(["a", "b"], await buffer.TailAsync(module, 100, CancellationToken.None));
        Assert.Equal(["a", "b"], await buffer.TailAsync(module, 100, CancellationToken.None));

        queue.Enqueue("c");
        Assert.Equal(["a", "b", "c"], await buffer.TailAsync(module, 100, CancellationToken.None));
        Assert.Equal(["b", "c"], await buffer.TailAsync(module, 2, CancellationToken.None));
    }

    [Fact]
    public async Task WindowModule_RepeatedLinesAreKeptOnce()
    {
        var window = new List<string> { "a", "b" };
        var module = new FakeModule("apache", max => window.TakeLast(max).ToList());
        var buffer = new ServiceLogBuffer();

        Assert.Equal(["a", "b"], await buffer.PullAsync(module, 200, CancellationToken.None));
        window.Add("c");
        Assert.Equal(["c"], await buffer.PullAsync(module, 200, CancellationToken.None));
        Assert.Equal(["a", "b", "c"], await buffer.TailAsync(module, 100, CancellationToken.None));
    }
}
