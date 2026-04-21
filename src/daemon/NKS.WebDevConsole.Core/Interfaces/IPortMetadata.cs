namespace NKS.WebDevConsole.Core.Interfaces;

/// <summary>
/// Describes a single TCP port owned by a plugin. Plugins opt-in by registering
/// concrete implementations as singleton services during Initialize():
///   services.AddSingleton&lt;IPortMetadata&gt;(sp =&gt; new MyPort(sp.GetRequiredService&lt;MyConfig&gt;()));
/// The daemon collects all registrations via GetServices&lt;IPortMetadata&gt;() and
/// surfaces them through GET /api/plugins/ports.
/// </summary>
public interface IPortMetadata
{
    /// <summary>Unique key like "apache.http" or "mysql.port". Used as a settings key.</summary>
    string Key { get; }

    /// <summary>Human-facing label shown in Settings &gt; Ports.</summary>
    string Label { get; }

    /// <summary>Default port value when the user hasn't overridden.</summary>
    int DefaultPort { get; }

    /// <summary>Current effective port (from settings if overridden, else DefaultPort).</summary>
    int CurrentPort { get; }

    /// <summary>True when the owning plugin is enabled and running. UI filters on this.</summary>
    bool IsActive { get; }

    /// <summary>Plugin id that owns this port, e.g. "nks.wdc.apache".</summary>
    string PluginId { get; }
}
