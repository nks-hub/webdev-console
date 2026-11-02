using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NKS.WebDevConsole.Daemon.Sites;
using Xunit;

namespace NKS.WebDevConsole.Daemon.Tests;

public class CertCoversSiteTests
{
    private static X509Certificate2 MakeCert(DateTimeOffset notBefore, DateTimeOffset notAfter, params string[] names)
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var n in names)
        {
            if (IPAddress.TryParse(n, out var ip)) san.AddIpAddress(ip);
            else san.AddDnsName(n);
        }
        req.CertificateExtensions.Add(san.Build());
        return req.CreateSelfSigned(notBefore, notAfter);
    }

    private static readonly DateTime Now = new(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Valid_cert_naming_domain_aliases_and_ip_is_kept()
    {
        using var cert = MakeCert(Now.AddDays(-1), Now.AddDays(800), "shop.loc", "www.shop.loc", "*.shop.loc", "10.0.2.2");
        Assert.True(SiteOrchestrator.CertCovers(cert, "shop.loc", new[] { "www.shop.loc", "*.shop.loc", "10.0.2.2" }, Now));
    }

    [Fact]
    public void Missing_alias_needs_a_new_cert()
    {
        using var cert = MakeCert(Now.AddDays(-1), Now.AddDays(800), "shop.loc");
        Assert.False(SiteOrchestrator.CertCovers(cert, "shop.loc", new[] { "www.shop.loc" }, Now));
    }

    [Fact]
    public void Cert_expiring_within_30_days_is_renewed()
    {
        using var cert = MakeCert(Now.AddDays(-800), Now.AddDays(10), "shop.loc");
        Assert.False(SiteOrchestrator.CertCovers(cert, "shop.loc", Array.Empty<string>(), Now));
    }

    [Fact]
    public void Missing_files_mean_no_cert()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wdc-cert-test-" + Guid.NewGuid().ToString("N"));
        Assert.False(SiteOrchestrator.ExistingCertCovers(dir, "shop.loc", Array.Empty<string>(), Now));
    }
}
