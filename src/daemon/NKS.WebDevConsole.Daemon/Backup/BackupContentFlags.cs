namespace NKS.WebDevConsole.Daemon.Backup;

/// <summary>
/// Selects which content categories are included in a backup archive.
/// Flags can be ORed together. <see cref="Default"/> covers the three
/// categories that are always safe and cheap to pack.
/// </summary>
[Flags]
public enum BackupContentFlags
{
    None        = 0,
    Vhosts      = 1 << 0,
    PluginConfigs = 1 << 1,
    Ssl         = 1 << 2,
    Databases   = 1 << 3,
    Docroots    = 1 << 4,
    Binaries    = 1 << 5,

    Default     = Vhosts | PluginConfigs | Ssl,
    All         = Vhosts | PluginConfigs | Ssl | Databases | Docroots,
}
