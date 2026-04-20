using System.Runtime.InteropServices;

namespace NKS.WebDevConsole.Daemon.Sites;

/// <summary>
/// F65: Windows MAX_PATH preflight for the site-duplicate endpoint.
/// Extracted from <c>/api/sites/{domain}/duplicate</c> handler so the
/// guard can be unit-tested without spinning up a WebApplication.
/// </summary>
public static class SiteDuplicatePreflight
{
    /// <summary>Windows MAX_PATH is 260; we cap at 259 because the API
    /// requires a null terminator internally on ANSI paths. Pure magic-
    /// number, matches the value used in the endpoint handler.</summary>
    public const int MaxWindowsPath = 259;

    /// <summary>
    /// Checks whether duplicating <paramref name="sourceRoot"/> to
    /// <paramref name="newRoot"/> would emit any file path exceeding
    /// <see cref="MaxWindowsPath"/> chars. Returns the offending source
    /// path when violation detected, null otherwise. Scan failures
    /// (permission denied, IO errors) fall back to no-violation so the
    /// original copy attempt can still proceed — identical behaviour to
    /// the endpoint's try/catch.
    /// </summary>
    public static string? FindPathTooLong(string sourceRoot, string newRoot, IEnumerable<string>? entryEnumerator = null)
    {
        // Only Windows is affected. Caller should skip when running on Unix.
        var enumerator = entryEnumerator ?? SafeEnumerate(sourceRoot);
        var prefixDelta = newRoot.Length - sourceRoot.Length;
        string? longest = null;
        foreach (var path in enumerator)
        {
            if (longest is null || path.Length > longest.Length)
                longest = path;
        }
        if (longest is null) return null;
        return longest.Length + prefixDelta > MaxWindowsPath ? longest : null;
    }

    /// <summary>Wraps <c>Directory.EnumerateFileSystemEntries</c> so IO
    /// faults short-circuit to an empty sequence instead of bubbling up
    /// out of the preflight scan.</summary>
    private static IEnumerable<string> SafeEnumerate(string root)
    {
        IEnumerable<string> seq;
        try
        {
            seq = Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories);
        }
        catch
        {
            yield break;
        }
        foreach (var p in seq) yield return p;
    }

    /// <summary>Shortcut used by the endpoint: only runs the scan on
    /// Windows when the copy isn't "empty" (nothing to copy).</summary>
    public static bool ShouldPreflight(string copyFiles) =>
        OperatingSystem.IsWindows() && copyFiles != "empty";
}
