using Microsoft.Extensions.Logging;

namespace NKS.WebDevConsole.Daemon.Config;

/// <summary>
/// Formats one line of the daemon log file. Log message arguments carry values from
/// API requests (domains, service ids, paths); a CR or LF inside one would start a
/// fake line in the file, which the log viewer in the app reads back as a record.
/// The message is therefore written on a single line; the exception text keeps its
/// own line breaks, because stack traces need them.
/// </summary>
public static class LogLineFormatter
{
    /// <summary>The text with CR and LF replaced by their escaped forms.</summary>
    public static string SingleLine(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : text.Replace("\r", "\\r").Replace("\n", "\\n");

    public static string Format(DateTime time, LogLevel level, string category, string? message, Exception? exception) =>
        $"{time:HH:mm:ss.fff} {level,-11} {SingleLine(category)} {SingleLine(message)} {exception?.ToString() ?? ""}".Trim();
}
