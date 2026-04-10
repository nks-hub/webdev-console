using DbUp;
using Microsoft.Extensions.Logging;

namespace NKS.WebDevConsole.Daemon.Data;

public class MigrationRunner
{
    private readonly ILogger<MigrationRunner> _logger;

    public MigrationRunner(ILogger<MigrationRunner> logger)
    {
        _logger = logger;
    }

    public bool Run(string connectionString)
    {
        var upgrader = DeployChanges.To
            .SqliteDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(typeof(MigrationRunner).Assembly,
                s => s.Contains(".Migrations."))
            .LogToConsole()
            .Build();

        if (!upgrader.IsUpgradeRequired())
        {
            _logger.LogInformation("Database is up to date");
            return true;
        }

        var result = upgrader.PerformUpgrade();
        if (!result.Successful)
        {
            _logger.LogError(result.Error, "Migration failed");
            return false;
        }

        _logger.LogInformation("Database migrated successfully");
        return true;
    }
}
