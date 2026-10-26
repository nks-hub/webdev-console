namespace NKS.WebDevConsole.Daemon.Config;

/// <summary>Origins allowed to read daemon responses cross-origin.</summary>
public static class DaemonCors
{
    public static bool IsAllowedOrigin(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        // Packaged Electron renderer (protocol.handle('app') in electron/main.ts).
        if (uri.Scheme == "app" && uri.Host == "nks-wdc") return true;
        // electron-vite dev server / browser preview on this machine.
        return (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && uri.IsLoopback;
    }
}
