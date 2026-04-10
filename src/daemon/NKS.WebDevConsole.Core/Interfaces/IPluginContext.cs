using Microsoft.Extensions.Logging;

namespace NKS.WebDevConsole.Core.Interfaces;

public interface IPluginContext
{
    IServiceProvider ServiceProvider { get; }
    ILogger<T> GetLogger<T>();
}
