namespace NKS.WebDevConsole.Daemon.Config;

/// <summary>
/// Containment check for user-supplied paths. A bare
/// <c>candidate.StartsWith(root)</c> also accepts sibling folders that share the
/// prefix (<c>~/.wdc/backups-old</c> for root <c>~/.wdc/backups</c>), so the root
/// is compared with a trailing separator. Comparison is case-insensitive on
/// Windows and macOS (default file systems) and ordinal on Linux.
/// </summary>
public static class PathContainment
{
    public static bool IsUnder(string root, string candidate)
    {
        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var candidateFull = Path.GetFullPath(candidate);
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return candidateFull.StartsWith(rootFull, comparison);
    }
}
