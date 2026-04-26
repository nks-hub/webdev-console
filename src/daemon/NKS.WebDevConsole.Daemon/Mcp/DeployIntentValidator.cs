using Dapper;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Daemon.Data;

namespace NKS.WebDevConsole.Daemon.Mcp;

/// <summary>
/// SQLite-backed implementation of <see cref="IDeployIntentValidator"/>
/// (migration 006 — <c>deploy_intents</c> table). Stamps <c>used_at</c>
/// in the same statement that reads the row to make replay impossible
/// even under concurrent callers — SQLite serialises writes, so the
/// "second" updater sees rowcount=0 and gets <c>already_used</c>.
///
/// Token format (exchanged with the MCP server) is:
/// <code>
///     {intentId}.{nonce}.{base64UrlSignature}
/// </code>
/// All three fields are URL-safe; the signature covers the canonical
/// payload built by <see cref="IntentSigner.Canonicalize"/>.
/// </summary>
public sealed class DeployIntentValidator : IDeployIntentValidator
{
    private readonly Database _db;
    private readonly IntentSigner _signer;
    private readonly IMcpSessionGrantsRepository _grants;
    private readonly IDestructiveOperationKinds _kinds;
    private readonly Func<bool> _strictKindsLookup;

    /// <summary>
    /// Phase 7.4e ctor — adds the kinds registry and a strict-mode lookup.
    /// The lookup is a delegate so the validator picks up live setting
    /// flips without singleton recreation; daemon Program.cs wires it to
    /// <c>SettingsStore.GetBool("mcp", "strict_kinds", false)</c>.
    /// </summary>
    public DeployIntentValidator(
        Database db,
        IntentSigner signer,
        IMcpSessionGrantsRepository grants,
        IDestructiveOperationKinds kinds,
        Func<bool> strictKindsLookup)
    {
        _db = db;
        _signer = signer;
        _grants = grants;
        _kinds = kinds;
        _strictKindsLookup = strictKindsLookup;
    }

    /// <summary>
    /// Backwards-compat ctor — pre-7.4e callers (existing tests + DI before
    /// 7.4e wire-up). Defaults to lenient kind validation (no strict mode)
    /// and a null-object kinds registry. Should NOT be used in new code.
    /// </summary>
    public DeployIntentValidator(
        Database db,
        IntentSigner signer,
        IMcpSessionGrantsRepository grants)
        : this(db, signer, grants, NullKinds.Instance, () => false)
    { }

    /// <summary>Empty kinds registry for the legacy ctor — every Get returns null.</summary>
    private sealed class NullKinds : IDestructiveOperationKinds
    {
        public static readonly NullKinds Instance = new();
        public void Register(DestructiveOperationKind kind) { }
        public void Register(string id, string label, string pluginId) { }
        public void UnregisterPlugin(string pluginId) { }
        public DestructiveOperationKind? Get(string id) => null;
        public IReadOnlyList<DestructiveOperationKind> List() => Array.Empty<DestructiveOperationKind>();
    }

    public async Task<IntentValidationResult> ValidateAndConsumeAsync(
        string intentToken,
        string kind,
        string domain,
        string host,
        bool allowUnconfirmed,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(intentToken))
            return IntentValidationResult.Deny("missing_token");

        var parts = intentToken.Split('.');
        if (parts.Length != 3)
            return IntentValidationResult.Deny("malformed_token");

        var (intentId, nonce, signature) = (parts[0], parts[1], parts[2]);

        using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<IntentRow>(
            "SELECT id AS Id, domain AS Domain, host AS Host, release_id AS ReleaseId, " +
            "kind AS Kind, nonce AS Nonce, expires_at AS ExpiresAtRaw, " +
            "hmac_signature AS HmacSignature, used_at AS UsedAt, " +
            "confirmed_at AS ConfirmedAt " +
            "FROM deploy_intents WHERE id = @Id",
            new { Id = intentId });

        if (row is null) return IntentValidationResult.Deny("not_found");
        if (row.Nonce != nonce) return IntentValidationResult.Deny("nonce_mismatch");
        if (!string.IsNullOrEmpty(row.UsedAt)) return IntentValidationResult.Deny("already_used");
        if (!string.Equals(row.Kind, kind, StringComparison.OrdinalIgnoreCase))
            return IntentValidationResult.Deny("kind_mismatch");

