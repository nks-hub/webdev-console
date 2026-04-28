using System.Security.Cryptography;
using NKS.WebDevConsole.Core.Services;
using NKS.WebDevConsole.Daemon.Mcp;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Unit tests for <see cref="IntentSigner"/>. The signer writes its HMAC key
/// to <c>{WdcPaths.DataRoot}/mcp-hmac.*</c>. Because <see cref="WdcPaths"/>
/// caches its root via a <see cref="Lazy{T}"/> after first access, the env
/// override cannot be reliably redirected inside a shared test process.
/// Instead, tests accept whatever <c>WdcPaths.DataRoot</c> resolves to and
/// clean up only what they write (the key file itself is left in place across
/// tests because two instances must share it for the idempotency test).
/// </summary>
public sealed class IntentSignerTests
{
    private static readonly DateTimeOffset TestExpiry =
        new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static string MakeCanonical(
        string intentId = "id-1",
        string domain = "myapp.loc",
        string host = "production",
        string kind = "deploy",
        string nonce = "abc123",
        DateTimeOffset? expiresAt = null,
        string? releaseId = "rel-42") =>
        IntentSigner.Canonicalize(intentId, domain, host, kind, nonce, expiresAt ?? TestExpiry, releaseId);

    // --- Sign/Verify roundtrip ---

    [Fact]
    public void SignVerify_Roundtrip_Succeeds()
    {
        var signer = new IntentSigner();
        var canonical = MakeCanonical();
        var sig = signer.Sign(canonical);
        Assert.True(signer.Verify(canonical, sig));
    }

    // --- Tamper rejection ---

    [Fact]
    public void Verify_RejectsTamperedDomain()
    {
        var signer = new IntentSigner();
        var original = MakeCanonical(domain: "myapp.loc");
        var sig = signer.Sign(original);
        // Verify against canonical with a different domain
        var tampered = MakeCanonical(domain: "evil.loc");
        Assert.False(signer.Verify(tampered, sig));
    }

    [Fact]
    public void Verify_RejectsTamperedSignature()
    {
        var signer = new IntentSigner();
        var canonical = MakeCanonical();
        var sig = signer.Sign(canonical);
        // Flip the last character of the base64url signature
        var tampered = sig[..^1] + (sig[^1] == 'A' ? 'B' : 'A');
        Assert.False(signer.Verify(canonical, tampered));
    }

    [Fact]
    public void Verify_RejectsMalformedNonBase64UrlSignature()
    {
        var signer = new IntentSigner();
        var canonical = MakeCanonical();
        // Contains characters illegal in base64url — should not throw, returns false
        Assert.False(signer.Verify(canonical, "!!!not-base64url!!!"));
    }

    [Fact]
    public void Verify_RejectsEmptySignature()
    {
        var signer = new IntentSigner();
        var canonical = MakeCanonical();
        Assert.False(signer.Verify(canonical, ""));
    }

    // --- Canonicalize determinism ---

    [Fact]
    public void Canonicalize_IsDeterministicForSameInputs()
    {
        var a = MakeCanonical();
        var b = MakeCanonical();
        Assert.Equal(a, b);
    }

    [Fact]
    public void Canonicalize_ChangesWhenIntentIdMutated()
    {
        var baseline = MakeCanonical(intentId: "id-1");
        var mutated = MakeCanonical(intentId: "id-2");
        Assert.NotEqual(baseline, mutated);
    }

    [Fact]
    public void Canonicalize_ChangesWhenDomainMutated()
    {
        var baseline = MakeCanonical(domain: "a.loc");
        var mutated = MakeCanonical(domain: "b.loc");
        Assert.NotEqual(baseline, mutated);
    }

    [Fact]
    public void Canonicalize_ChangesWhenHostMutated()
    {
        var baseline = MakeCanonical(host: "prod");
        var mutated = MakeCanonical(host: "staging");
        Assert.NotEqual(baseline, mutated);
    }

