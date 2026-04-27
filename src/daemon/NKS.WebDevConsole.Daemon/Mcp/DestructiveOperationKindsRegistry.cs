using System.Collections.Concurrent;
using NKS.WebDevConsole.Core.Interfaces;

namespace NKS.WebDevConsole.Daemon.Mcp;

/// <summary>
/// Phase 7.4b — in-memory <see cref="IDestructiveOperationKinds"/>.
/// Concurrent dict so plugin OnLoad hooks can register from background
/// threads without locking. Snapshot read for <see cref="List"/> avoids
/// returning a live reference to an IDictionary the caller could mutate.
///
/// The four legacy kinds (deploy/rollback/cancel/restore) are seeded at
/// construction so the existing intent flow keeps working even before any
/// plugin registers. They're owned by pseudo-plugin id "core" so
/// <see cref="UnregisterPlugin"/> can never accidentally drop them.
/// </summary>
public sealed class DestructiveOperationKindsRegistry : IDestructiveOperationKinds
{
    private static readonly System.Text.RegularExpressions.Regex IdPattern =
        new("^[a-z][a-z0-9_:]{0,63}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private const string CorePluginId = "core";

    private readonly ConcurrentDictionary<string, DestructiveOperationKind> _kinds = new();

    public DestructiveOperationKindsRegistry()
    {
        // Seed legacy hardcoded kinds — these power deploy / rollback /
        // cancel / restore from before plugin extraction. Owner is "core"
        // (the daemon itself), not a real plugin, so UnregisterPlugin
        // never touches them. Labels stay English here; the GUI banner
        // does its own localisation via i18n keys when it recognises
        // these well-known ids.
        _kinds["deploy"] = new DestructiveOperationKind(
            "deploy", "Deploy a new release", CorePluginId, DangerLevel.Reversible);
        _kinds["rollback"] = new DestructiveOperationKind(
            "rollback", "Roll back to a previous release", CorePluginId, DangerLevel.Reversible);
        _kinds["cancel"] = new DestructiveOperationKind(
            "cancel", "Cancel an in-flight deploy", CorePluginId, DangerLevel.Reversible);
        _kinds["restore"] = new DestructiveOperationKind(
            "restore", "Restore a database snapshot", CorePluginId, DangerLevel.Destructive);
        // Phase 7.5+++ — test_hook runs arbitrary operator-supplied
        // shell/http/php commands. Ungated, an AI could call this with
        // its own "hook" payload to execute anything on the host. Tagged
        // Destructive so strict-kinds + always-confirm-by-default treat
        // it like a real destructive op.
        _kinds["test_hook"] = new DestructiveOperationKind(
            "test_hook", "Run a deploy hook command for testing", CorePluginId, DangerLevel.Destructive);
        // Phase 7.5+++ — settings_write rewrites the per-site deploy config
        // file (hooks, localPaths, notifications, retention). An AI with
        // settings_write but without deploy could plant a malicious hook
        // and wait for the next operator-triggered deploy to execute it.
        // Reversible (file overwrite is undoable) but practically destructive.
        _kinds["settings_write"] = new DestructiveOperationKind(
            "settings_write", "Overwrite per-site deploy settings", CorePluginId, DangerLevel.Destructive);
        // Phase 7.5+++ — snapshot_create runs a real ZIP of the current
        // release dir and writes to disk. Reversible (operator can delete
        // the file), but an AI loop spamming this is a disk-fill DoS.
        _kinds["snapshot_create"] = new DestructiveOperationKind(
            "snapshot_create", "Create a manual snapshot of the current release", CorePluginId, DangerLevel.Reversible);
    }

    public void Register(DestructiveOperationKind kind)
    {
        if (kind is null) throw new ArgumentNullException(nameof(kind));
        if (string.IsNullOrEmpty(kind.Id) || !IdPattern.IsMatch(kind.Id))
            throw new ArgumentException(
                $"Kind id '{kind.Id}' does not match {IdPattern}", nameof(kind));
        if (string.IsNullOrEmpty(kind.PluginId))
            throw new ArgumentException("PluginId is required", nameof(kind));
        if (string.IsNullOrEmpty(kind.Label))
            throw new ArgumentException("Label is required", nameof(kind));

        if (!_kinds.TryAdd(kind.Id, kind))
        {
            throw new InvalidOperationException(
                $"Destructive op kind '{kind.Id}' is already registered (owned by " +
                $"plugin '{_kinds[kind.Id].PluginId}'). Two plugins fighting over " +
                $"the same id is a bug — pick a different namespace prefix.");
        }
    }

    public void Register(string id, string label, string pluginId) =>
        Register(new DestructiveOperationKind(id, label, pluginId));

    public void UnregisterPlugin(string pluginId)
    {
        if (string.IsNullOrEmpty(pluginId) || pluginId == CorePluginId) return;
        // ConcurrentDictionary doesn't have a "remove where" so we snapshot
        // the keys we want to drop, then TryRemove each. Concurrent
        // modifications to other keys are unaffected.
        var toDrop = _kinds
            .Where(kvp => kvp.Value.PluginId == pluginId)
            .Select(kvp => kvp.Key)
            .ToList();
        foreach (var key in toDrop)
        {
            _kinds.TryRemove(key, out _);
        }
    }

    public DestructiveOperationKind? Get(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        return _kinds.TryGetValue(id, out var k) ? k : null;
    }

    public IReadOnlyList<DestructiveOperationKind> List()
    {
        // Snapshot to detach from live state. Order by plugin then id so
        // the GUI's "what AI can do here" page renders deterministically.
        return _kinds.Values
            .OrderBy(k => k.PluginId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(k => k.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