        // Phase 7.4e — strict kind mode. When mcp.strict_kinds=true the
        // validator refuses any kind not currently in IDestructiveOperationKinds.
        // Useful when an operator wants belt-and-braces: only kinds plugins
        // have explicitly registered (and which therefore have human labels +
        // danger metadata for the banner) can fire. Default false so existing
        // intent flows minted before plugins migrate keep working.
        if (_strictKindsLookup() && _kinds.Get(row.Kind) is null)
            return IntentValidationResult.Deny("kind_unknown");
        if (!string.Equals(row.Domain, domain, StringComparison.OrdinalIgnoreCase))
            return IntentValidationResult.Deny("domain_mismatch");
        if (!string.Equals(row.Host, host, StringComparison.OrdinalIgnoreCase))
            return IntentValidationResult.Deny("host_mismatch");

        DateTimeOffset expiresAt;
        try { expiresAt = DateTimeOffset.Parse(row.ExpiresAtRaw); }
        catch { return IntentValidationResult.Deny("malformed_expiry"); }
        if (expiresAt < DateTimeOffset.UtcNow) return IntentValidationResult.Deny("expired");

        var canonical = IntentSigner.Canonicalize(
            row.Id, row.Domain, row.Host, row.Kind, row.Nonce, expiresAt, row.ReleaseId);

        // Compare against both the persisted signature (defence-in-depth)
        // and the freshly recomputed HMAC (catches a row that was tampered
        // with at the SQLite layer). Both must match.
        if (!_signer.Verify(canonical, signature))
            return IntentValidationResult.Deny("signature_mismatch");
        if (!string.Equals(row.HmacSignature, signature, StringComparison.Ordinal))
            return IntentValidationResult.Deny("signature_mismatch");

        // Phase 5.5 Mode A gate: refuse to consume an intent that the GUI
        // hasn't approved yet. The HTTP route layer translates this reason
        // to 425 Too Early; CI / headless callers bypass the gate by
        // passing allowUnconfirmed=true (which pre-stamps confirmed_at
        // BEFORE the gate check, so the gate trivially passes).
        //
        // Phase 7.3 — BEFORE returning pending_confirmation, check the
        // persistent grants table. If the calling MCP context (session id /
        // api-key id / instance id, set by middleware from X-Mcp-* headers)
        // matches an active grant for this kind+domain, auto-stamp
        // confirmed_at and proceed without bothering the operator. This is
        // the "trust this session for 30 min" / "always trust this AI"
        // story — the grant survives daemon restarts; X-Allow-Unconfirmed
        // had to be re-asserted on every request.
        if (string.IsNullOrEmpty(row.ConfirmedAt))
        {
            var grantedAuto = false;
            if (!allowUnconfirmed && McpCallerContext.HasAnyIdentity)
            {
                var grant = await _grants.FindMatchingActiveAsync(
                    McpCallerContext.SessionId,
                    McpCallerContext.InstanceId,
                    McpCallerContext.ApiKeyId,
                    row.Kind,
                    row.Domain,
                    ct);
                grantedAuto = grant is not null;
                // Phase 7.5+++ — bump match telemetry so operators can spot
                // dead vs heavily-used grants. Best-effort; never blocks
                // the auth path (RecordMatchAsync swallows DB errors).
                if (grant is not null && !string.IsNullOrEmpty(grant.Id))
                {
                    await _grants.RecordMatchAsync(grant.Id, ct);
                }
            }

            if (!allowUnconfirmed && !grantedAuto)
                return IntentValidationResult.Deny("pending_confirmation");
            // Pre-stamp via single UPDATE — no-op if a concurrent GUI click
            // already stamped (rowcount=0 is fine, the row is now
            // confirmed either way and the next read will see it).
            await conn.ExecuteAsync(
                "UPDATE deploy_intents SET confirmed_at = @Now WHERE id = @Id AND confirmed_at IS NULL",
                new { Id = intentId, Now = DateTimeOffset.UtcNow.ToString("o") });
        }

        // Atomic single-use stamp. The `used_at IS NULL` predicate guards
        // against a concurrent validator winning the race — only one of
        // the two callers will get rowcount=1.
        var rows = await conn.ExecuteAsync(
            "UPDATE deploy_intents SET used_at = @UsedAt WHERE id = @Id AND used_at IS NULL",
            new { Id = intentId, UsedAt = DateTimeOffset.UtcNow.ToString("o") });
        if (rows == 0) return IntentValidationResult.Deny("already_used");

        return IntentValidationResult.Allow();
    }

    /// <summary>Internal Dapper row.</summary>
    private sealed class IntentRow
    {
        public string Id { get; set; } = "";
        public string Domain { get; set; } = "";
        public string Host { get; set; } = "";
        public string? ReleaseId { get; set; }
        public string Kind { get; set; } = "";
        public string Nonce { get; set; } = "";
        public string ExpiresAtRaw { get; set; } = "";
        public string HmacSignature { get; set; } = "";
        public string? UsedAt { get; set; }
        /// <summary>NULL until the GUI banner is approved (Mode A).</summary>
        public string? ConfirmedAt { get; set; }
    }
}
