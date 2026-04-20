namespace NKS.WebDevConsole.Daemon.Services;

/// <summary>
/// Small helpers for parsing boolean-ish environment variables with the
/// conventions used across the daemon. Centralising these keeps the
/// accepted truthy surface (1 / true / yes / on, case-insensitive,
/// whitespace-tolerant) in one place instead of scattered ad-hoc
/// comparisons that each get whitespace/trim edge-cases slightly wrong.
/// </summary>
internal static class EnvFlags
{
    /// <summary>
    /// Returns <c>true</c> when <paramref name="raw"/> is a commonly-used
    /// truthy token ("1", "true", "yes", "on") after trimming. Null,
    /// empty, whitespace-only, or unknown values return <c>false</c>.
    /// </summary>
    public static bool IsTruthy(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var v = raw.Trim();
        return v.Equals("1", System.StringComparison.Ordinal)
            || v.Equals("true", System.StringComparison.OrdinalIgnoreCase)
            || v.Equals("yes", System.StringComparison.OrdinalIgnoreCase)
            || v.Equals("on", System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Convenience overload that reads the env var and delegates to
    /// <see cref="IsTruthy(string?)"/>.
    /// </summary>
    public static bool IsTruthy(System.Func<string, string?> reader, string name)
        => IsTruthy(reader(name));
}
