using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Daemon.Sites;

namespace NKS.WebDevConsole.Daemon.Tests;

public class SitePhpSettingsValidationTests
{
    [Fact]
    public void Null_settings_are_valid()
    {
        SitePhpIniWriter.Validate(null);
    }

    [Fact]
    public void Typical_values_are_valid()
    {
        SitePhpIniWriter.Validate(new SitePhpSettings
        {
            MemoryLimit = "512M",
            PostMaxSize = "-1",
            UploadMaxFilesize = "64m",
            MaxExecutionTime = 0,
            MaxInputTime = -1,
            Timezone = "America/Argentina/Buenos_Aires",
            ExtraExtensions = ["imagick", "pdo_sqlite"],
            DisabledExtensions = ["xdebug"],
        });
    }

    [Theory]
    [InlineData("256M\nauto_prepend_file = /tmp/x.php")]
    [InlineData("1 G")]
    [InlineData("lots")]
    public void Memory_limit_rejects_values_that_leave_the_line(string value)
    {
        Assert.Throws<ArgumentException>(() =>
            SitePhpIniWriter.Validate(new SitePhpSettings { MemoryLimit = value }));
    }

    [Theory]
    [InlineData("Europe/Prague\nauto_prepend_file=/tmp/x.php")]
    [InlineData("../../etc")]
    public void Timezone_rejects_injection(string value)
    {
        Assert.Throws<ArgumentException>(() =>
            SitePhpIniWriter.Validate(new SitePhpSettings { Timezone = value }));
    }

    [Theory]
    [InlineData("gd\nauto_prepend_file=/tmp/x.php")]
    [InlineData("../evil")]
    public void Extension_names_reject_injection(string value)
    {
        Assert.Throws<ArgumentException>(() =>
            SitePhpIniWriter.Validate(new SitePhpSettings { ExtraExtensions = [value] }));
        Assert.Throws<ArgumentException>(() =>
            SitePhpIniWriter.Validate(new SitePhpSettings { DisabledExtensions = [value] }));
    }

    [Fact]
    public void Negative_timeouts_below_minus_one_are_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            SitePhpIniWriter.Validate(new SitePhpSettings { MaxExecutionTime = -5 }));
    }
}
