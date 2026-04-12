using NKS.WebDevConsole.Plugin.Cloudflare;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Tests for <see cref="CloudflareConfig"/>'s pure surface — the parts that
/// don't touch <c>WdcPaths.CloudflareRoot</c> and therefore can run without
/// contaminating the user's real Cloudflare config directory. Covered:
///
///   1. <see cref="CloudflareConfig.DomainHash"/> — 6 hex chars, deterministic,
///      stable across calls with the same (salt, domain). A regression here
///      would make re-enabling a tunnel for a site generate a DIFFERENT
///      public subdomain, breaking whatever the user had hardcoded upstream.
///
///   2. <see cref="CloudflareConfig.RenderSubdomain"/> — placeholder
///      substitution with <c>{stem}</c>, <c>{hash}</c>, <c>{user}</c> and
///      consecutive-dash collapse. Edge cases: .loc/.local/.test stripping,
///      empty hash (no salt), template with no placeholders.
///
///   3. <see cref="CloudflareConfig.Redacted"/> — security-sensitive: must
///      mask ApiToken (except last 4 chars) and TunnelToken (fully). Never
///      echo secrets back to the UI.
/// </summary>
public sealed class CloudflareConfigTests
{
    private static CloudflareConfig Fresh(string salt = "deadbeef") =>
        new() { InstallSalt = salt };

    // ── DomainHash ──────────────────────────────────────────────────────

    [Fact]
    public void DomainHash_ReturnsSixHexChars()
    {
        var cfg = Fresh();
        var hash = cfg.DomainHash("myapp.loc");
        Assert.Equal(6, hash.Length);
        Assert.Matches("^[a-f0-9]{6}$", hash);
    }

    [Fact]
    public void DomainHash_IsDeterministic()
    {
        var cfg = Fresh();
        var a = cfg.DomainHash("myapp.loc");
        var b = cfg.DomainHash("myapp.loc");
        Assert.Equal(a, b);
    }

