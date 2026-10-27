using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NKS.WebDevConsole.Daemon.Ssl;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// The SSL page used to show "mkcert ready" and offer "Install CA" without
/// looking at the machine. These pin the CA check to the trust store, using a
/// temp CAROOT only (nothing is added to any store).
/// </summary>
public sealed class SslCaStatusTests : IDisposable
{
    private readonly string _caRoot = Path.Combine(Path.GetTempPath(), $"nks-wdc-caroot-{Guid.NewGuid():N}");

    public SslCaStatusTests() => Directory.CreateDirectory(_caRoot);

    public void Dispose()
    {
        try { Directory.Delete(_caRoot, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void NoRootCaFile_IsNotTrusted()
    {
        Assert.False(SslEndpoints.IsCaTrusted(_caRoot));
    }

    [Fact]
    public void RootCaFileThatNoStoreTrusts_IsNotTrusted()
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest($"CN=wdc-test-{Guid.NewGuid():N}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        File.WriteAllText(Path.Combine(_caRoot, "rootCA.pem"), cert.ExportCertificatePem());

        Assert.False(SslEndpoints.IsCaTrusted(_caRoot));
    }

    [Fact]
    public void UnreadableRootCaFile_IsNotTrusted()
    {
        File.WriteAllText(Path.Combine(_caRoot, "rootCA.pem"), "not a certificate");

        Assert.False(SslEndpoints.IsCaTrusted(_caRoot));
    }
}
