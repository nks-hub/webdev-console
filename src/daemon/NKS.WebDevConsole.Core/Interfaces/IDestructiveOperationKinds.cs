namespace NKS.WebDevConsole.Core.Interfaces;

/// <summary>
/// Phase 7.4b — registry of destructive operation kinds plugins can mint
/// MCP intents for. Replaces the hardcoded
/// <c>deploy/rollback/cancel/restore</c> set with a runtime registry that
/// plugins contribute to during their <c>OnLoad</c> hook.
///
/// Each kind ships with:
/// <list type="bullet">
///   <item><b>id</b> — wire-format identifier matching the Phase 7.4a
///         schema regex <c>^[a-z][a-z0-9_:]{0,63}$</c>. Conventional namespace
///         prefix (e.g. <c>nksbackup:restore</c>) avoids collisions between
///         plugins. Stored in <c>deploy_intents.kind</c>; flows through SSE
///         events; never localised.</item>
///   <item><b>label</b> — short human-readable noun phrase shown in the
///         confirm banner ("Restore database backup", "Drop MySQL table").
///         Localisation belongs to the plugin; the daemon just passes the
///         string through. Distinct from id so wire compatibility survives
///         label tweaks.</item>
///   <item><b>danger</b> — operator-visible severity hint. The banner can
///         escalate visual treatment for <see cref="DangerLevel.Destructive"/>
///         (red border, mandatory typed-host confirmation) vs the default
///         <see cref="DangerLevel.Reversible"/> deploy flow.</item>
///   <item><b>plugin id</b> — which plugin owns the kind. Removing the
///         plugin removes its kinds (so a leftover intent for
///         <c>nksbackup:restore</c> after uninstall is treated as
///         <c>kind_unknown</c> at validate time).</item>
/// </list>
///
/// Cross-ALC safe — every method takes/returns primitives or this file's
/// own record types. The implementation lives in the daemon
/// (<c>DestructiveOperationKindsRegistry</c>), one singleton per host;
/// plugins call <c>Register</c> from their load hook and the daemon
/// snapshots <c>List</c> for the GUI's "what AI can do here" page.
/// </summary>
public interface IDestructiveOperationKinds
{
    /// <summary>
    /// Register a new kind. Throws <see cref="InvalidOperationException"/>
    /// if the id is already registered (defensive: two plugins fighting
    /// over the same id is a bug, not a no-op). Validates the id against
    /// the schema regex and refuses to register out-of-range ids.
    /// </summary>
    void Register(DestructiveOperationKind kind);

    /// <summary>
    /// Convenience overload for the common case (no danger override =
    /// reversible deploy-style op).
    /// </summary>
    void Register(string id, string label, string pluginId);

    /// <summary>
    /// Drop every kind owned by <paramref name="pluginId"/>. Called when
    /// the plugin is unloaded so its kinds disappear from the registry
    /// in lock-step. Idempotent — unknown plugin id is a no-op.
    /// </summary>
    void UnregisterPlugin(string pluginId);

    /// <summary>
    /// Look up a registered kind. Returns null when the id is not in the
    /// registry — callers (validator, banner) can decide whether to treat
    /// that as <c>kind_unknown</c> (strict) or fall through to the legacy
    /// hardcoded set (lenient, for migration period).
    /// </summary>
    DestructiveOperationKind? Get(string id);

    /// <summary>
    /// Enumerate every currently-registered kind. Used by the MCP tool
    /// schema endpoint ("what kinds can a client mint here?") and the
    /// admin GUI's discovery page. Snapshot semantics — the returned list
    /// is detached, safe to iterate while plugins (un)register concurrently.
    /// </summary>
    IReadOnlyList<DestructiveOperationKind> List();
}

/// <summary>
/// One kind registered by a plugin. Primitive-typed for cross-ALC safety.
/// </summary>
public sealed record DestructiveOperationKind(
    string Id,
    string Label,
    string PluginId,
    DangerLevel Danger = DangerLevel.Reversible);

/// <summary>
/// Operator-visible severity hint. The banner uses this to decide whether
/// the operation needs the standard 2-second-countdown confirm or the
/// stronger typed-host-name confirm. NOT used for authorisation — that's
/// what intent signing + grants are for.
/// </summary>
public enum DangerLevel
{
    /// <summary>Reversible (deploy with rollback). Default. 2s countdown banner.</summary>
    Reversible,
    /// <summary>Irreversible data change (drop table, delete site). Typed confirm.</summary>
    Destructive,
}
