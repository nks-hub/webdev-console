using System.Globalization;
using System.Text;
using CliWrap;
using NKS.WebDevConsole.Core.Services;
using NKS.WebDevConsole.Daemon.Binaries;
using NKS.WebDevConsole.Daemon.Services;

namespace NKS.WebDevConsole.Daemon.Backup;

/// <summary>
/// Dumps MySQL databases for <see cref="BackupContentFlags.Databases"/>. Both
/// methods throw <see cref="InvalidOperationException"/> with the reason
/// (MySQL not installed, server not running, access denied), so a backup can
/// say what it left out instead of skipping it silently.
/// </summary>
public interface IMysqlBackupDumper
{
    /// <summary>User databases on the WDC MySQL server, without the system schemas.</summary>
    IReadOnlyList<string> ListDatabases();

    /// <summary>Writes <c>mysqldump --databases <paramref name="database"/></c> to <paramref name="output"/>.</summary>
    void Dump(string database, Stream output);
}

/// <summary>Runs the mysql and mysqldump clients of the installed WDC MySQL as root.</summary>
public sealed class MysqlBackupDumper(BinaryManager binaries, SettingsStore settings) : IMysqlBackupDumper
{
    private static readonly HashSet<string> SystemSchemas = new(StringComparer.OrdinalIgnoreCase)
    {
        "information_schema", "performance_schema", "sys", "mysql",
    };

    public IReadOnlyList<string> ListDatabases()
    {
        var output = new StringBuilder();
        Run("mysql", ["--connect-timeout=5", "-N", "-e", "SHOW DATABASES"], PipeTarget.ToStringBuilder(output));
        return output.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(name => !SystemSchemas.Contains(name))
            .ToList();
    }

    public void Dump(string database, Stream output) =>
        Run("mysqldump",
            ["--single-transaction", "--routines", "--events", "--triggers", "--databases", database],
            PipeTarget.ToStream(output));

    private void Run(string tool, IEnumerable<string> toolArgs, PipeTarget stdout)
    {
        var mysql = binaries.ListInstalled("mysql").FirstOrDefault(b => b.Executable is not null)
            ?? throw new InvalidOperationException("MySQL is not installed");
        var exe = Path.Combine(Path.GetDirectoryName(mysql.Executable)!,
            OperatingSystem.IsWindows() ? tool + ".exe" : tool);
        if (!File.Exists(exe))
            throw new InvalidOperationException($"{Path.GetFileName(exe)} not found in {Path.GetDirectoryName(exe)}");

        var args = new List<string>
        {
            "-h", "127.0.0.1",
            "-P", settings.MysqlPort.ToString(CultureInfo.InvariantCulture),
            "-u", "root",
        };
        args.AddRange(toolArgs);
        // MYSQL_PWD, not -p<password>: the command line shows in process lists.
        var password = MySqlRootPassword.TryRead();
        var env = string.IsNullOrEmpty(password)
            ? new Dictionary<string, string?>()
            : new Dictionary<string, string?> { ["MYSQL_PWD"] = password };

        var stderr = new StringBuilder();
        var result = Cli.Wrap(exe)
            .WithArguments(args)
            .WithEnvironmentVariables(env)
            .WithStandardOutputPipe(stdout)
            .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stderr))
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync()
            .GetAwaiter().GetResult();
        if (result.ExitCode != 0)
        {
            var message = stderr.ToString().Trim();
            throw new InvalidOperationException(message.Length > 0 ? message : $"{tool} exited with code {result.ExitCode}");
        }
    }
}
