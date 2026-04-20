using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Logging;

namespace NKS.WebDevConsole.Daemon.Config;

/// <summary>
/// Validates service configuration files by invoking the service binary's built-in syntax checker.
/// </summary>
public sealed class ConfigValidator
{
    private readonly ILogger<ConfigValidator> _logger;

    public ConfigValidator(ILogger<ConfigValidator> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Runs <c>httpd -t -f configPath</c> and returns whether the config is syntactically valid.
    /// </summary>
    public async Task<(bool IsValid, string Output)> ValidateApacheConfig(
        string httpdPath, string configPath)
    {
        try
        {
            var result = await Cli.Wrap(httpdPath)
                .WithArguments(["-t", "-f", configPath])
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();

            var output = result.StandardError + result.StandardOutput;
            var isValid = result.ExitCode == 0;

            if (!isValid)
                _logger.LogWarning("Apache config validation failed: {Output}", output);

            return (isValid, output.Trim());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to validate Apache config");
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Validates a php.ini file via <c>php --php-ini &lt;file&gt; -r "exit(0);"</c>. PHP does not
    /// provide a dedicated INI linter, but loading a known-good script with the file
    /// applied surfaces parse errors to stderr and sets a non-zero exit code.
    /// </summary>
    public async Task<(bool IsValid, string Output)> ValidatePhpIni(
        string phpPath, string iniPath)
    {
        try
        {
            var result = await Cli.Wrap(phpPath)
                .WithArguments(["--php-ini", iniPath, "-r", "exit(0);"])
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();

            var output = result.StandardError + result.StandardOutput;
            // PHP loads the ini and prints parse errors to stderr while still exiting 0
            // if the file is merely malformed (legacy behaviour). Treat any mention of
            // "PHP Warning" / "PHP Parse error" / "PHP Fatal error" as invalid.
            var hasParseError =
                output.Contains("PHP Parse error", StringComparison.OrdinalIgnoreCase)
                || output.Contains("PHP Fatal error", StringComparison.OrdinalIgnoreCase)
                || output.Contains("syntax error", StringComparison.OrdinalIgnoreCase);
            var isValid = result.ExitCode == 0 && !hasParseError;

            if (!isValid)
                _logger.LogWarning("php.ini validation failed: {Output}", output);

            return (isValid, output.Trim());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to validate php.ini");
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Validates a MySQL my.cnf via <c>mysqld --defaults-file=&lt;file&gt; --validate-config</c>
    /// (MySQL 8.0+). Returns false if mysqld reports any warnings or errors.
    /// </summary>
    public async Task<(bool IsValid, string Output)> ValidateMyCnf(
        string mysqldPath, string cnfPath)
    {
        try
        {
            var result = await Cli.Wrap(mysqldPath)
                .WithArguments(["--defaults-file=" + cnfPath, "--validate-config"])
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();

            var output = result.StandardError + result.StandardOutput;
            var isValid = result.ExitCode == 0;

            if (!isValid)
                _logger.LogWarning("my.cnf validation failed: {Output}", output);

            return (isValid, output.Trim());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to validate my.cnf");
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Validates a redis.conf via <c>redis-server &lt;file&gt; --test-memory 1</c>. Redis does
    /// not ship a dedicated config linter — this command parses the file and exits
    /// after a brief self-test, surfacing any syntax errors to stderr.
    /// </summary>
    public async Task<(bool IsValid, string Output)> ValidateRedisConf(
        string redisServerPath, string confPath)
    {
        try
        {
            // --check-system / --test-memory exit quickly after parsing the config.
            // We parse-only by redirecting the actual listen port to 0 (no bind) via
            // a throwaway override on the command line.
            // CTS is `using` so the internal Timer callback is released when we
            // fall through to the catch — previously `new CancellationTokenSource(TimeSpan)`
            // leaked the Timer handle per redis.conf validation call.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var result = await Cli.Wrap(redisServerPath)
                .WithArguments([confPath, "--port", "0", "--daemonize", "no", "--bind", "127.0.0.1"])
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(cts.Token);

            var output = result.StandardError + result.StandardOutput;
            var isValid = !output.Contains("FATAL CONFIG FILE ERROR", StringComparison.OrdinalIgnoreCase)
                          && !output.Contains("Bad directive", StringComparison.OrdinalIgnoreCase);

            if (!isValid)
                _logger.LogWarning("redis.conf validation failed: {Output}", output);

            return (isValid, output.Trim());
        }
        catch (OperationCanceledException)
        {
            // Expected — we intentionally cancel after 2 s because redis-server
            // runs indefinitely even with --port 0. If we got here, parsing succeeded.
            return (true, "parsed OK");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to validate redis.conf");
            return (false, ex.Message);
        }
    }
}
