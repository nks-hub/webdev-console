using Microsoft.Extensions.Logging.Abstractions;
using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Daemon.Config;
using NKS.WebDevConsole.Daemon.Sites;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// What SiteManager records for the Overview activity feed: a site created,
/// changed (and what changed, PHP version included) and deleted.
/// </summary>
public sealed class SiteActivityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wdc-site-activity-" + Guid.NewGuid().ToString("N"));
    private readonly ActivityTestDb _db = new();
    private readonly SiteManager _manager;

    public SiteActivityTests()
    {
        _manager = new SiteManager(NullLogger<SiteManager>.Instance, new TemplateEngine(), new AtomicWriter(),
            Path.Combine(_root, "sites"), Path.Combine(_root, "generated"), _db.Repository());
    }

    public void Dispose()
    {
        _db.Dispose();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static SiteConfig Site(string php = "8.3") =>
        new() { Domain = "shop.loc", DocumentRoot = "C:/htdocs/shop", PhpVersion = php };

    [Fact]
    public async Task Creating_a_site_records_an_insert_with_the_domain()
    {
        await _manager.CreateAsync(Site());

        var row = Assert.Single(_db.Rows("entity_type = 'site'"));
        Assert.Equal("shop.loc", (string)row.entity_name);
        Assert.Equal("INSERT", (string)row.operation);
    }

    [Fact]
    public async Task Changing_the_php_version_records_an_update_with_the_old_and_new_version()
    {
        await _manager.CreateAsync(Site("8.3"));

        await _manager.UpdateAsync(Site("8.4"));

        var row = Assert.Single(_db.Rows("operation = 'UPDATE'"));
        Assert.Equal("shop.loc", (string)row.entity_name);
        Assert.Equal("""["phpVersion"]""", (string)row.changed_fields);
        Assert.Equal("""{"phpVersion":"8.3"}""", (string)row.old_values);
        Assert.Equal("""{"phpVersion":"8.4"}""", (string)row.new_values);
    }

    [Fact]
    public async Task An_edit_made_on_the_object_the_registry_holds_is_still_seen()
    {
        // Several callers do this (the enable switch, the orchestrator); the old state is only on disk.
        await _manager.CreateAsync(Site("8.3"));
        var held = _manager.Get("shop.loc")!;
        held.PhpVersion = "8.4";
        held.Enabled = false;

        await _manager.UpdateAsync(held);

        var row = Assert.Single(_db.Rows("operation = 'UPDATE'"));
        Assert.Equal("""["phpVersion","enabled"]""", ((string)row.changed_fields).Replace(" ", ""));
    }

    [Fact]
    public async Task Saving_a_site_without_a_change_records_nothing()
    {
        await _manager.CreateAsync(Site());

        await _manager.UpdateAsync(Site());

        Assert.Empty(_db.Rows("operation = 'UPDATE'"));
    }

    [Fact]
    public async Task Changed_environment_variables_are_recorded_by_name_without_their_values()
    {
        await _manager.CreateAsync(Site());
        var changed = Site();
        changed.Environment["DB_PASSWORD"] = "hunter2";

        await _manager.UpdateAsync(changed);

        var row = Assert.Single(_db.Rows("operation = 'UPDATE'"));
        Assert.Equal("""["environment"]""", (string)row.changed_fields);
        Assert.DoesNotContain("hunter2", (string)row.old_values + (string)row.new_values);
    }

    [Fact]
    public async Task Deleting_a_site_records_a_delete_and_deleting_a_missing_one_does_not()
    {
        await _manager.CreateAsync(Site());

        Assert.True(_manager.Delete("shop.loc"));
        Assert.False(_manager.Delete("shop.loc"));

        var row = Assert.Single(_db.Rows("operation = 'DELETE'"));
        Assert.Equal("shop.loc", (string)row.entity_name);
    }

    [Fact]
    public void DescribeChange_gives_up_on_a_TOML_it_cannot_read()
    {
        Assert.Null(SiteManager.DescribeChange("this is = = not toml [", Site()));
    }
}
