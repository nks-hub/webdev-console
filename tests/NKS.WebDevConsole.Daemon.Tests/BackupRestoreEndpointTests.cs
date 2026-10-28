using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Daemon.Backup;
using NKS.WebDevConsole.Daemon.Config;
using NKS.WebDevConsole.Daemon.Data;
using NKS.WebDevConsole.Daemon.Services;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// The real restore routes behind the real auth middleware. A backup_restore
/// intent names no content, so an MCP token must not turn an approved
/// configuration restore into a database or site-file overwrite; the user's
/// token keeps both options.
/// </summary>
public class BackupRestoreEndpointTests : IAsyncLifetime
{
    private const string GuiToken = "gui-token-0123456789";
    private const string McpToken = "mcp-token-0123456789";

    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"wdc-restore-ep-{Guid.NewGuid():N}");
    private string WdcDir => Path.Combine(_tempRoot, ".wdc");
    private string SiteRoot => Path.Combine(_tempRoot, "shop", "www");
    private string DbPath => Path.Combine(_tempRoot, "state-test.db");

    private readonly FakeMysql _mysql = new(["shop"]);
    private Mock<IDeployIntentValidator> _validator = null!;
    private BackupManager _bm = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private byte[] _archive = [];

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(WdcDir);
        Directory.CreateDirectory(SiteRoot);
        File.WriteAllText(Path.Combine(SiteRoot, "index.php"), "original");

        var database = new Database(DbPath);
        using (var conn = database.CreateConnection())
        {
            conn.Execute("""
                CREATE TABLE settings (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    category TEXT NOT NULL, key TEXT NOT NULL, value TEXT NOT NULL,
                    value_type TEXT NOT NULL DEFAULT 'string', description TEXT,
                    is_readonly INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                    updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                    UNIQUE(category, key));
                """);
        }
        var settings = new SettingsStore(database);
        settings.Set("mcp", "enabled", "true");
        settings.Set("mcp", "capabilities.backup_restore", "true");

        var sites = new Mock<ISiteRegistry>();
        sites.Setup(r => r.Sites).Returns(new Dictionary<string, SiteConfig>
        {
            ["shop.loc"] = new() { Domain = "shop.loc", DocumentRoot = SiteRoot },
        });
        _bm = new BackupManager(NullLogger<BackupManager>.Instance, WdcDir, sites.Object, _mysql);
        var archivePath = _bm.CreateBackup("source.zip",
            BackupContentFlags.Default | BackupContentFlags.Databases | BackupContentFlags.Docroots).Path;
        _archive = File.ReadAllBytes(archivePath);
        File.WriteAllText(Path.Combine(SiteRoot, "index.php"), "changed after the backup");

        // A confirmed backup_restore intent: the validator says yes.
        _validator = new Mock<IDeployIntentValidator>();
        _validator.Setup(x => x.ValidateAndConsumeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(IntentValidationResult.Allow());

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(_validator.Object);
        builder.Services.AddSingleton(_bm);
        builder.Services.AddSingleton(new SseService());
        _app = builder.Build();
        _app.UseDaemonAuth(GuiToken, McpToken);
        _app.MapBackupEndpoints(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best effort */ }
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(string path, string token, HttpContent? content)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (token == McpToken) req.Headers.Add("X-Intent-Token", "intent.token.sig");
        using var res = await _client.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        return (res.StatusCode, text.Length > 0 ? JsonDocument.Parse(text).RootElement.Clone() : default);
    }

    private static StringContent Json(string flag) =>
        new($"{{\"{flag}\":true}}", Encoding.UTF8, "application/json");

    private MultipartFormDataContent Upload(params string[] flags)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(_archive);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        form.Add(file, "file", "backup.zip");
        foreach (var flag in flags) form.Add(new StringContent("true"), flag);
        return form;
    }

    private void AssertNothingRestored()
    {
        Assert.Equal("changed after the backup", File.ReadAllText(Path.Combine(SiteRoot, "index.php")));
        Assert.Empty(_mysql.Imported);
    }

    [Theory]
    [InlineData("databases")]
    [InlineData("siteFiles")]
    public async Task Mcp_token_cannot_ask_for_data_on_the_id_route_and_keeps_its_intent(string flag)
    {
        var (status, body) = await PostAsync("/api/backup/source.zip/restore", McpToken, Json(flag));

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("mcp_forbidden", body.GetProperty("error").GetString());
        AssertIntentNotConsumed();
        AssertNothingRestored();
    }

    [Theory]
    [InlineData("databases")]
    [InlineData("siteFiles")]
    public async Task Mcp_token_cannot_ask_for_data_when_uploading_an_archive_and_keeps_its_intent(string flag)
    {
        var (status, body) = await PostAsync("/api/restore", McpToken, Upload(flag));

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("mcp_forbidden", body.GetProperty("error").GetString());
        AssertIntentNotConsumed();
        AssertNothingRestored();
    }

    private void AssertIntentNotConsumed() =>
        _validator.Verify(x => x.ValidateAndConsumeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);

    [Fact]
    public async Task Mcp_upload_without_the_flags_still_goes_through_its_intent()
    {
        var (status, _) = await PostAsync("/api/restore", McpToken, Upload());

        Assert.Equal(HttpStatusCode.OK, status);
        _validator.Verify(x => x.ValidateAndConsumeAsync(It.IsAny<string>(), "backup_restore", It.IsAny<string>(),
            It.IsAny<string>(), false, It.IsAny<CancellationToken>()), Times.Once);
        AssertNothingRestored();
    }

    [Fact]
    public async Task Mcp_token_without_the_flags_still_goes_through_its_intent()
    {
        // The archive is not in the real backups folder, so the request ends in
        // 404 after the guard and after the intent check.
        var (status, _) = await PostAsync("/api/backup/missing.zip/restore", McpToken, null);

        Assert.Equal(HttpStatusCode.NotFound, status);
        _validator.Verify(x => x.ValidateAndConsumeAsync(It.IsAny<string>(), "backup_restore", It.IsAny<string>(),
            It.IsAny<string>(), false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task User_token_restores_databases_and_site_files()
    {
        var (status, body) = await PostAsync("/api/restore", GuiToken, Upload("databases", "siteFiles"));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("original", File.ReadAllText(Path.Combine(SiteRoot, "index.php")));
        Assert.Equal("CREATE DATABASE `shop`;", _mysql.Imported["shop"]);
        var items = body.GetProperty("items").EnumerateArray()
            .Select(i => (i.GetProperty("content").GetString(), i.GetProperty("name").GetString())).ToList();
        Assert.Contains(("Databases", "shop"), items);
        Assert.Contains(("Docroots", "shop.loc"), items);
        Assert.Empty(body.GetProperty("skipped").EnumerateArray());
    }

    [Fact]
    public async Task User_token_with_the_flags_is_not_stopped_by_the_guard_on_the_id_route()
    {
        var (status, _) = await PostAsync("/api/backup/missing.zip/restore", GuiToken, Json("databases"));

        // Past the guard: the archive lookup answers, not the MCP check.
        Assert.Equal(HttpStatusCode.NotFound, status);
    }
}
