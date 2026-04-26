using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using NKS.WebDevConsole.Core.Services;

namespace NKS.WebDevConsole.Daemon.Mcp;

/// <summary>
/// Owns the per-install HMAC key used to sign MCP destructive-operation
/// intent tokens. The key file lives at
/// <c>{WdcPaths.DataRoot}/mcp-hmac.dpapi</c> on Windows (DPAPI-wrapped to
/// the current Windows user — same trust boundary as the daemon process)
/// and at <c>{WdcPaths.DataRoot}/mcp-hmac.key</c> with 0600 perms on
/// POSIX systems where DPAPI is unavailable.
///
/// On first use we mint a fresh 256-bit key with <see cref="RandomNumberGenerator"/>;
/// subsequent daemon starts read it back unchanged. Rotating the key
/// invalidates every outstanding pre-signed intent — that is the correct
/// behaviour after a credential leak.
/// </summary>
public sealed class IntentSigner
{
    private readonly byte[] _key;

    public IntentSigner()
    {
        Directory.CreateDirectory(WdcPaths.DataRoot);
        _key = LoadOrCreateKey();
    }

    /// <summary>
    /// Sign a canonical payload, returning a URL-safe base64 string of the
    /// HMAC-SHA256 tag. Caller is responsible for canonicalising the
    /// payload (see <see cref="Canonicalize"/>) BEFORE signing — different
    /// orderings of the same JSON would otherwise hash to different tags.
    /// </summary>
    public string Sign(string canonicalPayload)
    {
        var bytes = Encoding.UTF8.GetBytes(canonicalPayload);
        var tag = HMACSHA256.HashData(_key, bytes);
        return Base64Url(tag);
    }

    /// <summary>
    /// Constant-time compare of a candidate signature against the expected
    /// HMAC of the canonical payload. Returns true iff the signature is
    /// well-formed base64url AND matches.
    /// </summary>
    public bool Verify(string canonicalPayload, string candidateSignature)
    {
        byte[] candidate;
        try
        {
            candidate = Base64UrlDecode(candidateSignature);
        }
        catch
        {
            return false;
        }
        var bytes = Encoding.UTF8.GetBytes(canonicalPayload);
        var expected = HMACSHA256.HashData(_key, bytes);
        return CryptographicOperations.FixedTimeEquals(expected, candidate);
    }

    /// <summary>
    /// Build a stable canonical form of the intent fields. Field ordering
    /// is fixed alphabetically and joined with a single newline so the
    /// resulting string can never be ambiguously re-parsed (no embedded
    /// JSON, no unsorted keys, no trailing whitespace).
    /// </summary>
    public static string Canonicalize(
        string intentId,
        string domain,
        string host,
        string kind,
        string nonce,
        DateTimeOffset expiresAt,
        string? releaseId)
    {
        // Strict ordering — do NOT reorder these lines. The MCP server
        // (or any future external signer) must build the same string in the
        // same order or verification fails.
        var sb = new StringBuilder();
        sb.Append("domain="); sb.AppendLine(domain);
        sb.Append("expires_at="); sb.AppendLine(expiresAt.ToUniversalTime().ToString("o"));
        sb.Append("host="); sb.AppendLine(host);
        sb.Append("intent_id="); sb.AppendLine(intentId);
        sb.Append("kind="); sb.AppendLine(kind);
        sb.Append("nonce="); sb.AppendLine(nonce);
        sb.Append("release_id="); sb.Append(releaseId ?? "");
        return sb.ToString();
    }

    private static byte[] LoadOrCreateKey()
    {
        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(WdcPaths.DataRoot, "mcp-hmac.dpapi");
            if (File.Exists(path))
            {
                var wrapped = File.ReadAllBytes(path);
                // DPAPI unwrap. CurrentUser scope ties the key to the same
                // Windows account that runs the daemon — service installs
                // would need to switch to LocalMachine scope; document if
                // we ever ship a service flavour.
#pragma warning disable CA1416 // platform-guarded above
                return ProtectedData.Unprotect(wrapped, optionalEntropy: null,
                    scope: DataProtectionScope.CurrentUser);
#pragma warning restore CA1416
            }
            var fresh = RandomNumberGenerator.GetBytes(32);
#pragma warning disable CA1416
            var sealed_ = ProtectedData.Protect(fresh, optionalEntropy: null,
                scope: DataProtectionScope.CurrentUser);
#pragma warning restore CA1416
            File.WriteAllBytes(path, sealed_);
            return fresh;
        }
        else
        {
            var path = Path.Combine(WdcPaths.DataRoot, "mcp-hmac.key");
            if (File.Exists(path))
            {
                return File.ReadAllBytes(path);
            }
            var fresh = RandomNumberGenerator.GetBytes(32);
            var opts = new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            };
            using (var stream = new FileStream(path, opts))
            {
                stream.Write(fresh, 0, fresh.Length);
            }
            return fresh;
        }
    }

    private static string Base64Url(byte[] bytes)
    {
        var s = Convert.ToBase64String(bytes);
        return s.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static byte[] Base64UrlDecode(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }
}
