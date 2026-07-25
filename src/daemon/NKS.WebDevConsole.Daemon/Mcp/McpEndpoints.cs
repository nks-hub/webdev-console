using NKS.WebDevConsole.Daemon.Config;
using NKS.WebDevConsole.Daemon.Data;
using NKS.WebDevConsole.Daemon.Services;

namespace NKS.WebDevConsole.Daemon.Mcp;

/// <summary>
/// Route registrations for the /api/mcp/* surface (intents, kinds, grants,
/// tool-call audit log, suggested grants).
///
/// Lifted verbatim out of Program.cs, which had grown past 11k lines with all
/// 191 endpoints inline. The block was self-contained already: every handler
/// takes its collaborators through minimal-API DI parameters rather than
/// closing over Program.cs locals, so moving it needed no signature changes
/// and leaves route order — which minimal APIs are sensitive to — untouched.
/// </summary>
public static class McpEndpoints
{
    // Helper: read mcp.enabled from settings on every request. Cheap (in-memory
    // dictionary lookup); could be cached but settings rarely change so the
    // extra round-trip via SettingsStore is fine.
    static bool IsMcpEnabled(HttpContext ctx) =>
        ctx.RequestServices.GetRequiredService<SettingsStore>()
            .GetBool("mcp", "enabled", defaultValue: false);

    public static void MapMcpEndpoints(this WebApplication app)
    {
        app.MapPost("/api/mcp/intents", async (
            HttpContext ctx,
            NKS.WebDevConsole.Daemon.Data.Database db,
            NKS.WebDevConsole.Daemon.Mcp.IntentSigner signer,
            NKS.WebDevConsole.Core.Interfaces.IDeployEventBroadcaster eventsBus) =>
        {
            if (!IsMcpEnabled(ctx)) return Results.NotFound(new { error = "mcp_disabled" });
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body);
            var root = doc.RootElement;
            string? domain = root.TryGetProperty("domain", out var dEl) ? dEl.GetString() : null;
            string? host = root.TryGetProperty("host", out var hEl) ? hEl.GetString() : null;
            string? kind = root.TryGetProperty("kind", out var kEl) ? kEl.GetString() : null;
            string? releaseId = root.TryGetProperty("releaseId", out var rEl) ? rEl.GetString() : null;
            int expiresInSec = root.TryGetProperty("expiresIn", out var eEl) && eEl.TryGetInt32(out var ei) ? ei : 300;

            if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(host) ||
                string.IsNullOrWhiteSpace(kind))
            {
                return Results.BadRequest(new { error = "domain, host, kind are required" });
            }
            // Phase 7.4 — kind is now an open namespace so plugins can mint
            // intents for their own destructive ops (db:drop_table, site:delete,
            // plugin:reset, …) without requiring a daemon-side migration.
            // Charset/length rule mirrors the schema CHECK in migration 013:
            // 1-64 chars, must start with a letter, [a-z0-9_:] only. Colon
            // is the conventional namespace separator (e.g. "deploy:full").
            if (!System.Text.RegularExpressions.Regex.IsMatch(kind!, "^[a-z][a-z0-9_:]{0,63}$"))
            {
                return Results.BadRequest(new
                {
                    error = "kind_invalid",
                    detail = "kind must match ^[a-z][a-z0-9_:]{0,63}$ (lowercase letters/digits/_/:; max 64 chars)",
                });
            }
            // Clamp the expiry window. Long-lived signed intents defeat the point
            // of single-use tokens — 1h ceiling matches the MCP server's CCR
            // session length so a single AI turn always has a fresh signature.
            expiresInSec = Math.Clamp(expiresInSec, 30, 3600);

            var intentId = Guid.NewGuid().ToString("D");
            var nonce = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresInSec);
            var canonical = NKS.WebDevConsole.Daemon.Mcp.IntentSigner.Canonicalize(
                intentId, domain!, host!, kind!, nonce, expiresAt, releaseId);
            var signature = signer.Sign(canonical);

            using var conn = db.CreateConnection();
            await conn.OpenAsync();
            await Dapper.SqlMapper.ExecuteAsync(conn,
                "INSERT INTO deploy_intents (id, domain, host, release_id, kind, nonce, expires_at, hmac_signature) " +
                "VALUES (@Id, @Domain, @Host, @ReleaseId, @Kind, @Nonce, @ExpiresAt, @Signature)",
                new
                {
                    Id = intentId,
                    Domain = domain,
                    Host = host,
                    ReleaseId = releaseId,
                    Kind = kind,
                    Nonce = nonce,
                    ExpiresAt = expiresAt.ToString("o"),
                    Signature = signature,
                });

            // Phase 7.5+++ — broadcast intent lifecycle so the admin McpIntents
            // table refreshes without F5 when AI/CI mints a new token. Best-effort
            // (no subscribers = no-op); never block the response on SSE I/O.
            try
            {
                await eventsBus.BroadcastAsync("mcp:intent-changed",
                    new { change = "created", intentId, domain, host, kind });
            }
            catch { /* SSE failure is non-fatal */ }

