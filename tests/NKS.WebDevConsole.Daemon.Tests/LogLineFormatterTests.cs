using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NKS.WebDevConsole.Daemon.Config;
using Xunit;

namespace NKS.WebDevConsole.Daemon.Tests;

public sealed class LogLineFormatterTests
{
    private static readonly DateTime Time = new(2026, 11, 24, 10, 11, 12, 345);

    [Fact]
    public void Format_ForgedLineInMessage_StaysOnOneLine()
    {
        var line = LogLineFormatter.Format(
            Time, LogLevel.Information, "Cat", "Uninstalled evil\r\n10:00:00.000 Error Fake forged entry", null);

        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.Contains("evil\\r\\n10:00:00.000 Error Fake forged entry", line);
    }

    [Fact]
    public void Format_PlainMessage_KeepsLayout()
    {
        var line = LogLineFormatter.Format(Time, LogLevel.Warning, "Cat", "hello", null);

        Assert.Equal("10:11:12.345 Warning     Cat hello", line);
    }

    [Fact]
    public void Format_Exception_IsAppended()
    {
        var line = LogLineFormatter.Format(Time, LogLevel.Error, "Cat", "failed", new InvalidOperationException("boom"));

        Assert.StartsWith("10:11:12.345 Error       Cat failed System.InvalidOperationException: boom", line);
    }

    [Fact]
    public void ConsoleFormatter_ForgedLineInMessage_WritesOneLine()
    {
        var writer = new StringWriter();
        var entry = new LogEntry<string>(
            LogLevel.Information, "Cat", new EventId(0), "state", null, (_, _) => "path /x\n12:00:00.000 Error forged");

        new SingleLineConsoleFormatter().Write(entry, null, writer);

        var lines = writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        Assert.Contains("/x\\n12:00:00.000 Error forged", lines[0]);
    }
}
