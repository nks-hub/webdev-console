namespace NKS.WebDevConsole.Daemon.Sites;

/// <summary>One step of <see cref="SiteOrchestrator.ApplyAsync"/>.</summary>
public sealed record SiteApplyStep(string Name, string Status, string? Detail)
{
    public const string Succeeded = "ok";
    public const string Skipped = "skipped";
    public const string Failed = "failed";
}

/// <summary>
/// Outcome of a site apply. Steps are best-effort (a missing mkcert must not
/// block the vhost), so the apply as a whole still completes; this records
/// which steps did not, for the API response and the <c>site:applied</c> event.
/// </summary>
public sealed class SiteApplyResult
{
    private readonly List<SiteApplyStep> _steps = new();

    public SiteApplyResult(string domain) => Domain = domain;

    public string Domain { get; }
    public IReadOnlyList<SiteApplyStep> Steps => _steps;
    public bool Success => _steps.All(s => s.Status != SiteApplyStep.Failed);

    /// <summary>When the apply finished (ms since the Unix epoch), so a UI that connects later can show its age.</summary>
    public long At { get; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>JSON shape shared by the <c>site:applied</c> event, the apply endpoint and the apply-status list.</summary>
    public object ToPayload() => new
    {
        domain = Domain,
        success = Success,
        at = At,
        steps = _steps.Select(st => new { name = st.Name, status = st.Status, detail = st.Detail }).ToList(),
    };

    public void Ok(string step, string? detail = null) => _steps.Add(new(step, SiteApplyStep.Succeeded, detail));
    public void Skip(string step, string? detail = null) => _steps.Add(new(step, SiteApplyStep.Skipped, detail));
    public void Fail(string step, string detail) => _steps.Add(new(step, SiteApplyStep.Failed, detail));
}
