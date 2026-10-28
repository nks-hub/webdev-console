using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CliWrap;
using NKS.WebDevConsole.Core.Services;
using NKS.WebDevConsole.Daemon.Binaries;
using NKS.WebDevConsole.Daemon.Data;
using NKS.WebDevConsole.Daemon.Services;

namespace NKS.WebDevConsole.Daemon.Backup;

/// <summary>
/// MySQL side of backups: dumps for <see cref="BackupContentFlags.Databases"/>
/// and the opt-in import on restore. Every method throws
/// <see cref="InvalidOperationException"/> with the reason (MySQL not
/// installed, server not running, access denied), so the caller can say what
/// it left out instead of skipping it silently.
/// </summary>
public interface IMysqlBackupClient
{
    /// <summary>User databases on the WDC MySQL server, without the system schemas.</summary>
    IReadOnlyList<string> ListDatabases();

    /// <summary>Writes <c>mysqldump --databases <paramref name="database"/></c> to <paramref name="output"/>.</summary>
    void Dump(string database, Stream output);

    /// <summary>Creates <paramref name="database"/> when missing, then runs the SQL from <paramref name="sql"/> in it.</summary>
    void Import(string database, Stream sql);
}

/// <summary>Runs the mysql and mysqldump clients of the installed WDC MySQL as root.</summary>
public sealed class MysqlBackupClient(BinaryManager binaries, SettingsStore settings) : IMysqlBackupClient
{
    private static readonly HashSet<string> SystemSchemas = new(StringComparer.OrdinalIgnoreCase)
    {
        "information_schema", "performance_schema", "sys", "mysql",
    };

    // Names reach the command line (and, on restore, come from archive entry
    // names): safe inside backticks, and no leading "-" that would read as an option.
    private static readonly Regex SafeName = new(@"^[A-Za-z0-9_][A-Za-z0-9_\-]{0,63}$", RegexOptions.CultureInvariant);

    /// <summary>True for a database name backups handle: letters, digits, _ and -, at most 64, not starting with -.</summary>
    public static bool IsSafeDatabaseName(string? name) => name is not null && SafeName.IsMatch(name);

    public IReadOnlyList<string> ListDatabases()
    {
        var output = new StringBuilder();
        Run("mysql", ListArgs(), PipeTarget.ToStringBuilder(output));
        return output.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(name => !SystemSchemas.Contains(name))
            .ToList();
    }

    public void Dump(string database, Stream output) =>
        Run("mysqldump", DumpArgs(database), PipeTarget.ToStream(output), options: MysqlDumpCompat.DumpOptions);

    public void Import(string database, Stream sql)
    {
        if (!IsSafeDatabaseName(database))
            throw new InvalidOperationException($"'{database}' is not a database name restore accepts");
        Run("mysql", CreateDatabaseArgs(database), PipeTarget.Null);
        // The archive may come from another machine: the client's own commands
        // (system, source, ...) are switched off where the client allows it.
        Run("mysql", ImportArgs(database), PipeTarget.Null,
            PipeSource.Create((destination, ct) => MysqlDumpCompat.CopyWithoutServerStateAsync(sql, destination, ct)),
            options: exe => MysqlClientHardening.ResolveAsync(exe, CancellationToken.None).GetAwaiter().GetResult());
    }

    internal static string[] ListArgs() => ["--connect-timeout=5", "-N", "-e", "SHOW DATABASES"];

    internal static string[] DumpArgs(string database) =>
        ["--single-transaction", "--routines", "--events", "--triggers", "--databases", database];

    internal static string[] CreateDatabaseArgs(string database) =>
        ["--connect-timeout=5", "-e", $"CREATE DATABASE IF NOT EXISTS `{database}`"];

    internal static string[] ImportArgs(string database) => ["--connect-timeout=5", database];

    /// <summary>The client executable next to the installed mysql binary.</summary>
    internal static string ToolPath(string mysqlExecutable, string tool) =>
        Path.Combine(Path.GetDirectoryName(mysqlExecutable)!, OperatingSystem.IsWindows() ? tool + ".exe" : tool);

    /// <summary>
    /// The full client invocation: executable next to the installed mysql,
    /// root on 127.0.0.1:<paramref name="port"/>, then <paramref name="options"/>
    /// and <paramref name="toolArgs"/>, and the password in MYSQL_PWD, never on
    /// the command line (it shows in process lists).
    /// </summary>
    internal static (string Exe, List<string> Args, Dictionary<string, string?> Env) Command(
        string mysqlExecutable, string tool, int port, string? password, IEnumerable<string> toolArgs,
        IEnumerable<string>? options = null)
    {
        var args = new List<string>
        {
            "-h", "127.0.0.1",
            "-P", port.ToString(CultureInfo.InvariantCulture),
            "-u", "root",
        };
        args.AddRange(options ?? []);
        args.AddRange(toolArgs);
        var env = string.IsNullOrEmpty(password)
            ? new Dictionary<string, string?>()
            : new Dictionary<string, string?> { ["MYSQL_PWD"] = password };
        return (ToolPath(mysqlExecutable, tool), args, env);
    }

    private void Run(string tool, IEnumerable<string> toolArgs, PipeTarget stdout, PipeSource? stdin = null,
        Func<string, string[]>? options = null)
    {
        var mysql = binaries.ListInstalled("mysql").FirstOrDefault(b => b.Executable is not null)
            ?? throw new InvalidOperationException("MySQL is not installed");
        var exe = ToolPath(mysql.Executable!, tool);
        if (!File.Exists(exe))
            throw new InvalidOperationException($"{Path.GetFileName(exe)} not found in {Path.GetDirectoryName(exe)}");
        var (_, args, env) = Command(mysql.Executable!, tool, settings.MysqlPort, MySqlRootPassword.TryRead(),
            toolArgs, options?.Invoke(exe));

        var stderr = new StringBuilder();
        var result = Cli.Wrap(exe)
            .WithArguments(args)
            .WithEnvironmentVariables(env)
            .WithStandardInputPipe(stdin ?? PipeSource.Null)
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