    [Fact]
    public void DomainHash_DiffersByDomain()
    {
        var cfg = Fresh();
        var a = cfg.DomainHash("app-a.loc");
        var b = cfg.DomainHash("app-b.loc");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void DomainHash_DiffersBySalt()
    {
        var a = new CloudflareConfig { InstallSalt = "salt1" }.DomainHash("app.loc");
        var b = new CloudflareConfig { InstallSalt = "salt2" }.DomainHash("app.loc");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void DomainHash_EmptySalt_ReturnsEmpty()
    {
        var cfg = new CloudflareConfig { InstallSalt = "" };
        Assert.Equal("", cfg.DomainHash("myapp.loc"));
    }

    // ── RenderSubdomain ─────────────────────────────────────────────────

    [Fact]
    public void RenderSubdomain_DefaultTemplate_StemDashHash()
    {
        var cfg = Fresh();
        var rendered = cfg.RenderSubdomain("myapp.loc");
        // Default is {stem}-{hash}; stem="myapp" for .loc; hash is 6 hex chars
        Assert.StartsWith("myapp-", rendered);
        Assert.Equal(6 + "myapp-".Length, rendered.Length);
    }

    [Theory]
    [InlineData("myapp.loc", "myapp")]
    [InlineData("myapp.local", "myapp")]
    [InlineData("myapp.test", "myapp")]
    [InlineData("MyApp.LOC", "MyApp")] // case-insensitive suffix strip
    public void RenderSubdomain_StripsLocalTlds(string domain, string expectedStem)
    {
        var cfg = new CloudflareConfig
        {
            SubdomainTemplate = "{stem}",
            InstallSalt = "x",
        };
        Assert.Equal(expectedStem, cfg.RenderSubdomain(domain));
    }

    [Fact]
    public void RenderSubdomain_EmptyHash_CollapsesTrailingDash()
    {
        var cfg = new CloudflareConfig
        {
            InstallSalt = "",  // empty salt → empty hash
            SubdomainTemplate = "{stem}-{hash}",
        };
        // "myapp-" collapses via the "-+" regex + Trim('-') to just "myapp"
        Assert.Equal("myapp", cfg.RenderSubdomain("myapp.loc"));
    }

    [Fact]
    public void RenderSubdomain_NoPlaceholders_ReturnsStaticString()
    {
        var cfg = new CloudflareConfig
        {
            SubdomainTemplate = "my-static-subdomain",
            InstallSalt = "x",
        };
        Assert.Equal("my-static-subdomain", cfg.RenderSubdomain("whatever.loc"));
    }

    [Fact]
    public void RenderSubdomain_CollapsesConsecutiveDashes()
    {
        var cfg = new CloudflareConfig
        {
            SubdomainTemplate = "{stem}--{hash}",  // double dash on purpose
            InstallSalt = "deadbeef",
        };
        var rendered = cfg.RenderSubdomain("myapp.loc");
        // "myapp--abcdef" → "myapp-abcdef" after "-+" collapse
        Assert.DoesNotContain("--", rendered);
        Assert.StartsWith("myapp-", rendered);
    }

    [Fact]
    public void RenderSubdomain_UserPlaceholder_Substituted()
    {
        var cfg = new CloudflareConfig
        {
            SubdomainTemplate = "{stem}-{user}",
            InstallSalt = "x",
        };
        var rendered = cfg.RenderSubdomain("myapp.loc");
        Assert.StartsWith("myapp-", rendered);
        // User is environment-dependent but always lowercased
        Assert.Equal(rendered.ToLowerInvariant(), rendered);
    }

    [Fact]
    public void RenderSubdomain_NullTemplate_FallsBackToDefault()
    {
        var cfg = new CloudflareConfig
        {
            SubdomainTemplate = null!,
            InstallSalt = "deadbeef",
        };
        var rendered = cfg.RenderSubdomain("myapp.loc");
        Assert.StartsWith("myapp-", rendered);
    }

    // ── Redacted ────────────────────────────────────────────────────────

    [Fact]
    public void Redacted_MasksApiToken_KeepingLastFour()
    {
        var cfg = new CloudflareConfig { ApiToken = "secret-abc1234" };
        var safe = cfg.Redacted();
        Assert.NotNull(safe.ApiToken);
        Assert.EndsWith("1234", safe.ApiToken!);
        Assert.StartsWith("••••••••", safe.ApiToken!);
        Assert.DoesNotContain("secret", safe.ApiToken!);
    }

    [Fact]
    public void Redacted_MasksTunnelToken_FullyHidden()
    {
        var cfg = new CloudflareConfig { TunnelToken = "eyJhbGciOiJIUzI1NiJ9.xxxx.yyyy" };
        var safe = cfg.Redacted();
        Assert.NotNull(safe.TunnelToken);
        Assert.DoesNotContain("eyJ", safe.TunnelToken!);
        Assert.Equal("••••••••", safe.TunnelToken);
    }

    [Fact]
    public void Redacted_NullTokens_StayNull()
    {
        var cfg = new CloudflareConfig { ApiToken = null, TunnelToken = null };
        var safe = cfg.Redacted();
        Assert.Null(safe.ApiToken);
        Assert.Null(safe.TunnelToken);
    }

    [Fact]
    public void Redacted_EmptyTokens_StayNull()
    {
        var cfg = new CloudflareConfig { ApiToken = "", TunnelToken = "" };
        var safe = cfg.Redacted();
        Assert.Null(safe.ApiToken);
        Assert.Null(safe.TunnelToken);
    }

    [Fact]
    public void Redacted_PreservesNonSecretFields()
    {
        var cfg = new CloudflareConfig
        {
            CloudflaredPath = @"C:\tools\cloudflared.exe",
            TunnelName = "my-tunnel",
            TunnelId = "abc-123-def",
            AccountId = "acc-456",
            DefaultZoneId = "zone-789",
            StartupTimeoutSecs = 30,
            SubdomainTemplate = "{stem}-{user}",
        };
        var safe = cfg.Redacted();
        Assert.Equal(cfg.CloudflaredPath, safe.CloudflaredPath);
        Assert.Equal(cfg.TunnelName, safe.TunnelName);
        Assert.Equal(cfg.TunnelId, safe.TunnelId);
        Assert.Equal(cfg.AccountId, safe.AccountId);
        Assert.Equal(cfg.DefaultZoneId, safe.DefaultZoneId);
        Assert.Equal(cfg.StartupTimeoutSecs, safe.StartupTimeoutSecs);
        Assert.Equal(cfg.SubdomainTemplate, safe.SubdomainTemplate);
    }

    [Fact]
    public void Redacted_NeverLeaksOriginalApiTokenInString()
    {
        var cfg = new CloudflareConfig { ApiToken = "very-long-secret-token-abcdefghijk" };
        var safe = cfg.Redacted();
        Assert.DoesNotContain("very-long-secret", safe.ApiToken!);
    }

    // ── Defaults ────────────────────────────────────────────────────────

    [Fact]
    public void Default_StartupTimeoutSecs_Is20()
    {
        Assert.Equal(20, new CloudflareConfig().StartupTimeoutSecs);
    }

    [Fact]
    public void Default_SubdomainTemplate_IsStemDashHash()
    {
        Assert.Equal("{stem}-{hash}", new CloudflareConfig().SubdomainTemplate);
    }
}