            return Results.Ok(new
            {
                intentId,
                intentToken = $"{intentId}.{nonce}.{signature}",
                expiresAt = expiresAt.ToString("o"),
            });
        });

        app.MapPost("/api/mcp/intents/confirm-request", async (
            HttpContext ctx,
            NKS.WebDevConsole.Core.Interfaces.IDeployEventBroadcaster broadcaster,
            NKS.WebDevConsole.Core.Interfaces.IDestructiveOperationKinds kindsRegistry,
            NKS.WebDevConsole.Daemon.Data.Database db,
            SettingsStore settings) =>
        {
            if (!IsMcpEnabled(ctx)) return Results.NotFound(new { error = "mcp_disabled" });
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body);
            var root = doc.RootElement;
            string? intentId = root.TryGetProperty("intentId", out var iEl) ? iEl.GetString() : null;
            string? prompt = root.TryGetProperty("prompt", out var pEl) ? pEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(intentId))
            {
                return Results.BadRequest(new { error = "intentId is required" });
            }
            // Phase 6.14b — include expiresAt + kind so the GUI banner can show a
            // live countdown and surface what verb is about to fire. Best-effort
            // lookup; if the row vanished (intent never persisted, race with
            // sweeper), fall back to the minimal payload.
            string? expiresAt = null;
            string? kind = null;
            string? domain = null;
            string? host = null;
            try
            {
                using var conn = db.CreateConnection();
                await conn.OpenAsync();
                var meta = await Dapper.SqlMapper.QuerySingleOrDefaultAsync<dynamic>(conn,
                    "SELECT expires_at, kind, domain, host FROM deploy_intents WHERE id = @Id",
                    new { Id = intentId });
                if (meta is not null)
                {
                    expiresAt = (string?)meta.expires_at;
                    kind = (string?)meta.kind;
                    domain = (string?)meta.domain;
                    host = (string?)meta.host;
                }
            }
            catch { /* best-effort; banner still renders without metadata */ }

            // Phase 7.4c — enrich the SSE event with the human label + danger
            // level the kind was registered with. The banner surfaces these so
            // the operator sees "Restore database backup" instead of bare
            // "restore", and gets visual escalation (red border + typed-host
            // confirm) for kinds tagged Destructive. Falls back to the bare
            // kind id when the registry doesn't know it (post-uninstall race
            // or core-only bootstrap before any plugin contributed).
            string? kindLabel = null;
            string? kindDanger = null;
            string? kindPluginId = null;
            if (!string.IsNullOrEmpty(kind))
            {
                var registered = kindsRegistry.Get(kind);
                if (registered is not null)
                {
                    kindLabel = registered.Label;
                    kindDanger = registered.Danger.ToString().ToLowerInvariant();
                    kindPluginId = registered.PluginId;
                }
            }

            // Phase 7.5+++ — flag whether this kind is currently in
            // mcp.always_confirm_kinds so the banner can show distinct copy
            // ("operator marked this kind as always-confirm — your trust grants
            // were skipped"). Same parsing as the validator's lookup so the GUI
            // and runtime gate stay coherent.
            bool alwaysConfirm = false;
            if (!string.IsNullOrEmpty(kind))
            {
                var raw = settings.GetString("mcp", "always_confirm_kinds") ?? "";
                foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries
                                                  | StringSplitOptions.TrimEntries))
                {
                    if (string.Equals(part, kind, StringComparison.OrdinalIgnoreCase))
                    {
                        alwaysConfirm = true;
                        break;
                    }
                }
            }

            // Best-effort: the SSE bus is the GUI's notification channel. Failure
            // to broadcast (no subscribers, etc.) is not fatal — the AI can still
            // proceed with MCP_DEPLOY_AUTO_APPROVE=true to bypass GUI banner.
            await broadcaster.BroadcastAsync("mcp:confirm-request",
                new { intentId, prompt, expiresAt, kind, kindLabel, kindDanger, kindPluginId, domain, host, alwaysConfirm });
            return Results.Accepted();
        });

        // GUI calls this when the user clicks Approve on the banner that the
        // confirm-request SSE event raised. Single-stamp: only the first POST
        // flips `confirmed_at`; subsequent calls return 409 so a confused
        // double-click can't be mistaken for a fresh approval.
        app.MapPost("/api/mcp/intents/{intentId}/confirm", async (
            string intentId,
            HttpContext ctx,
            NKS.WebDevConsole.Daemon.Data.Database db,
            NKS.WebDevConsole.Core.Interfaces.IDeployEventBroadcaster eventsBus) =>
        {
            if (!IsMcpEnabled(ctx)) return Results.NotFound(new { error = "mcp_disabled" });
            if (string.IsNullOrWhiteSpace(intentId))
            {
                return Results.BadRequest(new { error = "intentId is required" });
            }
            using var conn = db.CreateConnection();
            await conn.OpenAsync();
            // Pre-check existence so we can distinguish 404 from 409 cleanly —
            // SQLite's UPDATE rowcount alone collapses both into 0.
            var exists = await Dapper.SqlMapper.QuerySingleOrDefaultAsync<int?>(conn,
                "SELECT 1 FROM deploy_intents WHERE id = @Id",
                new { Id = intentId });
            if (exists is null) return Results.NotFound(new { error = "intent_not_found", intentId });

            var now = DateTimeOffset.UtcNow.ToString("o");
            var rows = await Dapper.SqlMapper.ExecuteAsync(conn,
                "UPDATE deploy_intents SET confirmed_at = @Now WHERE id = @Id AND confirmed_at IS NULL",
                new { Id = intentId, Now = now });
            if (rows == 0)
            {
                return Results.Conflict(new { error = "already_confirmed", intentId });
            }
            try
            {
                await eventsBus.BroadcastAsync("mcp:intent-changed",
                    new { change = "confirmed", intentId, confirmedAt = now });
            }
            catch { /* SSE failure is non-fatal */ }
            return Results.Ok(new { intentId, confirmedAt = now });
        });

        // Phase 6.12b — operator-driven intent revoke. Marks used_at without
        // actually consuming, so a leaked or unwanted token can be neutered
        // before an AI client tries to fire it. Idempotent — second call
        // returns 409 already_used.
        app.MapPost("/api/mcp/intents/{intentId}/revoke", async (
            string intentId,
            HttpContext ctx,
            NKS.WebDevConsole.Daemon.Data.Database db,
            NKS.WebDevConsole.Core.Interfaces.IDeployEventBroadcaster eventsBus) =>
        {
            if (!IsMcpEnabled(ctx)) return Results.NotFound(new { error = "mcp_disabled" });
            if (string.IsNullOrWhiteSpace(intentId))
            {
                return Results.BadRequest(new { error = "intentId is required" });
            }
            using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var exists = await Dapper.SqlMapper.QuerySingleOrDefaultAsync<int?>(conn,
                "SELECT 1 FROM deploy_intents WHERE id = @Id",
                new { Id = intentId });
            if (exists is null) return Results.NotFound(new { error = "intent_not_found", intentId });

            var now = DateTimeOffset.UtcNow.ToString("o");
            var rows = await Dapper.SqlMapper.ExecuteAsync(conn,
                "UPDATE deploy_intents SET used_at = @Now WHERE id = @Id AND used_at IS NULL",
                new { Id = intentId, Now = now });
            if (rows == 0)
            {
                return Results.Conflict(new { error = "already_used", intentId });
            }
            try
            {
                await eventsBus.BroadcastAsync("mcp:intent-changed",
                    new { change = "revoked", intentId, revokedAt = now });
            }
            catch { /* SSE failure is non-fatal */ }
            return Results.Ok(new { intentId, revokedAt = now });
        });

        // Phase 6.11b — admin inventory of all signed intents. Read-only, no
        // destructive side effects — hands back the full deploy_intents row
        // list (newest first) so a wdc operator can audit what AI/CI clients
        // have minted recently. Bearer-auth on /api/* is sufficient gate.
        app.MapGet("/api/mcp/intents", async (
            HttpContext ctx,
            NKS.WebDevConsole.Daemon.Data.Database db,
            NKS.WebDevConsole.Core.Interfaces.IDestructiveOperationKinds kindsRegistry,
            int limit = 100,
            string? matchedGrantId = null) =>
        {
            if (!IsMcpEnabled(ctx)) return Results.NotFound(new { error = "mcp_disabled" });
            using var conn = db.CreateConnection();
            await conn.OpenAsync();
            // Phase 7.5+++ — optional matchedGrantId filter for "show all
            // intents this grant approved" drilldown. Server-side WHERE clause
            // is faster than fetching the full inventory and filtering client-
            // side, and lets the limit param scope the response correctly.
            var sql = "SELECT id, domain, host, release_id, kind, expires_at, used_at, " +
                      "confirmed_at, created_at, matched_grant_id " +
                      "FROM deploy_intents " +
                      (string.IsNullOrWhiteSpace(matchedGrantId)
                          ? ""
                          : "WHERE matched_grant_id = @MatchedGrantId ") +
                      "ORDER BY created_at DESC LIMIT @Limit";
            var rows = await Dapper.SqlMapper.QueryAsync<dynamic>(conn, sql,
                new { Limit = Math.Clamp(limit, 1, 500), MatchedGrantId = matchedGrantId });
            // Phase 7.5+ — enrich the inventory with the registry-resolved label
            // + danger so the McpIntents page can render "Restore database
            // snapshot (destructive)" instead of bare "restore". Lookup is
            // O(1) per row against the in-memory registry.
            var entries = rows.Select(r =>
            {
                var kindId = (string)r.kind;
                var registered = kindsRegistry.Get(kindId);
                return new
                {
                    intentId = (string)r.id,
                    domain = (string)r.domain,
                    host = (string)r.host,
                    releaseId = (string?)r.release_id,
                    kind = kindId,
                    kindLabel = registered?.Label,
                    kindDanger = registered?.Danger.ToString().ToLowerInvariant(),
                    kindPluginId = registered?.PluginId,
                    expiresAt = (string)r.expires_at,
                    usedAt = (string?)r.used_at,
                    confirmedAt = (string?)r.confirmed_at,
                    createdAt = (string)r.created_at,
                    // Phase 7.5+++ — audit trail: which grant auto-confirmed
                    // this intent (NULL = manually confirmed via banner OR
                    // allowUnconfirmed CI path).
                    matchedGrantId = (string?)r.matched_grant_id,
                    // Derived state for UI rendering convenience.
                    state = ComputeIntentState(
                        (string?)r.used_at,
                        (string?)r.confirmed_at,
                        (string)r.expires_at),
                };
            }).ToList();
            return Results.Ok(new { count = entries.Count, entries });
        });

        static string ComputeIntentState(string? usedAt, string? confirmedAt, string expiresAtRaw)
        {
            if (!string.IsNullOrEmpty(usedAt)) return "consumed";
            if (DateTimeOffset.TryParse(expiresAtRaw, out var exp) && exp < DateTimeOffset.UtcNow)
                return "expired";
            if (string.IsNullOrEmpty(confirmedAt)) return "pending_confirmation";
            return "ready";
        }

        // Phase 7.4b — discover destructive op kinds plugins have registered.
        // MCP clients call this to know what kinds they can include in
        // /api/mcp/intents requests; the GUI shows it on the MCP Hub page so
        // operators see "what AI can do here". Read-only; bearer auth on /api/*
        // is sufficient gate.
        app.MapGet("/api/mcp/kinds", async (
            HttpContext ctx,
            NKS.WebDevConsole.Core.Interfaces.IDestructiveOperationKinds kinds,
            NKS.WebDevConsole.Daemon.Data.Database db,
            SettingsStore settings,
            CancellationToken ct) =>
        {
            if (!IsMcpEnabled(ctx)) return Results.NotFound(new { error = "mcp_disabled" });
            // Phase 7.5+++ — surface mcp.always_confirm_kinds membership per row.
            // Same parsing rules as the validator's lookup so the GUI flag and
            // the runtime gate can never disagree.
            var alwaysConfirmRaw = settings.GetString("mcp", "always_confirm_kinds") ?? "";
            var alwaysConfirmSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in alwaysConfirmRaw.Split(',', StringSplitOptions.RemoveEmptyEntries
                                                           | StringSplitOptions.TrimEntries))
            {
                if (part.Length > 0) alwaysConfirmSet.Add(part);
            }
            // Phase 7.5+++ — usage telemetry per kind. Single GROUP BY query
            // tells operators which destructive ops AI is actually exercising
            // (deploy: 47, restore: 3, rollback: 0). Tolerates missing table
            // for fresh-DB compat.
            var usageByKind = new Dictionary<string, int>(StringComparer.Ordinal);
            try
            {
                using var conn = db.CreateConnection();
                await conn.OpenAsync(ct);
                var rows = await Dapper.SqlMapper.QueryAsync<(string Kind, int Count)>(conn,
                    "SELECT kind AS Kind, COUNT(*) AS Count FROM deploy_intents GROUP BY kind");
                foreach (var (kind, count) in rows)
                {
                    usageByKind[kind] = count;
                }
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 1)
            {
                // Table doesn't exist (fresh DB before migration 006). All counts 0.
            }

            var list = kinds.List().Select(k => new
            {
                id = k.Id,
                label = k.Label,
                pluginId = k.PluginId,
                danger = k.Danger.ToString().ToLowerInvariant(),
                // Phase 7.5+++ — lifetime intent count for this kind. Includes
                // consumed + revoked + expired + still-pending; operators care
                // about historical use, not just live state.
                intentCount = usageByKind.TryGetValue(k.Id, out var c) ? c : 0,
                // Phase 7.5+++ — true when operator has marked this kind as
                // always-confirm via Settings → grants are bypassed for it.
                alwaysConfirm = alwaysConfirmSet.Contains(k.Id),
            }).ToList();
            return Results.Ok(new { count = list.Count, entries = list });
        });

        // ============================================================================
        // Phase 7.3 — MCP grants CRUD. The grants table powers persistent trust:
        // "approve THIS session for 30 min", "always trust THIS API key", or coarse
        // "always trust any AI on THIS daemon". Endpoints are gated by mcp.enabled
        // (same as /api/mcp/intents) and the standard bearer auth on /api/*.
        // ============================================================================

        // List active grants — newest first. Used by GUI grants page + tests.
        app.MapGet("/api/mcp/grants", async (
            HttpContext ctx,
            NKS.WebDevConsole.Core.Interfaces.IMcpSessionGrantsRepository grants,
            CancellationToken ct,
            bool? includeRevoked = null,
            int? page = null,
            int? pageSize = null) =>
        {
            if (!IsMcpEnabled(ctx)) return Results.NotFound(new { error = "mcp_disabled" });
            // Phase 7.5+++ — opt-in audit view. Nullable + default null so the
            // minimal-API binder treats the param as truly optional (a non-
            // nullable `bool` would 400 when the query string is empty).
            var rows = includeRevoked == true
                ? await grants.ListAllAsync(ct)
                : await grants.ListActiveAsync(ct);
            var total = rows.Count;
            // Phase 7.5+++ — pagination on top of the in-memory list. Defaults
            // (page=1, pageSize=50) keep BC for callers that don't pass params.
            // Page 0 / negative is treated as 1; pageSize clamped to [1, 500]
            // to bound payload size.
            var p = Math.Max(1, page ?? 1);
            var ps = Math.Clamp(pageSize ?? 50, 1, 500);
            var skip = (p - 1) * ps;
            var paged = skip >= total
                ? new List<NKS.WebDevConsole.Core.Interfaces.McpSessionGrantRow>()
                : rows.Skip(skip).Take(ps).ToList();
            return Results.Ok(new
            {
                count = paged.Count,
                total,
                page = p,
                pageSize = ps,
                totalPages = (total + ps - 1) / ps,
                entries = paged,
            });
        });

        // Create a grant. Body shape:
        // {
        //   "scopeType":   "session" | "instance" | "api_key" | "always",
        //   "scopeValue":  "<id>" | null (must be null when scopeType='always'),
        //   "kindPattern": "*" or "deploy" | "rollback" | "cancel" | "restore",
        //   "targetPattern":"*" or specific target (e.g. domain),
        //   "expiresAt":   ISO-8601 UTC or null (null = permanent),
        //   "note":        free-form, optional
        // }
        app.MapPost("/api/mcp/grants", async (
            HttpContext ctx,
            NKS.WebDevConsole.Core.Interfaces.IMcpSessionGrantsRepository grants,
            NKS.WebDevConsole.Core.Interfaces.IDeployEventBroadcaster eventsBus,
            CancellationToken ct) =>
        {
            if (!IsMcpEnabled(ctx)) return Results.NotFound(new { error = "mcp_disabled" });

            GrantCreateBody? body;
            try { body = await ctx.Request.ReadFromJsonAsync<GrantCreateBody>(ct); }
            catch { return Results.BadRequest(new { error = "invalid_json" }); }
            if (body is null) return Results.BadRequest(new { error = "missing_body" });

            var allowedScopes = new[] { "session", "instance", "api_key", "always" };
            if (string.IsNullOrEmpty(body.ScopeType) || !allowedScopes.Contains(body.ScopeType))
                return Results.BadRequest(new { error = "invalid_scope_type", allowed = allowedScopes });
            if (body.ScopeType == "always")
            {
                if (!string.IsNullOrEmpty(body.ScopeValue))
                    return Results.BadRequest(new { error = "scope_value_must_be_null_for_always" });
            }
            else if (string.IsNullOrEmpty(body.ScopeValue))
            {
                return Results.BadRequest(new { error = "scope_value_required" });
            }

            var row = new NKS.WebDevConsole.Core.Interfaces.McpSessionGrantRow(
                Id: null,
                ScopeType: body.ScopeType,
                ScopeValue: body.ScopeType == "always" ? null : body.ScopeValue,
                KindPattern: string.IsNullOrEmpty(body.KindPattern) ? "*" : body.KindPattern,
                TargetPattern: string.IsNullOrEmpty(body.TargetPattern) ? "*" : body.TargetPattern,
                GrantedAt: "",
                ExpiresAt: body.ExpiresAt,
                GrantedBy: string.IsNullOrEmpty(body.GrantedBy) ? "gui" : body.GrantedBy,
                RevokedAt: null,
                Note: body.Note,
                // Phase 7.5+++ — optional rate limit. Math.Max in repo clamps negatives.
                MinCooldownSeconds: body.MinCooldownSeconds ?? 0);

            var id = await grants.InsertAsync(row, ct);
            // Phase 7.5+++ — broadcast lifecycle event so any open McpHub Grants
            // tab refreshes its list without operator F5. Best-effort; failure
            // doesn't roll back the grant.
            try
            {
                await eventsBus.BroadcastAsync("mcp:grant-changed", new
                {
                    change = "created",
                    id,
                    scopeType = body.ScopeType,
                    kindPattern = row.KindPattern,
                    targetPattern = row.TargetPattern,
                });
            }
            catch { /* SSE best-effort */ }
            return Results.Ok(new { id, status = "created" });
        });

        // Revoke (soft-delete) a grant by id.
        app.MapDelete("/api/mcp/grants/{id}", async (
            string id,
            HttpContext ctx,
            NKS.WebDevConsole.Core.Interfaces.IMcpSessionGrantsRepository grants,
            NKS.WebDevConsole.Core.Interfaces.IDeployEventBroadcaster eventsBus,
            CancellationToken ct) =>
        {
            if (!IsMcpEnabled(ctx)) return Results.NotFound(new { error = "mcp_disabled" });
            var ok = await grants.RevokeAsync(id, ct);
            if (!ok)
            {
                return Results.NotFound(new { error = "grant_not_found_or_already_revoked", id });
            }
            try { await eventsBus.BroadcastAsync("mcp:grant-changed", new { change = "revoked", id }); }
            catch { /* SSE best-effort */ }
            return Results.Ok(new { id, status = "revoked" });
        });

        // Phase 7.5+++ — aggregate grant statistics. Single round-trip that
        // the McpHub uses to render rich badges + the Settings page can show
        // as a snapshot card. Server-side aggregation keeps the GUI fast even
        // when the grants table grows beyond the 200-row default page size.
        //
        // Returned shape:
        //   {
        //     "total": int,            // all rows (active + revoked, not swept)
        //     "active": int,           // revoked_at IS NULL AND not yet expired
        //     "deadweight": int,       // active AND match_count=0 AND age >7d
        //     "totalMatches": long,    // sum(match_count) across all rows
        //     "lastMatchAt": ISO?      // max(last_matched_at), null if never
        //   }
        app.MapGet("/api/mcp/grants/stats", async (
            HttpContext ctx,
            NKS.WebDevConsole.Daemon.Data.Database db,
            CancellationToken ct) =>
        {
            if (!IsMcpEnabled(ctx)) return Results.NotFound(new { error = "mcp_disabled" });
            using var conn = db.CreateConnection();
            await conn.OpenAsync(ct);
            try
            {
                var deadweightCutoff = DateTimeOffset.UtcNow.AddDays(-7).ToString("o");
                var stats = await Dapper.SqlMapper.QuerySingleAsync<GrantStatsRow>(conn,
                    "SELECT " +
                    "  COUNT(*) AS Total, " +
                    "  SUM(CASE WHEN revoked_at IS NULL AND " +
                    "           (expires_at IS NULL OR expires_at > strftime('%Y-%m-%dT%H:%M:%fZ','now')) " +
                    "           THEN 1 ELSE 0 END) AS Active, " +
                    "  SUM(CASE WHEN revoked_at IS NULL AND " +
                    "           (expires_at IS NULL OR expires_at > strftime('%Y-%m-%dT%H:%M:%fZ','now')) AND " +
                    "           match_count = 0 AND granted_at < @Cutoff " +
                    "           THEN 1 ELSE 0 END) AS Deadweight, " +
                    "  COALESCE(SUM(match_count), 0) AS TotalMatches, " +
                    "  MAX(last_matched_at) AS LastMatchAt " +
                    "FROM mcp_session_grants",
                    new { Cutoff = deadweightCutoff });
                return Results.Ok(new
                {
                    total = stats.Total,
                    active = stats.Active,
                    deadweight = stats.Deadweight,
                    totalMatches = stats.TotalMatches,
                    lastMatchAt = stats.LastMatchAt,
                });
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 1)
            {
                // Table doesn't exist (fresh DB before migration 012/014). Return
                // zeros so the GUI renders gracefully rather than choking on 500.
                return Results.Ok(new
                {
                    total = 0, active = 0, deadweight = 0,
                    totalMatches = 0L, lastMatchAt = (string?)null,
                });
            }
        });

        // Phase 7.5+++ — dry-run grant match. Operator can ask "would a caller
        // with this identity tuple firing this kind+target match an existing
        // active grant?" WITHOUT actually creating an intent or auto-firing
        // anything. Mirrors the validator's pre-check semantics 1:1 by going
        // through the same `FindMatchingActiveAsync` path.
        //
        // Body: { sessionId?: string, instanceId?: string, apiKeyId?: string,
        //         kind: string, target: string }
        // Returns: { matched: bool, grant?: { id, scopeType, scopeValue,
        //            kindPattern, targetPattern, matchCount, lastMatchedAt } }
        //
        // Use cases: debugging "why isn't my grant matching?" without firing
        // destructive ops; pre-flight checks from the MCP CLI; admin auditing.
        app.MapPost("/api/mcp/grants/test-match", async (
            HttpContext ctx,
            NKS.WebDevConsole.Core.Interfaces.IMcpSessionGrantsRepository grants,
            CancellationToken ct) =>
        {
            if (!IsMcpEnabled(ctx)) return Results.NotFound(new { error = "mcp_disabled" });
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
            var root = doc.RootElement;
            var sessionId  = root.TryGetProperty("sessionId",  out var sEl) ? sEl.GetString() : null;
            var instanceId = root.TryGetProperty("instanceId", out var iEl) ? iEl.GetString() : null;
            var apiKeyId   = root.TryGetProperty("apiKeyId",   out var aEl) ? aEl.GetString() : null;
            var kind       = root.TryGetProperty("kind",       out var kEl) ? kEl.GetString() : null;
            var target     = root.TryGetProperty("target",     out var tEl) ? tEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(target))
                return Results.BadRequest(new { error = "kind and target are required" });

            var grant = await grants.FindMatchingActiveAsync(
                sessionId, instanceId, apiKeyId, kind!, target!, ct);
            if (grant is null)
            {
                return Results.Ok(new { matched = false });
            }
            // NOTE: this is a dry-run — do NOT call RecordMatchAsync. The
            // telemetry counters reflect REAL auto-confirms, not test queries.
            return Results.Ok(new
            {
                matched = true,
                grant = new
                {
                    id            = grant.Id,
                    scopeType     = grant.ScopeType,
                    scopeValue    = grant.ScopeValue,
                    kindPattern   = grant.KindPattern,
                    targetPattern = grant.TargetPattern,
                    matchCount    = grant.MatchCount,
                    lastMatchedAt = grant.LastMatchedAt,
                },
            });
        });

        // Phase 7.5+++ — manual sweep trigger. Operator can fire the grant
        // janitor on demand without waiting for the 15-minute background tick.
        // Reuses the same SQL helper the BackgroundService uses; broadcasts
        // mcp:grant-changed{change:swept} on success so the GUI table updates.
        app.MapPost("/api/mcp/grants/sweep-now", async (
            HttpContext ctx,
            NKS.WebDevConsole.Daemon.Data.Database db,
            NKS.WebDevConsole.Core.Interfaces.IDeployEventBroadcaster eventsBus,
            SettingsStore settings,
            CancellationToken ct) =>
        {
            if (!IsMcpEnabled(ctx)) return Results.NotFound(new { error = "mcp_disabled" });
            using var conn = db.CreateConnection();
            await conn.OpenAsync(ct);
            // Read the same operator-tunable retention the background janitor uses
            // so the manual button matches the timer's behaviour exactly.
            var expiredDays = Math.Max(0, settings.GetInt(
                "mcp", "grant_expired_retention_days",
                NKS.WebDevConsole.Daemon.Mcp.GrantSweeperService.DefaultExpiredRetentionDays));
            var revokedDays = Math.Max(0, settings.GetInt(
                "mcp", "grant_revoked_retention_days",
                NKS.WebDevConsole.Daemon.Mcp.GrantSweeperService.DefaultRevokedRetentionDays));
            var deleted = await NKS.WebDevConsole.Daemon.Mcp.GrantSweeperService.SweepAsync(
                conn, DateTimeOffset.UtcNow,
                TimeSpan.FromDays(expiredDays), TimeSpan.FromDays(revokedDays), ct);
            if (deleted > 0)
            {
                try
                {
                    await eventsBus.BroadcastAsync("mcp:grant-changed",
                        new { change = "swept", count = deleted });
                }
                catch { /* SSE best-effort */ }
            }
            return Results.Ok(new { deleted });
        });

        // Phase 7.5+++ — partial update of an existing grant. Only mutable
        // operator-tunable fields (cooldown, expiresAt, note) — identity and
        // telemetry are immutable. Body shape:
        //   { "minCooldownSeconds": 60?,
        //     "expiresAt": "2026-05-01T00:00:00Z" | null,  ← null = make permanent
        //     "note": "updated reason" }
        // Any field omitted = leave unchanged. Returns 200 with id, 404 if not
        // found, 400 if body has nothing to change.
        app.MapPatch("/api/mcp/grants/{id}", async (
            string id,
            HttpContext ctx,
            NKS.WebDevConsole.Core.Interfaces.IMcpSessionGrantsRepository grants,
            NKS.WebDevConsole.Core.Interfaces.IDeployEventBroadcaster eventsBus,
            CancellationToken ct) =>
        {
            if (!IsMcpEnabled(ctx)) return Results.NotFound(new { error = "mcp_disabled" });
            System.Text.Json.JsonDocument doc;
            try { doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct); }
            catch { return Results.BadRequest(new { error = "Invalid JSON body" }); }
            using var _ = doc;
            var root = doc.RootElement;

            int? cooldown = null;
            string? expiresAt = null;
            string? note = null;
            if (root.TryGetProperty("minCooldownSeconds", out var cdEl) && cdEl.ValueKind == System.Text.Json.JsonValueKind.Number)
                cooldown = cdEl.GetInt32();
            if (root.TryGetProperty("expiresAt", out var exEl))
            {
                // Distinguish "absent" vs "null" vs "string". Null in JSON → set
                // permanent (sentinel "__null__"); string → use as-is.
                if (exEl.ValueKind == System.Text.Json.JsonValueKind.Null) expiresAt = "__null__";
                else if (exEl.ValueKind == System.Text.Json.JsonValueKind.String) expiresAt = exEl.GetString();
            }
            if (root.TryGetProperty("note", out var noteEl) && noteEl.ValueKind == System.Text.Json.JsonValueKind.String)
                note = noteEl.GetString();

            if (cooldown is null && expiresAt is null && note is null)
                return Results.BadRequest(new { error = "no_mutable_fields", hint = "send minCooldownSeconds, expiresAt, or note" });

            var ok = await grants.UpdateMutableAsync(id, cooldown, expiresAt, note, ct);
            if (!ok) return Results.NotFound(new { error = "grant_not_found", id });

            try
            {
                await eventsBus.BroadcastAsync("mcp:grant-changed", new { change = "updated", id });
            }
            catch { /* SSE best-effort */ }
            return Results.Ok(new { id, status = "updated" });
        });

        // Phase 7.5+++ — bulk import grants from a previously-exported envelope.
        // Payload shape (matches the GUI export):
        //   { "formatVersion": 1, "entries": [ { id?, scopeType, scopeValue?,
        //     kindPattern, targetPattern, expiresAt?, grantedBy?, note? }, … ] }
        //
        // Strategy: skip rows whose `id` already exists (idempotent re-import
        // of the same backup). Rows without an id get a fresh UUID. Validation
        // is delegated to the existing INSERT path's CHECK constraints — bad
        // scope_type values blow up per-row and land in the errors[] array
        // without aborting the whole batch.
        //
        // Returns: { imported, skipped, errors: [{index, error}] }
        app.MapPost("/api/mcp/grants/import", async (
            HttpContext ctx,
            NKS.WebDevConsole.Core.Interfaces.IMcpSessionGrantsRepository grants,
            NKS.WebDevConsole.Core.Interfaces.IDeployEventBroadcaster eventsBus,
            NKS.WebDevConsole.Daemon.Data.Database db,
            CancellationToken ct) =>
        {
            if (!IsMcpEnabled(ctx)) return Results.NotFound(new { error = "mcp_disabled" });
            System.Text.Json.JsonDocument doc;
            try { doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct); }
            catch { return Results.BadRequest(new { error = "Invalid JSON body" }); }
            using var _ = doc;
            var root = doc.RootElement;
            if (!root.TryGetProperty("formatVersion", out var fv) || fv.ValueKind != System.Text.Json.JsonValueKind.Number || fv.GetInt32() != 1)
                return Results.BadRequest(new { error = "formatVersion must be 1" });
            if (!root.TryGetProperty("entries", out var entries) || entries.ValueKind != System.Text.Json.JsonValueKind.Array)
                return Results.BadRequest(new { error = "entries must be an array" });

            // Pre-load existing ids in one shot so dup detection is O(1).
            using var conn = db.CreateConnection();
            await conn.OpenAsync(ct);
            var existing = (await Dapper.SqlMapper.QueryAsync<string>(conn,
                "SELECT id FROM mcp_session_grants")).ToHashSet(StringComparer.Ordinal);

            int imported = 0, skipped = 0;
            var errors = new List<object>();
            int idx = -1;
            foreach (var e in entries.EnumerateArray())
            {
                idx++;
                try
                {
                    var id = e.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                    if (!string.IsNullOrEmpty(id) && existing.Contains(id))
                    {
                        skipped++;
                        continue;
                    }
                    var row = new NKS.WebDevConsole.Core.Interfaces.McpSessionGrantRow(
                        Id: id,
                        ScopeType: e.GetProperty("scopeType").GetString() ?? "session",
                        ScopeValue: e.TryGetProperty("scopeValue", out var sv) ? sv.GetString() : null,
                        KindPattern: e.TryGetProperty("kindPattern", out var kp) ? (kp.GetString() ?? "*") : "*",
                        TargetPattern: e.TryGetProperty("targetPattern", out var tp) ? (tp.GetString() ?? "*") : "*",
                        GrantedAt: e.TryGetProperty("grantedAt", out var ga) ? (ga.GetString() ?? "") : "",
                        ExpiresAt: e.TryGetProperty("expiresAt", out var ea) ? ea.GetString() : null,
                        GrantedBy: e.TryGetProperty("grantedBy", out var gb) ? (gb.GetString() ?? "import") : "import",
                        RevokedAt: null, // imported grants always start active; ignore source revoked_at
                        Note: e.TryGetProperty("note", out var note) ? note.GetString() : null);
                    await grants.InsertAsync(row, ct);
                    imported++;
                }
                catch (Exception ex)
                {
                    errors.Add(new { index = idx, error = ex.Message });
                }
            }

            if (imported > 0)
            {
                try
                {
                    await eventsBus.BroadcastAsync("mcp:grant-changed",
                        new { change = "imported", count = imported });
                }
                catch { /* SSE best-effort */ }
            }

            return Results.Ok(new { imported, skipped, errors });
        });

        // ============================================================================
        // Phase 8 — MCP tool call audit log (every call, read + write).
        //
        // Endpoints:
        //   POST /api/mcp/tool-calls         — append a row (called by MCP server)
        //   GET  /api/mcp/tool-calls         — paginated read with filters
        //   GET  /api/mcp/tool-calls/stats   — aggregate counts for the Activity header
        //
        // All three share the same Bearer token used by every other /api/mcp/*
        // route. The POST is fire-and-forget from the MCP server's side —
        // failures are logged but never bubble up to the caller.
        // ============================================================================

        app.MapPost("/api/mcp/tool-calls", async (
            HttpContext httpContext,
            NKS.WebDevConsole.Daemon.Mcp.McpToolCallsRepository repo,
            NKS.WebDevConsole.Core.Interfaces.IDeployEventBroadcaster eventsBus,
            CancellationToken ct) =>
        {
            System.Text.Json.JsonDocument doc;
            try
            {
                doc = await System.Text.Json.JsonDocument.ParseAsync(httpContext.Request.Body, cancellationToken: ct);
            }
            catch (System.Text.Json.JsonException ex)
            {
                // Don't 500 on a malformed payload — return 400 so the MCP server
                // (and any other caller) can distinguish "your payload is wrong"
                // from "the daemon is down".
                return Results.BadRequest(new { error = "invalid_json", detail = ex.Message });
            }
            using var _ = doc;
            var root = doc.RootElement;
            string? GetStr(string n) => root.TryGetProperty(n, out var p) && p.ValueKind == System.Text.Json.JsonValueKind.String ? p.GetString() : null;
            int GetInt(string n) => root.TryGetProperty(n, out var p) && p.ValueKind == System.Text.Json.JsonValueKind.Number ? p.GetInt32() : 0;

            // Defensive cap — a malicious or buggy caller could otherwise stuff
            // multi-MB payloads into args_summary and bloat the SQLite file.
            // The MCP server already pre-truncates to 500 chars, so anything
            // bigger here is a contract violation; we hard-cap rather than
            // reject so legitimate audit data survives the request.
            static string? Cap(string? s, int maxLen) =>
                s is null ? null : (s.Length <= maxLen ? s : s.Substring(0, maxLen));

            var toolName = GetStr("toolName");
            if (string.IsNullOrWhiteSpace(toolName))
                return Results.BadRequest(new { error = "toolName is required" });
            if (toolName!.Length > 200)
                return Results.BadRequest(new { error = "toolName too long (max 200)" });

            var row = new NKS.WebDevConsole.Daemon.Mcp.McpToolCallRow
            {
                SessionId = Cap(GetStr("sessionId"), 100),
                Caller = Cap(GetStr("caller"), 100) ?? "unknown",
                ToolName = toolName!,
                ArgsSummary = Cap(GetStr("argsSummary"), 1000),
                ArgsHash = Cap(GetStr("argsHash"), 64),
                DurationMs = GetInt("durationMs"),
                ResultCode = Cap(GetStr("resultCode"), 50) ?? "ok",
                ErrorMessage = Cap(GetStr("errorMessage"), 1000),
                DangerLevel = Cap(GetStr("dangerLevel"), 20) ?? "read",
                IntentId = Cap(GetStr("intentId"), 100),
            };
            var id = await repo.InsertAsync(row, ct);

            // Phase 8 — broadcast SSE so the Activity feed updates in real time
            // (replaces the 30s polling fallback). Best-effort: a broadcast
            // failure must NOT fail the audit insert.
            try
            {
                await eventsBus.BroadcastAsync("mcp:tool-call", new
                {
                    id,
                    toolName = row.ToolName,
                    sessionId = row.SessionId,
                    caller = row.Caller,
                    dangerLevel = row.DangerLevel,
                    resultCode = row.ResultCode,
                    durationMs = row.DurationMs,
                });
            }
            catch { /* SSE best-effort */ }

            return Results.Ok(new { id });
        });

        app.MapGet("/api/mcp/tool-calls", async (
            NKS.WebDevConsole.Daemon.Mcp.McpToolCallsRepository repo,
            int? limit,
            int? offset,
            string? dangerLevel,
            string? toolName,
            string? sessionId,
            string? q,
            CancellationToken ct) =>
        {
            var l = limit ?? 50;
            var o = offset ?? 0;
            var entries = await repo.ListAsync(l, o, dangerLevel, toolName, sessionId, ct, q);
            var total = await repo.CountAsync(dangerLevel, toolName, sessionId, ct, q);
            return Results.Ok(new { entries, total, limit = l, offset = o });
        });

        app.MapGet("/api/mcp/tool-calls/stats", async (
            NKS.WebDevConsole.Daemon.Mcp.McpToolCallsRepository repo,
            int? withinMinutes,
            CancellationToken ct) =>
        {
            var stats = await repo.GetStatsAsync(withinMinutes ?? 1440 /* 24h */, ct);
            return Results.Ok(stats);
        });

        app.MapGet("/api/mcp/tool-calls/timeline", async (
            NKS.WebDevConsole.Daemon.Mcp.McpToolCallsRepository repo,
            int? withinHours,
            CancellationToken ct) =>
        {
            var buckets = await repo.GetTimelineAsync(withinHours ?? 24, ct);
            return Results.Ok(new { withinHours = withinHours ?? 24, buckets });
        });

        app.MapGet("/api/mcp/tool-calls/by-tool", async (
            NKS.WebDevConsole.Daemon.Mcp.McpToolCallsRepository repo,
            int? withinHours,
            int? limit,
            CancellationToken ct) =>
        {
            var rows = await repo.GetByToolAsync(withinHours ?? 24, limit ?? 10, ct);
            return Results.Ok(new { withinHours = withinHours ?? 24, limit = limit ?? 10, rows });
        });

        // CSV export — RFC 4180 with comma delimiter. Streams the FULL audit
        // trail respecting current filters; capped at 10k rows so a runaway
        // AI session can't OOM the daemon. Operator gets Content-Disposition
        // so the browser saves it as `mcp-audit-{date}.csv`.
        app.MapGet("/api/mcp/tool-calls/export.csv", async (
            HttpContext httpCtx,
            NKS.WebDevConsole.Daemon.Mcp.McpToolCallsRepository repo,
            string? dangerLevel,
            string? toolName,
            string? sessionId,
            CancellationToken ct) =>
        {
            var entries = await repo.ListAsync(10_000, 0, dangerLevel, toolName, sessionId, ct);
            httpCtx.Response.ContentType = "text/csv; charset=utf-8";
            httpCtx.Response.Headers.Append("Content-Disposition",
                $"attachment; filename=\"mcp-audit-{DateTime.UtcNow:yyyy-MM-dd}.csv\"");
            static string Esc(string? s)
            {
                if (string.IsNullOrEmpty(s)) return "";
                var needsQuote = s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r');
                if (!needsQuote) return s;
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            }
            await using var writer = new StreamWriter(httpCtx.Response.Body);
            await writer.WriteLineAsync("called_at,session_id,caller,tool_name,danger_level,duration_ms,result_code,error_message,intent_id,args_summary");
            foreach (var e in entries)
            {
                await writer.WriteLineAsync(string.Join(',',
                    Esc(e.CalledAt), Esc(e.SessionId), Esc(e.Caller),
                    Esc(e.ToolName), Esc(e.DangerLevel),
                    e.DurationMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Esc(e.ResultCode), Esc(e.ErrorMessage), Esc(e.IntentId),
                    Esc(e.ArgsSummary)));
            }
            await writer.FlushAsync(ct);
        });

        // ============================================================================
        // Phase 8 — Suggested grants. Looks at the last 7 days of deploy_intents
        // where the operator manually approved (confirmed_at IS NOT NULL) but no
        // existing grant matched — i.e. the operator is doing the same approval
        // repeatedly. Surfaces (kind, domain, host) tuples with count >= 3 as
        // candidates for an auto-approve rule, reducing decision fatigue.
        // ============================================================================
        app.MapGet("/api/mcp/grants/suggested", async (
            HttpContext ctx,
            NKS.WebDevConsole.Daemon.Data.Database db,
            NKS.WebDevConsole.Core.Interfaces.IDestructiveOperationKinds kindsRegistry,
            int? withinDays,
            int? minOccurrences) =>
        {
            if (!IsMcpEnabled(ctx)) return Results.NotFound(new { error = "mcp_disabled" });
            var days = Math.Clamp(withinDays ?? 7, 1, 90);
            var minN = Math.Max(2, minOccurrences ?? 3);
            var cutoff = DateTime.UtcNow.AddDays(-days).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

            using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var rows = await Dapper.SqlMapper.QueryAsync<dynamic>(conn, @"
                SELECT kind, domain, host,
                       COUNT(*) AS occurrences,
                       MAX(confirmed_at) AS last_confirmed_at
                FROM deploy_intents
                WHERE confirmed_at IS NOT NULL
                  AND matched_grant_id IS NULL
                  AND confirmed_at >= @Cutoff
                GROUP BY kind, domain, host
                HAVING COUNT(*) >= @MinN
                ORDER BY COUNT(*) DESC, MAX(confirmed_at) DESC
                LIMIT 25",
                new { Cutoff = cutoff, MinN = minN });

            var suggestions = rows.Select(r =>
            {
                var kindId = (string)r.kind;
                var registered = kindsRegistry.Get(kindId);
                return new
                {
                    kind = kindId,
                    kindLabel = registered?.Label,
                    kindDanger = registered?.Danger.ToString().ToLowerInvariant(),
                    domain = (string)r.domain,
                    host = (string)r.host,
                    occurrences = (long)r.occurrences,
                    lastConfirmedAt = (string?)r.last_confirmed_at,
                };
            }).ToList();
            return Results.Ok(new { withinDays = days, minOccurrences = minN, count = suggestions.Count, suggestions });
        });
    }
}
