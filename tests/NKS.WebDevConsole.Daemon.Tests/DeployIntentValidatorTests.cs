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
        new DeployIntentValidator(db, Signer);

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
