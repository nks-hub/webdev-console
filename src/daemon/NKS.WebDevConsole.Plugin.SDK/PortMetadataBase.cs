using NKS.WebDevConsole.Core.Interfaces;

namespace NKS.WebDevConsole.Plugin.SDK;

/// <summary>
/// Convenience base for IPortMetadata implementations. Subclass and override
/// CurrentPort / IsActive when settings or runtime state determine the live values.
/// </summary>
public abstract class PortMetadataBase : IPortMetadata
{
    public abstract string Key { get; }
    public abstract string Label { get; }
    public abstract int DefaultPort { get; }
    public virtual int CurrentPort => DefaultPort;
    public virtual bool IsActive => false;
    public abstract string PluginId { get; }
}

/// <summary>
/// Sample IPortMetadata implementations for the Apache plugin — serves as the
/// canonical pattern for task 15 when other plugins wire up their own ports.
/// Not registered automatically; ApachePlugin.Initialize() must call
///   services.AddSingleton&lt;IPortMetadata, ApacheHttpPort&gt;();
///   services.AddSingleton&lt;IPortMetadata, ApacheHttpsPort&gt;();
/// (task 15 will do this; these classes live here so the pattern is reviewable now.)
/// </summary>
public sealed class ApacheHttpPort : PortMetadataBase
{
    private readonly Func<int>? _currentPort;
    private readonly Func<bool>? _isActive;

    public ApacheHttpPort(Func<int>? currentPort = null, Func<bool>? isActive = null)
    {
        _currentPort = currentPort;
        _isActive = isActive;
    }

    public override string Key => "apache.http";
    public override string Label => "HTTP";
    public override int DefaultPort => 80;
    public override int CurrentPort => _currentPort?.Invoke() ?? DefaultPort;
    public override bool IsActive => _isActive?.Invoke() ?? false;
    public override string PluginId => "nks.wdc.apache";
}

public sealed class ApacheHttpsPort : PortMetadataBase
{
    private readonly Func<int>? _currentPort;
    private readonly Func<bool>? _isActive;

    public ApacheHttpsPort(Func<int>? currentPort = null, Func<bool>? isActive = null)
    {
        _currentPort = currentPort;
        _isActive = isActive;
    }

    public override string Key => "apache.https";
    public override string Label => "HTTPS";
    public override int DefaultPort => 443;
    public override int CurrentPort => _currentPort?.Invoke() ?? DefaultPort;
    public override bool IsActive => _isActive?.Invoke() ?? false;
    public override string PluginId => "nks.wdc.apache";
}
