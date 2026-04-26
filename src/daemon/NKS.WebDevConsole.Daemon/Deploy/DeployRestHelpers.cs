namespace NKS.WebDevConsole.Daemon.Deploy;

/// <summary>
/// Phase 7.5 — pure helpers used by the /api/nks.wdc.deploy/* routes.
/// Extracted from Program.cs top-level statements so the test project can
/// import them directly. Both methods are total functions: no I/O, no
/// network. The settings path helper still touches Directory.CreateDirectory
/// at call sites in Program.cs — that side-effect lives there, not here.
/// </summary>
public static class DeployRestHelpers
{
    /// <summary>
    /// Map a DeployRunRow.status to the frontend's DeployPhase enum string.
    /// Conservative: anything we don't recognise becomes "Unknown" so the
    /// frontend can still render a tag. Case-insensitive on the input —
    /// historic rows used Title case before migration 006 settled on
    /// lowercase, and we want both to project cleanly.
    /// </summary>
    public static string MapStatusToPhase(string? status) => status?.ToLowerInvariant() switch
    {
        "queued" => "Queued",
        "running" => "Building",
        "awaiting_soak" => "AwaitingSoak",
        "completed" => "Done",
        "failed" => "Failed",
        "cancelled" => "Cancelled",
        "rolling_back" => "RollingBack",
        "rolled_back" => "RolledBack",
        _ => "Unknown",
    };

    /// <summary>
    /// Sanitise a domain string for use as a filename — letters / digits /
    /// dot / dash / underscore only, everything else replaced with '_'.
    /// Caller is responsible for prepending the deploy-settings dir.
    /// Stops a domain like "evil/../etc/passwd" from escaping the
    /// settings directory; ASP.NET routing already rejects '/' in the
    /// route value but defence in depth is cheap.
    /// </summary>
    public static string SanitiseDomainForFilename(string domain)
    {
        if (string.IsNullOrEmpty(domain)) return "_";
        return System.Text.RegularExpressions.Regex.Replace(
            domain, "[^a-zA-Z0-9._-]", "_");
    }
}
