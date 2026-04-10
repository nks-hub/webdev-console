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
}
