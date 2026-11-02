namespace NKS.WebDevConsole.Daemon.Plugin;

/// <summary>Matches a manifest's <c>supportedPlatforms</c> (windows/macos/linux) against this OS.</summary>
internal static class PluginPlatform
{
    public static string Current =>
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

    /// <summary>An empty or missing list means "any platform".</summary>
    public static bool IsSupported(IEnumerable<string>? platforms, string? current = null)
    {
        if (platforms is null) return true;
        var list = platforms.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return list.Count == 0 || list.Contains(current ?? Current, StringComparer.OrdinalIgnoreCase);
    }
}
