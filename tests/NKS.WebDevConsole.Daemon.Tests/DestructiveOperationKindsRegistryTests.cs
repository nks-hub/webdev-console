using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Daemon.Mcp;
using Xunit;

namespace NKS.WebDevConsole.Daemon.Tests;

public sealed class DestructiveOperationKindsRegistryTests
{
    [Fact]
    public void Constructor_SeedsLegacyCoreKinds()
    {
        var reg = new DestructiveOperationKindsRegistry();
        // The four legacy kinds must be present immediately so existing
        // intent flows keep working without any plugin contribution.
        Assert.NotNull(reg.Get("deploy"));
        Assert.NotNull(reg.Get("rollback"));
        Assert.NotNull(reg.Get("cancel"));
        Assert.NotNull(reg.Get("restore"));
        // 'restore' is the only legacy kind tagged Destructive (operator
        // gets the typed-confirm banner instead of the 2s countdown).
        Assert.Equal(DangerLevel.Destructive, reg.Get("restore")!.Danger);
        Assert.Equal(DangerLevel.Reversible, reg.Get("deploy")!.Danger);
        // All seeded kinds are owned by the pseudo-plugin "core" so
        // UnregisterPlugin can never accidentally drop them.
        Assert.Equal("core", reg.Get("deploy")!.PluginId);
    }

    [Fact]
    public void Register_AddsPluginKind_ReturnsViaGet()
    {
        var reg = new DestructiveOperationKindsRegistry();
        reg.Register("nksbackup:restore", "Restore database backup", "nksbackup");
        var got = reg.Get("nksbackup:restore");
        Assert.NotNull(got);
        Assert.Equal("Restore database backup", got!.Label);
        Assert.Equal("nksbackup", got.PluginId);
        Assert.Equal(DangerLevel.Reversible, got.Danger);
    }

    [Fact]
    public void Register_DuplicateId_Throws()
    {
        var reg = new DestructiveOperationKindsRegistry();
        reg.Register("plugin_a:op", "Op A", "plugin-a");
        // Two plugins fighting for the same id is a bug; surface loudly.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            reg.Register("plugin_a:op", "Op A clone", "plugin-b"));
        Assert.Contains("already registered", ex.Message);
        // Original registration unchanged.
        Assert.Equal("plugin-a", reg.Get("plugin_a:op")!.PluginId);
    }

    [Theory]
    [InlineData("UPPERCASE")]   // upper letters
    [InlineData("1starts_digit")] // starts with digit
    [InlineData("has space")]   // space
    [InlineData("has-dash")]    // dash not allowed (only _ and :)
    [InlineData("")]            // empty
    [InlineData("a;b")]         // sql-injection char
    public void Register_InvalidId_Throws(string badId)
    {
        var reg = new DestructiveOperationKindsRegistry();
        Assert.Throws<ArgumentException>(() =>
            reg.Register(badId, "label", "plugin-x"));
    }

    [Fact]
    public void Register_TooLongId_Throws()
    {
        var reg = new DestructiveOperationKindsRegistry();
        var tooLong = "a" + new string('b', 64); // 65 chars total
        Assert.Throws<ArgumentException>(() =>
            reg.Register(tooLong, "label", "plugin-x"));
    }

    [Fact]
    public void UnregisterPlugin_DropsOnlyOwnedKinds()
    {
        var reg = new DestructiveOperationKindsRegistry();
        reg.Register("plugin_a:op1", "A op 1", "plugin-a");
        reg.Register("plugin_a:op2", "A op 2", "plugin-a");
        reg.Register("plugin_b:op", "B op", "plugin-b");

        reg.UnregisterPlugin("plugin-a");

        Assert.Null(reg.Get("plugin_a:op1"));
        Assert.Null(reg.Get("plugin_a:op2"));
        Assert.NotNull(reg.Get("plugin_b:op"));
        // Core kinds untouched.
        Assert.NotNull(reg.Get("deploy"));
    }

    [Fact]
    public void UnregisterPlugin_CoreId_IsNoOp()
    {
        var reg = new DestructiveOperationKindsRegistry();
        // Defensive: trying to unregister the pseudo-plugin "core" would
        // wipe the seeded legacy kinds and break every shipped deploy.
        // The implementation must reject this.
        reg.UnregisterPlugin("core");
        Assert.NotNull(reg.Get("deploy"));
        Assert.NotNull(reg.Get("rollback"));
    }

    [Fact]
    public void UnregisterPlugin_UnknownId_IsNoOp()
    {
        var reg = new DestructiveOperationKindsRegistry();
        var beforeCount = reg.List().Count;
        reg.UnregisterPlugin("never-registered");
        // Unregistering a non-existent plugin must not change the seeded
        // core kinds. Test is additive-tolerant: doesn't pin the seed
        // count so adding a new core kind in the registry doesn't
        // require updating this assertion. The legacy core kinds (deploy,
        // rollback, cancel, restore) MUST still be present though —
        // those are part of the public contract.
        var after = reg.List();
        Assert.Equal(beforeCount, after.Count);
        foreach (var legacyId in new[] { "deploy", "rollback", "cancel", "restore" })
            Assert.NotNull(reg.Get(legacyId));
    }

    [Fact]
    public void List_IsDeterministicallyOrdered()
    {
        var reg = new DestructiveOperationKindsRegistry();
        reg.Register("zzz:op", "Z op", "zzz-plugin");
        reg.Register("aaa:op", "A op", "aaa-plugin");

        var list = reg.List();
        // Sorted by pluginId (case-insensitive), then by id. Plugin order
        // ends up alphabetical: "aaa-plugin", "core" (legacy seeded),
        // "zzz-plugin". The exact set of core kinds is registry-defined
        // and grows over time — assert structure (sort order, distinct
        // plugin block) rather than enumerating every core id, so adding
        // a new core kind doesn't break this test.
        var pluginIds = list.Select(k => k.PluginId).Distinct().ToList();
        Assert.Equal(new[] { "aaa-plugin", "core", "zzz-plugin" }, pluginIds);

        // The legacy four MUST appear in alphabetical order at the start
        // of the core block (the contract this test originally pinned).
        // Tail of core block can grow with future kinds — assert prefix.
        var coreIds = list.Where(k => k.PluginId == "core").Select(k => k.Id).ToArray();
        Assert.Equal(new[] { "cancel", "deploy", "restore", "rollback" },
            coreIds.Where(id => id is "cancel" or "deploy" or "restore" or "rollback").ToArray());

        // Whole core block is sorted alphabetically (extra kinds slot in
        // their own positions — `database_drop`, `dns_record_delete` etc.
        // — verifies the registry keeps its determinism contract).
        var sortedCore = coreIds.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.Equal(sortedCore, coreIds);
    }

    [Fact]
    public void List_IsSnapshot_NotLiveReference()
    {
        var reg = new DestructiveOperationKindsRegistry();
        var snapshot1 = reg.List();
        var snapshot1Count = snapshot1.Count;
        reg.Register("plugin_x:op", "X op", "plugin-x");
        var snapshot2 = reg.List();
        // First snapshot reflects the state at the time of the call —
        // late mutations don't appear in it. Test is additive-tolerant:
        // captures the initial count and asserts +1 after Register so
        // future seed changes don't break the test.
        Assert.Equal(snapshot1Count, snapshot1.Count);
        Assert.Equal(snapshot1Count + 1, snapshot2.Count);
    }
}