    [Fact]
    public void Canonicalize_ChangesWhenKindMutated()
    {
        var baseline = MakeCanonical(kind: "deploy");
        var mutated = MakeCanonical(kind: "rollback");
        Assert.NotEqual(baseline, mutated);
    }

    [Fact]
    public void Canonicalize_ChangesWhenNonceMutated()
    {
        var baseline = MakeCanonical(nonce: "nonce-a");
        var mutated = MakeCanonical(nonce: "nonce-b");
        Assert.NotEqual(baseline, mutated);
    }

    [Fact]
    public void Canonicalize_ChangesWhenExpiresAtMutated()
    {
        var baseline = MakeCanonical(expiresAt: TestExpiry);
        var mutated = MakeCanonical(expiresAt: TestExpiry.AddSeconds(1));
        Assert.NotEqual(baseline, mutated);
    }

    [Fact]
    public void Canonicalize_ChangesWhenReleaseIdMutated()
    {
        var baseline = MakeCanonical(releaseId: "rel-1");
        var mutated = MakeCanonical(releaseId: "rel-2");
        Assert.NotEqual(baseline, mutated);
    }

    [Fact]
    public void Canonicalize_NullReleaseIdDiffersFromNonNull()
    {
        var withNull = MakeCanonical(releaseId: null);
        var withValue = MakeCanonical(releaseId: "something");
        Assert.NotEqual(withNull, withValue);
    }

    // --- DPAPI key load idempotency (Windows only) ---

    [Fact]
    public void DpapiKey_IsIdempotent_SecondInstanceReturnsSameKey()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Both instances read or create the same mcp-hmac.dpapi file under
        // WdcPaths.DataRoot. Ensure the DataRoot exists so the first
        // constructor doesn't fail on a fresh machine.
        Directory.CreateDirectory(WdcPaths.DataRoot);

        var signer1 = new IntentSigner();
        var signer2 = new IntentSigner();

        // A signature produced by signer1 must verify against signer2 and
        // vice-versa — only possible if both loaded the same key.
        var canonical = MakeCanonical();
        var sig1 = signer1.Sign(canonical);
        var sig2 = signer2.Sign(canonical);

        Assert.True(signer2.Verify(canonical, sig1),
            "signer2 could not verify a signature produced by signer1 — keys differ");
        Assert.True(signer1.Verify(canonical, sig2),
            "signer1 could not verify a signature produced by signer2 — keys differ");
    }

    // --- Constant-time compare via FixedTimeEquals ---

    [Fact]
    public void ConstantTimeCompare_DifferentSignaturesOfSameLengthBothFail()
    {
        // Build two canonical payloads that produce HMAC tags of the same
        // byte length (HMAC-SHA256 is always 32 bytes / 43 base64url chars).
        // Verify that a signature from payload-A does NOT verify against
        // payload-B — exercising the FixedTimeEquals path with same-length
        // buffers where the comparison must not short-circuit.
        var signer = new IntentSigner();
        var canonicalA = MakeCanonical(intentId: "aaa");
        var canonicalB = MakeCanonical(intentId: "bbb");

        var sigA = signer.Sign(canonicalA);
        var sigB = signer.Sign(canonicalB);

        // Same length guaranteed (SHA256 always 32 bytes → 43 base64url chars)
        Assert.Equal(sigA.Length, sigB.Length);
        Assert.NotEqual(sigA, sigB);

        // Cross-verify: neither sig validates against the other's payload
        Assert.False(signer.Verify(canonicalA, sigB));
        Assert.False(signer.Verify(canonicalB, sigA));
    }

    [Fact]
    public void Sign_ProducesBase64UrlWithoutPaddingOrStandardChars()
    {
        var signer = new IntentSigner();
        var sig = signer.Sign(MakeCanonical());
        // Base64url must not contain +, /, or = (padding)
        Assert.DoesNotContain("+", sig);
        Assert.DoesNotContain("/", sig);
        Assert.DoesNotContain("=", sig);
    }
}
