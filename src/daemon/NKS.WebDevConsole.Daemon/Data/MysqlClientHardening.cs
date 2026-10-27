using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using CliWrap.Buffered;

namespace NKS.WebDevConsole.Daemon.Data;

/// <summary>
/// The mysql command-line client also executes its own client commands
/// (system, source, tee, ...) when they appear in piped input, so an
/// imported dump could run a shell command or write files on this machine.
/// Newer clients can switch those off; which option exists depends on the
/// client, so it is read from the installed client's own --help text and
/// passed only when listed:
///   MariaDB  --sandbox                 (no command that touches the file system)
///   MySQL    --system-command=OFF      (8.0.40+, no shell command)
/// --commands=OFF (MySQL 8.4+) is deliberately not used: it also disables
/// DELIMITER, which mysqldump output relies on for routines and triggers.
/// </summary>
internal static partial class MysqlClientHardening
{
    private static readonly ConcurrentDictionary<string, string[]> Cache = new(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"^\s+--sandbox\b", RegexOptions.Multiline)]
    private static partial Regex SandboxOption();

    [GeneratedRegex(@"^\s+--system-command\b", RegexOptions.Multiline)]
    private static partial Regex SystemCommandOption();

    /// <summary>Hardening arguments supported according to a client's --help text.</summary>
    public static string[] ArgsFromHelp(string helpText)
    {
        if (string.IsNullOrEmpty(helpText)) return Array.Empty<string>();
        if (SandboxOption().IsMatch(helpText)) return new[] { "--sandbox" };
        if (SystemCommandOption().IsMatch(helpText)) return new[] { "--system-command=OFF" };
        return Array.Empty<string>();
    }

    /// <summary>Hardening arguments for the given client binary (cached per path and file time).</summary>
    public static async Task<string[]> ResolveAsync(string mysqlCli, CancellationToken ct)
    {
        var stamp = File.Exists(mysqlCli) ? File.GetLastWriteTimeUtc(mysqlCli).Ticks : 0;
        var key = $"{mysqlCli}|{stamp}";
        if (Cache.TryGetValue(key, out var cached)) return cached;
        string[] args;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            var help = await CliWrap.Cli.Wrap(mysqlCli)
                .WithArguments(new[] { "--help" })
                .WithValidation(CliWrap.CommandResultValidation.None)
                .ExecuteBufferedAsync(cts.Token);
            args = ArgsFromHelp(help.StandardOutput);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            args = Array.Empty<string>();
        }
        Cache[key] = args;
        return args;
    }
}
