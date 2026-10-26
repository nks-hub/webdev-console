using NKS.WebDevConsole.Daemon.Data;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// The import hardening option is picked from the client's own --help text.
/// These pin the selection so a client that lacks the option still imports
/// (no unknown-option failure) and DELIMITER-disabling options never appear.
/// </summary>
public sealed class MysqlClientHardeningTests
{
    private const string MariaDbHelp = """
        mysql from 11.4.2-MariaDB, client 15.2 for Win64 (AMD64)
          -s, --silent        Be more silent. Print results with a tab as separator,
          --sandbox           Disallow commands that access the file system (except \P
                              without an argument and \e).
          -t, --table         Output in table format.

        Variables (--variable-name=value)
        sandbox                           FALSE
        """;

    private const string MySqlHelp = """
        mysql  Ver 8.4.3 for Win64 on x86_64 (MySQL Community Server - GPL)
          --commands          Enable or disable processing of local mysql commands.
          --system-command    Enable or disable (by default) the 'system' mysql
                              command.
          -t, --table         Output in table format.
        """;

    private const string OldClientHelp = """
        mysql  Ver 8.0.30 for Linux on x86_64
          -t, --table         Output in table format.
        Variables (--variable-name=value)
        table                             FALSE
        """;

    [Fact]
    public void MariaDb_client_gets_sandbox()
        => Assert.Equal(new[] { "--sandbox" }, MysqlClientHardening.ArgsFromHelp(MariaDbHelp));

    [Fact]
    public void MySql_client_gets_system_command_off_and_keeps_commands()
    {
        var args = MysqlClientHardening.ArgsFromHelp(MySqlHelp);
        Assert.Equal(new[] { "--system-command=OFF" }, args);
        Assert.DoesNotContain(args, a => a.StartsWith("--commands", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(OldClientHelp)]
    [InlineData("")]
    [InlineData("sandbox mentioned in prose --sandbox inline only")]
    public void Unknown_or_old_client_gets_no_extra_args(string help)
        => Assert.Empty(MysqlClientHardening.ArgsFromHelp(help));

    [Fact]
    public async Task Missing_client_resolves_to_no_args()
        => Assert.Empty(await MysqlClientHardening.ResolveAsync(
            Path.Combine(Path.GetTempPath(), "wdc-no-such-mysql-" + Guid.NewGuid().ToString("N")), CancellationToken.None));
}
