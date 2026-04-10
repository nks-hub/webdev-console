using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Interfaces;

namespace NKS.WebDevConsole.Daemon.Plugin;

/// <summary>
/// Concrete IPluginContext passed to plugins during Initialize and StartAsync.
/// Wraps the application's IServiceProvider and ILoggerFactory.
/// </summary>
public sealed class PluginContext : IPluginContext
{
    private readonly ILoggerFactory _loggerFactory;

    public IServiceProvider ServiceProvider { get; }

    public PluginContext(IServiceProvider serviceProvider, ILoggerFactory loggerFactory)
    {
        ServiceProvider = serviceProvider;
        _loggerFactory = loggerFactory;
    }

    public ILogger<T> GetLogger<T>() => _loggerFactory.CreateLogger<T>();

    /// <summary>
    /// Creates a lightweight context for the pre-Build Initialize phase
    /// where only ILoggerFactory is available (no IServiceProvider yet).
    /// </summary>
    public static PluginContext ForInitPhase(ILoggerFactory loggerFactory)
    {
        // ServiceProvider is not yet available during Initialize;
        // plugins must not access it until StartAsync.
        return new PluginContext(null!, loggerFactory);
    }
}
