namespace NKS.WebDevConsole.Daemon.Services;

public sealed record BootEvent(string Stage, string Message, DateTime Timestamp, int Percent, string? Error);

public sealed class BootProgressService
{
    private readonly List<BootEvent> _events = new();
    private readonly object _lock = new();

    public string CurrentStage { get; private set; } = "starting";
    public int Percent { get; private set; } = 0;
    public string? LastError { get; private set; }

    public void Report(string stage, string message, int percent, string? error = null)
    {
        lock (_lock)
        {
            CurrentStage = stage;
            Percent = percent;
            if (error != null) LastError = error;
            _events.Add(new BootEvent(stage, message, DateTime.UtcNow, percent, error));
            if (_events.Count > 100) _events.RemoveAt(0);
        }
    }

    public IReadOnlyList<BootEvent> Events
    {
        get { lock (_lock) return _events.ToList(); }
    }
}
