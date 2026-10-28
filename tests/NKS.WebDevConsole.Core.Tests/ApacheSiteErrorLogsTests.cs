using NKS.WebDevConsole.Core.Services;

namespace NKS.WebDevConsole.Core.Tests;

/// <summary>
/// Tests for <see cref="ApacheSiteErrorLogs"/>: which Apache error log a site shows. A site must never
/// show the global error.log (the lines of every other site) when its vhost writes a log of its own.
/// </summary>
public sealed class ApacheSiteErrorLogsTests : IDisposable
{
    private readonly string _apache;
    private readonly string _logs;

    public ApacheSiteErrorLogsTests()
    {
        _apache = Path.Combine(Path.GetTempPath(), $"nks-wdc-site-error-logs-{Guid.NewGuid():N}");
        _logs = Path.Combine(_apache, "logs");
        Directory.CreateDirectory(Path.Combine(_apache, "conf", "sites-enabled"));
        Directory.CreateDirectory(_logs);
    }

    public void Dispose()
    {
        try { Directory.Delete(_apache, recursive: true); }
        catch { /* best-effort */ }
    }

    private void WriteVhost(string fileName, string text) =>
        File.WriteAllText(Path.Combine(_apache, "conf", "sites-enabled", fileName), text);

    private string Log(string name) => Path.Combine(_logs, name);

    [Fact]
    public void PlainVhost_ReadsOnlyItsOwnLog_EvenWhenTheGlobalLogExists()
    {
        WriteVhost("shop.loc.conf", """
            <VirtualHost *:80>
                ServerName shop.loc
                CustomLog "${APACHE_LOG_DIR}/shop.loc-access.log" wdc-combined
                ErrorLog "${APACHE_LOG_DIR}/shop.loc-error.log"
            </VirtualHost>
            """);
        File.WriteAllText(Log("error.log"), "[Sun Apr 13 10:00:00.000000 2026] [core:error] [pid 1] another site failed");

        var logs = ApacheSiteErrorLogs.Resolve("shop.loc", _apache, Log("wdc-error.log"));

        Assert.Equal([Log("shop.loc-error.log")], logs);
    }

    [Fact]
    public void HttpsVhost_ReadsTheSslLog_TheRedirectBlockWithoutErrorLogAddsNothing()
    {
        WriteVhost("shop.loc.conf", """
            # HTTP → HTTPS redirect
            <VirtualHost *:80>
                ServerName shop.loc
                Redirect permanent / https://shop.loc/
            </VirtualHost>
            <VirtualHost *:443>
                ServerName shop.loc
                # ErrorLog "${APACHE_LOG_DIR}/commented-out.log"
                ErrorLog "${APACHE_LOG_DIR}/shop.loc-ssl-error.log"
            </VirtualHost>
            """);

        var logs = ApacheSiteErrorLogs.Resolve("shop.loc", _apache);

        Assert.Equal([Log("shop.loc-ssl-error.log")], logs);
    }

    [Fact]
    public void VhostWithoutErrorLog_FallsBackToTheGlobalLogs()
    {
        WriteVhost("shop.loc.conf", "<VirtualHost *:80>\n    ServerName shop.loc\n</VirtualHost>\n");

        var logs = ApacheSiteErrorLogs.Resolve("shop.loc", _apache, Log("wdc-error.log"));

        Assert.Equal([Log("error.log"), Log("wdc-error.log")], logs);
    }

    [Fact]
    public void VhostThatWritesToTheGlobalLog_CountsAsHavingNoLogOfItsOwn()
    {
        WriteVhost("shop.loc.conf", "<VirtualHost *:80>\n    ErrorLog \"${APACHE_LOG_DIR}/error.log\"\n</VirtualHost>\n");

        Assert.Equal([Log("error.log")], ApacheSiteErrorLogs.Resolve("shop.loc", _apache));
    }

    [Fact]
    public void NoVhostYet_NamesTheFilesThePluginWrites_NeverTheGlobalLog()
    {
        var logs = ApacheSiteErrorLogs.Resolve("new.loc", _apache, Log("wdc-error.log"));

        Assert.Equal([Log("new.loc-ssl-error.log"), Log("new.loc-error.log")], logs);
        Assert.DoesNotContain(Log("error.log"), logs);
    }

    [Fact]
    public void Localhost_ReadsItsVhostFromTheLocalhostFileName()
    {
        WriteVhost("000-localhost.conf", "<VirtualHost *:80>\n    ErrorLog \"${APACHE_LOG_DIR}/localhost-error.log\"\n</VirtualHost>\n");

        Assert.Equal([Log("localhost-error.log")], ApacheSiteErrorLogs.Resolve("localhost", _apache));
    }

    [Fact]
    public void UnquotedAndRelativePaths_AreResolved_UnknownVariablesAreSkipped()
    {
        var logs = ApacheSiteErrorLogs.PerSiteLogs(
            "ErrorLog logs/a.loc-error.log\nErrorLog \"${SOMETHING_ELSE}/b.log\"\n  ErrorLog \"${APACHE_LOG_DIR}/c.loc-error.log\"\n",
            _apache, _logs);

        Assert.Equal([Log("a.loc-error.log"), Log("c.loc-error.log")], logs);
    }
}
