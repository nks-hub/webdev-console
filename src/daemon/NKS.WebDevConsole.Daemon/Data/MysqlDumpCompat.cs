using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using CliWrap;

namespace NKS.WebDevConsole.Daemon.Data;

/// <summary>
/// Keeps MySQL dumps importable on the same kind of server: backups and the
/// Databases page export dump without the GTID set, and restores and imports
/// drop the server-state statements older dumps still carry.
/// </summary>
internal static class MysqlDumpCompat
{
    // MySQL's mysqldump writes the server's GTID set into the dump, and
    // importing it into a server with GTIDs fails (ERROR 3546, the set overlaps
    // GTID_EXECUTED). MariaDB's mysqldump has no such option, so it is passed
    // only when the client's --help lists it, like MysqlClientHardening does.
    private static readonly Regex SetGtidPurgedOption = new(@"^\s+--set-gtid-purged\b", RegexOptions.Multiline | RegexOptions.CultureInvariant);
    private static readonly ConcurrentDictionary<string, string[]> DumpOptionsCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>mysqldump options supported according to its --help text.</summary>
    internal static string[] DumpOptionsFromHelp(string? helpText) =>
        helpText is not null && SetGtidPurgedOption.IsMatch(helpText) ? ["--set-gtid-purged=OFF"] : [];

    /// <summary>Those options for one mysqldump binary, read once per binary and file time.</summary>
    internal static string[] DumpOptions(string mysqldump)
    {
        var key = $"{mysqldump}|{File.GetLastWriteTimeUtc(mysqldump).Ticks}";
        return DumpOptionsCache.GetOrAdd(key, _ =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var help = new StringBuilder();
                Cli.Wrap(mysqldump)
                    .WithArguments(["--help"])
                    .WithStandardOutputPipe(PipeTarget.ToStringBuilder(help))
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteAsync(cts.Token)
                    .GetAwaiter().GetResult();
                return DumpOptionsFromHelp(help.ToString());
            }
            catch (Exception)
            {
                return [];
            }
        });
    }

    // Statements that replay the source server's state rather than data. Dumps
    // made before --set-gtid-purged=OFF still carry them, and they fail on
    // import (GTID set overlap) or need privileges a restore does not.
    private static readonly byte[] GtidPurgedPrefix = "SET @@GLOBAL.GTID_PURGED"u8.ToArray();
    private static readonly byte[] SqlLogBinPrefix = "SET @@SESSION.SQL_LOG_BIN"u8.ToArray();

    /// <summary>
    /// Copies a mysqldump file without the <c>SET @@GLOBAL.GTID_PURGED</c>
    /// statement (which can span several lines, one per server UUID) and the
    /// <c>SET @@SESSION.SQL_LOG_BIN</c> lines. Works on bytes, not text: a
    /// dump without --hex-blob holds binary column data that is not valid UTF-8.
    /// </summary>
    internal static async Task CopyWithoutServerStateAsync(Stream input, Stream output, CancellationToken ct)
    {
        var line = new MemoryStream();
        var buffer = new byte[81920];
        var inGtidStatement = false;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            var start = 0;
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] != (byte)'\n') continue;
                line.Write(buffer, start, i - start + 1);
                await EmitAsync();
                start = i + 1;
            }
            line.Write(buffer, start, read - start);
        }
        if (line.Length > 0) await EmitAsync();

        async Task EmitAsync()
        {
            var bytes = line.GetBuffer().AsMemory(0, (int)line.Length);
            if (inGtidStatement || StartsWithIgnoreCase(bytes.Span, GtidPurgedPrefix))
            {
                // The statement ends at the line that ends with ";".
                inGtidStatement = !bytes.Span.TrimEnd("\r\n \t"u8).EndsWith(";"u8);
            }
            else if (!StartsWithIgnoreCase(bytes.Span, SqlLogBinPrefix))
            {
                await output.WriteAsync(bytes, ct);
            }
            line.SetLength(0);
        }
    }

    private static bool StartsWithIgnoreCase(ReadOnlySpan<byte> line, ReadOnlySpan<byte> prefix) =>
        line.Length >= prefix.Length && Ascii.EqualsIgnoreCase(line[..prefix.Length], prefix);
}
