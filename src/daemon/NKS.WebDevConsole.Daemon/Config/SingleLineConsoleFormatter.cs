using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace NKS.WebDevConsole.Daemon.Config;

/// <summary>
/// Console formatter that writes every entry through <see cref="LogLineFormatter"/>.
/// The desktop app relays the daemon's console output into its own log file, so the
/// console needs the same CR/LF handling as the daemon log file.
/// </summary>
public sealed class SingleLineConsoleFormatter : ConsoleFormatter
{
    public const string FormatterName = "wdc-single-line";

    public SingleLineConsoleFormatter() : base(FormatterName) { }

    public override void Write<TState>(
        in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception);
        if (message is null && logEntry.Exception is null) return;
        textWriter.WriteLine(LogLineFormatter.Format(
            DateTime.Now, logEntry.LogLevel, logEntry.Category, message, logEntry.Exception));
    }
}
