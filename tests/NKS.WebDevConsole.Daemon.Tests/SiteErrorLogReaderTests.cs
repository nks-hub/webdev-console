using NKS.WebDevConsole.Daemon.Sites;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// What the Logs tab of a site shows: its own Apache error log, and never the lines of other sites
/// from the global error.log while its vhost writes a log of its own.
/// </summary>
public sealed class SiteErrorLogReaderTests : IDisposable
{
    private readonly string _root;
    private readonly string _apache;
    private readonly string _logs;
    private readonly string _phpLogs;
    private readonly string _wdcApacheLog;

    public SiteErrorLogReaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"nks-wdc-site-error-reader-{Guid.NewGuid():N}");
        _apache = Path.Combine(_root, "apache");
        _logs = Path.Combine(_apache, "logs");
        _phpLogs = Path.Combine(_root, "php");
        _wdcApacheLog = Path.Combine(_root, "wdc-apache-error.log");
        Directory.CreateDirectory(Path.Combine(_apache, "conf", "sites-enabled"));
        Directory.CreateDirectory(_logs);
        Directory.CreateDirectory(_phpLogs);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private static string Line(string message) =>
        $"[Sun Apr 13 10:00:00.000000 2026] [core:error] [pid 1] {message}";

    private void Vhost(string domain, string body) =>
        File.WriteAllText(Path.Combine(_apache, "conf", "sites-enabled", $"{domain}.conf"),
            $"<VirtualHost *:80>\n    ServerName {domain}\n{body}</VirtualHost>\n");

    private void WriteLog(string name, params string[] messages) =>
        File.WriteAllLines(Path.Combine(_logs, name), messages.Select(Line));

    private IReadOnlyList<string> Messages(string domain) =>
        SiteErrorLogReader.Read(domain, [_apache], _wdcApacheLog, _phpLogs, null, 100, null)
            .Select(e => e.Message)
            .ToList();

    [Fact]
    public void NewSiteWithoutALogYet_ShowsNothing_NotTheGlobalLogOfOtherSites()
    {
        Vhost("new.loc", "    ErrorLog \"${APACHE_LOG_DIR}/new.loc-error.log\"\n");
        WriteLog("error.log", "another site failed", "Apache started");
        File.WriteAllLines(_wdcApacheLog, [Line("daemon-side Apache line")]);

        Assert.Empty(Messages("new.loc"));
    }

    [Fact]
    public void OnceTheSiteLogExists_OnlyItsOwnLinesAreShown()
    {
        Vhost("new.loc", "    ErrorLog \"${APACHE_LOG_DIR}/new.loc-error.log\"\n");
        WriteLog("error.log", "another site failed");

        Assert.Empty(Messages("new.loc"));

        WriteLog("new.loc-error.log", "the site failed");

        Assert.Equal(["the site failed"], Messages("new.loc"));
    }

    [Fact]
    public void TwoSites_EachShowOnlyTheirOwnLog()
    {
        Vhost("a.loc", "    ErrorLog \"${APACHE_LOG_DIR}/a.loc-error.log\"\n");
        Vhost("b.loc", "    ErrorLog \"${APACHE_LOG_DIR}/b.loc-error.log\"\n");
        WriteLog("a.loc-error.log", "a failed");
        WriteLog("b.loc-error.log", "b failed");
        WriteLog("error.log", "a failed", "b failed");

        Assert.Equal(["a failed"], Messages("a.loc"));
        Assert.Equal(["b failed"], Messages("b.loc"));
    }

    [Fact]
    public void HttpsSite_ReadsTheSslLog_NotTheStaleLogOfItsHttpDays()
    {
        Vhost("shop.loc", "    ErrorLog \"${APACHE_LOG_DIR}/shop.loc-ssl-error.log\"\n");
        WriteLog("shop.loc-error.log", "from before HTTPS");
        WriteLog("shop.loc-ssl-error.log", "over HTTPS");

        Assert.Equal(["over HTTPS"], Messages("shop.loc"));
    }

    [Fact]
    public void VhostWithoutALogOfItsOwn_FallsBackToTheGlobalLogs()
    {
        Vhost("plain.loc", "");
        WriteLog("error.log", "from the global log");
        File.WriteAllLines(_wdcApacheLog, [Line("from the daemon's Apache log")]);

        Assert.Equal(["from the daemon's Apache log", "from the global log"], Messages("plain.loc").Order());
    }

    [Fact]
    public void SiteWithoutAVhostYet_ShowsNothing_NotTheGlobalLog()
    {
        WriteLog("error.log", "another site failed");

        Assert.Empty(Messages("unapplied.loc"));
    }

    [Fact]
    public void PhpLogsAreStillIncluded()
    {
        Vhost("new.loc", "    ErrorLog \"${APACHE_LOG_DIR}/new.loc-error.log\"\n");
        File.WriteAllLines(Path.Combine(_phpLogs, "php85-errors.log"),
            ["[13-Apr-2026 10:00:00 UTC] PHP Fatal error: Uncaught Error in /srv/new.loc/index.php on line 3"]);

        var entries = SiteErrorLogReader.Read("new.loc", [_apache], _wdcApacheLog, _phpLogs, null, 100, null);

        Assert.Single(entries);
        Assert.Equal("php-error", entries[0].Source);
    }
}
