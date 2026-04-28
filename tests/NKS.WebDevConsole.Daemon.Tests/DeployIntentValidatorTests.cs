using Dapper;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Daemon.Data;
using NKS.WebDevConsole.Daemon.Mcp;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// State-machine tests for <see cref="DeployIntentValidator"/>. Each test
/// gets its own per-test temp SQLite file seeded with the deploy_intents
/// schema (mirrors migrations 006+007+008). A real <see cref="IntentSigner"/>
/// mints tokens so the HMAC path is exercised end-to-end.
/// </summary>
public sealed class DeployIntentValidatorTests
{
    private const string MigrationSql = """
        CREATE TABLE deploy_intents (
            id TEXT PRIMARY KEY,
            domain TEXT,
            host TEXT,
            release_id TEXT,
            nonce TEXT UNIQUE,
            expires_at TEXT,
            hmac_signature TEXT,
            used_at TEXT,
            kind TEXT NOT NULL DEFAULT 'deploy',
            confirmed_at TEXT,
            matched_grant_id TEXT,
            created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
        );
        """;

    // Shared signer — all tests in the class share the key written to
    // WdcPaths.DataRoot (Lazy<> is already resolved for the process).
    private static readonly IntentSigner Signer = new IntentSigner();

    private static (Database Db, string DbPath) NewDb()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nks-intent-test-{Guid.NewGuid():N}.db");
        var db = new Database(path);
        using var seed = db.CreateConnection();
        seed.Execute(MigrationSql);
        return (db, path);
    }

    private static void Cleanup(string path)
    {
        try { File.Delete(path); } catch { }
    }

    /// <summary>
    /// Helper that builds a valid intent row and returns the token string
    /// in {intentId}.{nonce}.{signature} format.
    /// </summary>
    private static (string Token, string IntentId) InsertValidIntent(
        Database db,
        string domain = "myapp.loc",
        string host = "production",
        string kind = "deploy",
        DateTimeOffset? expiresAt = null,
        string? confirmedAt = "2030-01-01T00:00:00.000Z",
        string? usedAt = null,
        string? releaseId = "rel-1",
        string? overrideSignature = null)
    {
        var intentId = Guid.NewGuid().ToString("N");
        var nonce = Guid.NewGuid().ToString("N");
        var expiry = expiresAt ?? DateTimeOffset.UtcNow.AddHours(1);

        var canonical = IntentSigner.Canonicalize(
            intentId, domain, host, kind, nonce, expiry, releaseId);
        var signature = overrideSignature ?? Signer.Sign(canonical);

        using var conn = db.CreateConnection();
        conn.Execute(
            "INSERT INTO deploy_intents (id, domain, host, release_id, nonce, " +
            "expires_at, hmac_signature, used_at, kind, confirmed_at) " +
            "VALUES (@Id, @Domain, @Host, @ReleaseId, @Nonce, " +
            "@ExpiresAt, @Sig, @UsedAt, @Kind, @ConfirmedAt)",
            new
            {
                Id = intentId,
                Domain = domain,
                Host = host,
                ReleaseId = releaseId,
                Nonce = nonce,
                ExpiresAt = expiry.ToString("o"),
                Sig = signature,
                UsedAt = usedAt,
                Kind = kind,
                ConfirmedAt = confirmedAt,
            });

        var token = $"{intentId}.{nonce}.{signature}";
        return (token, intentId);
    }

    private static DeployIntentValidator MakeValidator(Database db) =>
        new DeployIntentValidator(db, Signer, new EmptyGrantsRepo());

    /// <summary>
    /// Phase 7.3 — null-object grants repo. Returns "no match" for every
    /// lookup so the validator falls back to its existing pending-confirmation
    /// behaviour. Tests that exercise the grants pre-check pass a custom
    /// implementation in their own MakeValidator helper instead.
    /// </summary>
    private sealed class EmptyGrantsRepo : NKS.WebDevConsole.Core.Interfaces.IMcpSessionGrantsRepository
    {
        public Task<IReadOnlyList<NKS.WebDevConsole.Core.Interfaces.McpSessionGrantRow>> ListActiveAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<NKS.WebDevConsole.Core.Interfaces.McpSessionGrantRow>>(Array.Empty<NKS.WebDevConsole.Core.Interfaces.McpSessionGrantRow>());
        public Task<IReadOnlyList<NKS.WebDevConsole.Core.Interfaces.McpSessionGrantRow>> ListAllAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<NKS.WebDevConsole.Core.Interfaces.McpSessionGrantRow>>(Array.Empty<NKS.WebDevConsole.Core.Interfaces.McpSessionGrantRow>());
        public Task<string> InsertAsync(NKS.WebDevConsole.Core.Interfaces.McpSessionGrantRow row, CancellationToken ct) =>
            Task.FromResult(Guid.NewGuid().ToString("D"));
        public Task<bool> RevokeAsync(string id, CancellationToken ct) => Task.FromResult(false);
        public Task<NKS.WebDevConsole.Core.Interfaces.McpSessionGrantRow?> FindMatchingActiveAsync(
            string? sessionId, string? instanceId, string? apiKeyId,
            string kind, string target, CancellationToken ct) =>
            Task.FromResult<NKS.WebDevConsole.Core.Interfaces.McpSessionGrantRow?>(null);
        public Task RecordMatchAsync(string id, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> UpdateMutableAsync(string id, int? minCooldownSeconds, string? expiresAtIso, string? note, CancellationToken ct) => Task.FromResult(false);
    }

    // --- Allow path ---

    [Fact]
    public async Task ValidToken_Consumed_ReturnsOk()
    {
        var (db, path) = NewDb();
        try
        {
            var (token, _) = InsertValidIntent(db);
            var validator = MakeValidator(db);
            var result = await validator.ValidateAndConsumeAsync(
                token, "deploy", "myapp.loc", "production",
                allowUnconfirmed: false, CancellationToken.None);
            Assert.True(result.Ok);
            Assert.Null(result.Reason);
        }
        finally { Cleanup(path); }
    }

    // --- Deny: missing / malformed token ---

    [Fact]
    public async Task EmptyToken_Returns_MissingToken()
    {
        var (db, path) = NewDb();
        try
        {
            var result = await MakeValidator(db).ValidateAndConsumeAsync(
                "", "deploy", "myapp.loc", "production",
                false, CancellationToken.None);
            Assert.False(result.Ok);
            Assert.Equal("missing_token", result.Reason);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task WhitespaceToken_Returns_MissingToken()
    {
        var (db, path) = NewDb();
        try
        {
            var result = await MakeValidator(db).ValidateAndConsumeAsync(
                "   ", "deploy", "myapp.loc", "production",
                false, CancellationToken.None);
            Assert.False(result.Ok);
            Assert.Equal("missing_token", result.Reason);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task TokenWithNoDots_Returns_MalformedToken()
    {
        var (db, path) = NewDb();
        try
        {
            var result = await MakeValidator(db).ValidateAndConsumeAsync(
                "nodots", "deploy", "myapp.loc", "production",
                false, CancellationToken.None);
            Assert.False(result.Ok);
            Assert.Equal("malformed_token", result.Reason);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task TokenWithOneDot_Returns_MalformedToken()
    {
        var (db, path) = NewDb();
        try
        {
            var result = await MakeValidator(db).ValidateAndConsumeAsync(
                "a.b", "deploy", "myapp.loc", "production",
                false, CancellationToken.None);
            Assert.False(result.Ok);
            Assert.Equal("malformed_token", result.Reason);
        }
        finally { Cleanup(path); }
    }

    // --- Deny: not_found ---

    [Fact]
    public async Task RandomUuidId_Returns_NotFound()
    {
        var (db, path) = NewDb();
        try
        {
            var fakeToken = $"{Guid.NewGuid():N}.{Guid.NewGuid():N}.fakesig";
            var result = await MakeValidator(db).ValidateAndConsumeAsync(
                fakeToken, "deploy", "myapp.loc", "production",
                false, CancellationToken.None);
            Assert.False(result.Ok);
            Assert.Equal("not_found", result.Reason);
        }
        finally { Cleanup(path); }
    }

    // --- Deny: nonce_mismatch ---

    [Fact]
    public async Task WrongNonce_Returns_NonceMismatch()
    {
        var (db, path) = NewDb();
        try
        {
            var (token, intentId) = InsertValidIntent(db);
            // Replace the nonce segment with a different value
            var parts = token.Split('.');
            var badToken = $"{parts[0]}.wrongnonce.{parts[2]}";
            var result = await MakeValidator(db).ValidateAndConsumeAsync(
                badToken, "deploy", "myapp.loc", "production",
                false, CancellationToken.None);
            Assert.False(result.Ok);
            Assert.Equal("nonce_mismatch", result.Reason);
        }
        finally { Cleanup(path); }
    }

    // --- Deny: kind_mismatch ---

    [Fact]
    public async Task KindMismatch_Returns_KindMismatch()
    {
        var (db, path) = NewDb();
        try
        {
            var (token, _) = InsertValidIntent(db, kind: "deploy");
            var result = await MakeValidator(db).ValidateAndConsumeAsync(
                token, "rollback", "myapp.loc", "production",
                false, CancellationToken.None);
            Assert.False(result.Ok);
            Assert.Equal("kind_mismatch", result.Reason);
        }
        finally { Cleanup(path); }
    }

    // --- Phase 7.4e — strict-kinds mode rejects unregistered kinds ---

    [Fact]
    public async Task StrictMode_UnregisteredKind_Returns_KindUnknown()
    {
        var (db, path) = NewDb();
        try
        {
            var (token, _) = InsertValidIntent(db, kind: "rogue:plugin_op", confirmedAt: "stamped");
            var registry = new NKS.WebDevConsole.Daemon.Mcp.DestructiveOperationKindsRegistry();
            // registry has the 4 core seeded kinds but NOT "rogue:plugin_op"
            var validator = new DeployIntentValidator(
                db, Signer, new EmptyGrantsRepo(), registry, () => true /* strict */);
            var result = await validator.ValidateAndConsumeAsync(
                token, "rogue:plugin_op", "myapp.loc", "production",
                allowUnconfirmed: false, CancellationToken.None);
            Assert.False(result.Ok);
            Assert.Equal("kind_unknown", result.Reason);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task StrictMode_RegisteredPluginKind_PassesValidation()
    {
        var (db, path) = NewDb();
        try
        {
            var (token, _) = InsertValidIntent(db, kind: "nksbackup:restore", confirmedAt: "stamped");
            var registry = new NKS.WebDevConsole.Daemon.Mcp.DestructiveOperationKindsRegistry();
            registry.Register("nksbackup:restore", "Restore backup", "nksbackup");
            var validator = new DeployIntentValidator(
                db, Signer, new EmptyGrantsRepo(), registry, () => true /* strict */);
            var result = await validator.ValidateAndConsumeAsync(
                token, "nksbackup:restore", "myapp.loc", "production",
                allowUnconfirmed: false, CancellationToken.None);
            Assert.True(result.Ok);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task LenientMode_UnregisteredKind_StillPasses()
    {
        var (db, path) = NewDb();
        try
        {
            // Default behaviour (strict=false) — any regex-valid kind is fine
            // even if not in the registry. Backwards-compat with pre-7.4e flows.
            var (token, _) = InsertValidIntent(db, kind: "rogue:plugin_op", confirmedAt: "stamped");
            var registry = new NKS.WebDevConsole.Daemon.Mcp.DestructiveOperationKindsRegistry();
            var validator = new DeployIntentValidator(
                db, Signer, new EmptyGrantsRepo(), registry, () => false /* lenient */);
            var result = await validator.ValidateAndConsumeAsync(
                token, "rogue:plugin_op", "myapp.loc", "production",
                allowUnconfirmed: false, CancellationToken.None);
            Assert.True(result.Ok);
        }
        finally { Cleanup(path); }
    }

    // --- Phase 7.4 — plugin-defined custom kinds round-trip through the validator ---

    [Fact]
    public async Task CustomPluginKind_RoundTripsThroughValidator()
    {
        var (db, path) = NewDb();
        try
        {
            // A plugin-namespaced kind ("db:drop_table") is treated identically
            // to the legacy hardcoded kinds — the validator only compares the
            // requested kind to the row's stored kind, no whitelist anywhere.
            // This is what makes MCP intents truly globally usable for any
            // destructive op a plugin defines.
            var (token, intentId) = InsertValidIntent(db, kind: "db:drop_table", confirmedAt: "stamped");
            var result = await MakeValidator(db).ValidateAndConsumeAsync(
                token, "db:drop_table", "myapp.loc", "production",
                allowUnconfirmed: false, CancellationToken.None);
            Assert.True(result.Ok);
            Assert.Null(result.Reason);
            // And the wrong kind for the same intent still gets kind_mismatch.
            var (token2, _) = InsertValidIntent(db, kind: "site:delete", confirmedAt: "stamped");
            var mismatch = await MakeValidator(db).ValidateAndConsumeAsync(
                token2, "deploy", "myapp.loc", "production",
                allowUnconfirmed: false, CancellationToken.None);
            Assert.False(mismatch.Ok);
            Assert.Equal("kind_mismatch", mismatch.Reason);
            // Sanity: the consumed flag stuck on the first row.
            using var conn = db.CreateConnection();
            var usedAt = conn.QuerySingleOrDefault<string>(
                "SELECT used_at FROM deploy_intents WHERE id = @Id",
                new { Id = intentId });
            Assert.NotNull(usedAt);
        }
        finally { Cleanup(path); }
    }

    // --- Deny: domain_mismatch ---

    [Fact]
    public async Task DomainMismatch_Returns_DomainMismatch()
    {
        var (db, path) = NewDb();
        try
        {
            var (token, _) = InsertValidIntent(db, domain: "myapp.loc");
            var result = await MakeValidator(db).ValidateAndConsumeAsync(
                token, "deploy", "other.loc", "production",
                false, CancellationToken.None);
            Assert.False(result.Ok);
            Assert.Equal("domain_mismatch", result.Reason);
        }
        finally { Cleanup(path); }
    }

    // --- Deny: host_mismatch ---

    [Fact]
    public async Task HostMismatch_Returns_HostMismatch()
    {
        var (db, path) = NewDb();
        try
        {
            var (token, _) = InsertValidIntent(db, host: "production");
            var result = await MakeValidator(db).ValidateAndConsumeAsync(
                token, "deploy", "myapp.loc", "staging",
                false, CancellationToken.None);
            Assert.False(result.Ok);
            Assert.Equal("host_mismatch", result.Reason);
        }
        finally { Cleanup(path); }
    }

    // --- Deny: expired ---

    [Fact]
    public async Task ExpiredToken_Returns_Expired()
    {
        var (db, path) = NewDb();
        try
        {
            var (token, _) = InsertValidIntent(db, expiresAt: DateTimeOffset.UtcNow.AddHours(-1));
            var result = await MakeValidator(db).ValidateAndConsumeAsync(
                token, "deploy", "myapp.loc", "production",
                false, CancellationToken.None);
            Assert.False(result.Ok);
            Assert.Equal("expired", result.Reason);
        }
        finally { Cleanup(path); }
    }

    // --- Deny: signature_mismatch ---

    [Fact]
    public async Task TamperedSignatureInToken_Returns_SignatureMismatch()
    {
        var (db, path) = NewDb();
        try
        {
            var (token, _) = InsertValidIntent(db);
            var parts = token.Split('.');
            // Flip one character in the signature segment
            var sig = parts[2];
            var badSig = sig[..^1] + (sig[^1] == 'A' ? 'B' : 'A');
            var badToken = $"{parts[0]}.{parts[1]}.{badSig}";
            var result = await MakeValidator(db).ValidateAndConsumeAsync(
                badToken, "deploy", "myapp.loc", "production",
                false, CancellationToken.None);
            Assert.False(result.Ok);
            Assert.Equal("signature_mismatch", result.Reason);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task RowSignatureTampered_Returns_SignatureMismatch()
    {
        var (db, path) = NewDb();
        try
        {
            // Insert a row with a valid token, but then corrupt the
            // hmac_signature stored in the DB (simulates SQLite-layer tamper).
            var (token, intentId) = InsertValidIntent(db);
            using var conn = db.CreateConnection();
            conn.Execute(
                "UPDATE deploy_intents SET hmac_signature = 'tampered' WHERE id = @Id",
                new { Id = intentId });

            var result = await MakeValidator(db).ValidateAndConsumeAsync(
                token, "deploy", "myapp.loc", "production",
                false, CancellationToken.None);
            Assert.False(result.Ok);
            Assert.Equal("signature_mismatch", result.Reason);
        }
        finally { Cleanup(path); }
    }

    // --- Deny: pending_confirmation ---

    [Fact]
    public async Task ConfirmedAtNull_AllowUnconfirmedFalse_Returns_PendingConfirmation()
    {
        var (db, path) = NewDb();
        try
        {
            var (token, _) = InsertValidIntent(db, confirmedAt: null);
            var result = await MakeValidator(db).ValidateAndConsumeAsync(
                token, "deploy", "myapp.loc", "production",
                allowUnconfirmed: false, CancellationToken.None);
            Assert.False(result.Ok);
            Assert.Equal("pending_confirmation", result.Reason);
        }
        finally { Cleanup(path); }
    }

    // --- Phase 7.3 — grant pre-check auto-confirms ---

    [Fact]
    public async Task ConfirmedAtNull_GrantMatches_PreStampsAndSucceeds()
    {
        var (db, path) = NewDb();
        try
        {
            var (token, intentId) = InsertValidIntent(db, confirmedAt: null);

            var grants = new StubGrantsRepo();
            grants.MatchOn(sessionId: "agent-42", kind: "deploy", target: "myapp.loc");
            var validator = new DeployIntentValidator(db, Signer, grants);

            // Simulate the middleware setting AsyncLocal slots from request headers.
            NKS.WebDevConsole.Daemon.Mcp.McpCallerContext.SessionId = "agent-42";
            try
            {
                var result = await validator.ValidateAndConsumeAsync(
                    token, "deploy", "myapp.loc", "production",
                    allowUnconfirmed: false, CancellationToken.None);
                Assert.True(result.Ok);
                using var conn = db.CreateConnection();
                var confirmedAt = conn.QuerySingleOrDefault<string>(
                    "SELECT confirmed_at FROM deploy_intents WHERE id = @Id",
                    new { Id = intentId });
                Assert.NotNull(confirmedAt);
            }
            finally { NKS.WebDevConsole.Daemon.Mcp.McpCallerContext.Clear(); }
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task ConfirmedAtNull_NoGrantMatch_StillDeniesPendingConfirmation()
    {
        var (db, path) = NewDb();
        try
        {
            var (token, _) = InsertValidIntent(db, confirmedAt: null);
            var grants = new StubGrantsRepo();    // no grants registered
            var validator = new DeployIntentValidator(db, Signer, grants);
            NKS.WebDevConsole.Daemon.Mcp.McpCallerContext.SessionId = "agent-42";
            try
            {
                var result = await validator.ValidateAndConsumeAsync(
                    token, "deploy", "myapp.loc", "production",
                    allowUnconfirmed: false, CancellationToken.None);
                Assert.False(result.Ok);
                Assert.Equal("pending_confirmation", result.Reason);
            }
            finally { NKS.WebDevConsole.Daemon.Mcp.McpCallerContext.Clear(); }
        }
        finally { Cleanup(path); }
    }

    /// <summary>Stub grants repo whose lookup returns a match only when caller
    /// + kind + target line up with the slot configured via MatchOn.</summary>
    private sealed class StubGrantsRepo : NKS.WebDevConsole.Core.Interfaces.IMcpSessionGrantsRepository
    {
        private string? _sessionId; private string? _kind; private string? _target;
        public int RecordedMatches { get; private set; }
        public void MatchOn(string sessionId, string kind, string target)
        { _sessionId = sessionId; _kind = kind; _target = target; }

        public Task<IReadOnlyList<NKS.WebDevConsole.Core.Interfaces.McpSessionGrantRow>> ListActiveAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<NKS.WebDevConsole.Core.Interfaces.McpSessionGrantRow>>(Array.Empty<NKS.WebDevConsole.Core.Interfaces.McpSessionGrantRow>());
        public Task<IReadOnlyList<NKS.WebDevConsole.Core.Interfaces.McpSessionGrantRow>> ListAllAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<NKS.WebDevConsole.Core.Interfaces.McpSessionGrantRow>>(Array.Empty<NKS.WebDevConsole.Core.Interfaces.McpSessionGrantRow>());
        public Task<string> InsertAsync(NKS.WebDevConsole.Core.Interfaces.McpSessionGrantRow row, CancellationToken ct) =>
            Task.FromResult("");
        public Task<bool> RevokeAsync(string id, CancellationToken ct) => Task.FromResult(false);
        public Task<NKS.WebDevConsole.Core.Interfaces.McpSessionGrantRow?> FindMatchingActiveAsync(
            string? sessionId, string? instanceId, string? apiKeyId,
            string kind, string target, CancellationToken ct)
        {
            if (sessionId == _sessionId && kind == _kind && target == _target)
            {
                return Task.FromResult<NKS.WebDevConsole.Core.Interfaces.McpSessionGrantRow?>(
                    new NKS.WebDevConsole.Core.Interfaces.McpSessionGrantRow(
                        Id: "stub", ScopeType: "session", ScopeValue: sessionId,
                        KindPattern: kind, TargetPattern: target,
                        GrantedAt: "now", ExpiresAt: null, GrantedBy: "test",
                        RevokedAt: null, Note: null));
            }
            return Task.FromResult<NKS.WebDevConsole.Core.Interfaces.McpSessionGrantRow?>(null);
        }
        public Task RecordMatchAsync(string id, CancellationToken ct)
        {
            RecordedMatches++;
            return Task.CompletedTask;
        }
        public Task<bool> UpdateMutableAsync(string id, int? minCooldownSeconds, string? expiresAtIso, string? note, CancellationToken ct) => Task.FromResult(false);
    }

    // --- Allow: allowUnconfirmed pre-stamps confirmed_at ---

    [Fact]
    public async Task ConfirmedAtNull_AllowUnconfirmedTrue_PreStampsAndSucceeds()
    {
        var (db, path) = NewDb();
        try
        {
            var (token, intentId) = InsertValidIntent(db, confirmedAt: null);
            var result = await MakeValidator(db).ValidateAndConsumeAsync(
                token, "deploy", "myapp.loc", "production",
                allowUnconfirmed: true, CancellationToken.None);
            Assert.True(result.Ok);
            // Verify confirmed_at was stamped in the DB
            using var conn = db.CreateConnection();
            var confirmedAt = conn.QuerySingleOrDefault<string>(
                "SELECT confirmed_at FROM deploy_intents WHERE id = @Id",
                new { Id = intentId });
            Assert.NotNull(confirmedAt);
            Assert.NotEmpty(confirmedAt);
        }
        finally { Cleanup(path); }
    }

    // --- Deny: already_used ---

    [Fact]
    public async Task AlreadyUsed_Returns_AlreadyUsed()
    {
        var (db, path) = NewDb();
        try
        {
            var (token, _) = InsertValidIntent(db);
            var validator = MakeValidator(db);
            // First consume succeeds
            var first = await validator.ValidateAndConsumeAsync(
                token, "deploy", "myapp.loc", "production",
                false, CancellationToken.None);
            Assert.True(first.Ok);
            // Second consume is rejected
            var second = await validator.ValidateAndConsumeAsync(
                token, "deploy", "myapp.loc", "production",
                false, CancellationToken.None);
            Assert.False(second.Ok);
            Assert.Equal("already_used", second.Reason);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task PreInsertedUsedAt_Returns_AlreadyUsed()
    {
        var (db, path) = NewDb();
        try
        {
            var (token, _) = InsertValidIntent(
                db, usedAt: DateTimeOffset.UtcNow.AddMinutes(-5).ToString("o"));
            var result = await MakeValidator(db).ValidateAndConsumeAsync(
                token, "deploy", "myapp.loc", "production",
                false, CancellationToken.None);
            Assert.False(result.Ok);
            Assert.Equal("already_used", result.Reason);
        }
        finally { Cleanup(path); }
    }

    // --- Concurrent consume race ---

    [Fact]
    public async Task ConcurrentConsume_ExactlyOneWins_OtherGetsAlreadyUsed()
    {
        var (db, path) = NewDb();
        try
        {
            var (token, _) = InsertValidIntent(db);
            var validator = MakeValidator(db);

            // Fire two concurrent consumes and collect results
            var t1 = validator.ValidateAndConsumeAsync(
                token, "deploy", "myapp.loc", "production",
                false, CancellationToken.None);
            var t2 = validator.ValidateAndConsumeAsync(
                token, "deploy", "myapp.loc", "production",
                false, CancellationToken.None);

            var results = await Task.WhenAll(t1, t2);

            var successes = results.Count(r => r.Ok);
            var failures = results.Count(r => !r.Ok && r.Reason == "already_used");

            Assert.Equal(1, successes);
            Assert.Equal(1, failures);
        }
        finally { Cleanup(path); }
    }
}
