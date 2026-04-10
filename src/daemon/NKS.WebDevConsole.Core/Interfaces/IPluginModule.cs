namespace NKS.WebDevConsole.Core.Interfaces;

public interface IPluginModule
{
    string Id { get; }
    string Name { get; }
    string Version { get; }
    Task InitializeAsync(CancellationToken ct);
}
