using NKS.WebDevConsole.Daemon.Config;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// The ports.* → plugin config table shared by boot hydration and the
/// PUT /api/settings hook. ports.phpFpmBase used to be saved and read by nothing.
/// </summary>
public class ServicePortSettingsTests
{
    private sealed class ApacheLikeConfig
    {
        public int HttpPort { get; set; } = 80;
        public int HttpsPort { get; set; } = 443;
        public int PhpFpmBasePort { get; set; } = 9000;
    }

    private sealed class PhpLikeConfig
    {
        public int PhpFpmBasePort { get; set; } = 9000;
    }

    /// <summary>A plugin build from before the PhpFpmBasePort property.</summary>
    private sealed class OldApacheConfig
    {
        public int HttpPort { get; set; } = 80;
    }

    private static Func<string, string?> Values(params (string Key, string Value)[] values) =>
        key => values.FirstOrDefault(v => v.Key == key).Value;

    [Fact]
    public void PhpFpmBase_reaches_php_and_apache()
    {
        var php = new PhpLikeConfig();
        var apache = new ApacheLikeConfig();

        Assert.Equal(new[] { "PhpFpmBasePort" }, ServicePortSettings.Apply(php, "php", Values(("ports.phpFpmBase", "9100"))));
        ServicePortSettings.Apply(apache, "Apache", Values(("ports.phpFpmBase", "9100"), ("ports.http", "8081")));

        Assert.Equal(9100, php.PhpFpmBasePort);
        Assert.Equal(9100, apache.PhpFpmBasePort);
        Assert.Equal(8081, apache.HttpPort);
        Assert.Equal(443, apache.HttpsPort);
    }

    [Fact]
    public void Other_webservers_and_services_do_not_take_the_fpm_base()
    {
        foreach (var id in new[] { "caddy", "nginx", "mysql", "redis", "mailpit" })
            Assert.DoesNotContain(ServicePortSettings.For(id), m => m.Key == "ports.phpFpmBase");
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData(null)]
    public void Invalid_or_missing_values_leave_the_config_alone(string? value)
    {
        var php = new PhpLikeConfig();
        Assert.Empty(ServicePortSettings.Apply(php, "php", _ => value));
        Assert.Equal(9000, php.PhpFpmBasePort);
    }

    [Fact]
    public void An_older_plugin_config_without_the_property_is_skipped()
    {
        var old = new OldApacheConfig();
        Assert.Equal(new[] { "HttpPort" }, ServicePortSettings.Apply(old, "apache", Values(("ports.http", "81"), ("ports.phpFpmBase", "9100"))));
        Assert.Equal(81, old.HttpPort);
    }

    [Theory]
    [InlineData("ports.http", true)]
    [InlineData("ports.https", true)]
    [InlineData("ports.phpFpmBase", true)]
    [InlineData("ports.mysql", false)]
    [InlineData("ports.mailpitHttp", false)]
    public void Vhosts_are_regenerated_for_keys_written_into_them(string key, bool expected) =>
        Assert.Equal(expected, ServicePortSettings.ChangesVhosts(key));

    [Fact]
    public void Existing_service_ports_keep_their_properties()
    {
        Assert.Contains(("ports.mysql", "Port"), ServicePortSettings.For("mysql"));
        Assert.Contains(("ports.mailpitSmtp", "SmtpPort"), ServicePortSettings.For("mailpit"));
        Assert.Contains(("ports.mailpitHttp", "WebPort"), ServicePortSettings.For("mailpit"));
        Assert.Contains(("ports.https", "HttpsPort"), ServicePortSettings.For("nginx"));
        Assert.Empty(ServicePortSettings.For("cloudflare"));
    }
}
