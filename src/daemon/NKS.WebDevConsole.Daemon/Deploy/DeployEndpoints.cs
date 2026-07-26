using Dapper;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Daemon.Data;
using NKS.WebDevConsole.Daemon.Services;

namespace NKS.WebDevConsole.Daemon.Deploy;

/// <summary>
/// Route registrations for the /api/nks.wdc.deploy/* surface — deploy runs,
/// rollback, groups, snapshots and the hook/notification test probes.
///
/// Lifted verbatim out of Program.cs. Several registrations sit behind
/// <c>if (legacyHostHandlersAtBoot)</c>: that is the #109 plugin-cutover
/// switch, read once at boot. When it is false the daemon leaves those routes
/// unregistered so the deploy plugin's own handlers win the conflict guard, so
/// the guards have to stay exactly where they are.
/// </summary>
internal static class DeployEndpoints
{
    /// <summary>
    /// Phase 7.1a deploy subsystem toggle. Shared with Program.cs, whose
    /// pre-auth gate middleware short-circuits /api/nks.wdc.deploy/* to 404
    /// using the same flag.
    /// </summary>
    internal static bool IsDeployEnabled(HttpContext ctx) =>
        ctx.RequestServices.GetRequiredService<SettingsStore>()
            .GetBool("deploy", "enabled", defaultValue: true);

    public static void MapDeployEndpoints(this WebApplication app, bool legacyHostHandlersAtBoot)
    {
        if (legacyHostHandlersAtBoot)
        {
        app.MapPost("/api/nks.wdc.deploy/sites/{domain}/hooks/test", async (
            string domain, HttpContext ctx,
            NKS.WebDevConsole.Daemon.Deploy.LocalDeployBackend localBackend,
            NKS.WebDevConsole.Core.Interfaces.IDeployIntentValidator intentValidator,
            CancellationToken ct) =>
        {
            if (!IsDeployEnabled(ctx)) return Results.NotFound(new { error = "deploy_disabled" });
            // Phase 7.5+++ — optional MCP intent gate. test-hook runs arbitrary
            // shell/http/php commands; if AI can call this without a confirmed
            // intent, the operator's intent gates on deploy/rollback/restore are
            // bypassable. Validate-before-not-found ordering keeps the oracle
            // shape consistent with the rest of the gated endpoints.
            var thIntentToken = ctx.Request.Headers["X-Intent-Token"].FirstOrDefault();
            if (!string.IsNullOrEmpty(thIntentToken))
            {
                var thAllowUnconfirmed = string.Equals(
                    ctx.Request.Headers["X-Allow-Unconfirmed"].FirstOrDefault(), "true",
                    StringComparison.OrdinalIgnoreCase);
                var thVerdict = await intentValidator.ValidateAndConsumeAsync(
                    thIntentToken, "test_hook", domain, host: "*", thAllowUnconfirmed, ct);
                if (!thVerdict.Ok)
                    return Results.Json(new { error = "intent_rejected", reason = thVerdict.Reason, detail = thVerdict.Detail },
                        statusCode: thVerdict.Reason == "pending_confirmation" ? 425 : 403);
            }
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
            var root = doc.RootElement;
            var ty = root.TryGetProperty("type", out var tEl) ? tEl.GetString() ?? "shell" : "shell";
            var cmd = root.TryGetProperty("command", out var cEl) ? cEl.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(cmd))
                return Results.BadRequest(new { error = "command_required" });
            var to = root.TryGetProperty("timeoutSeconds", out var toEl) && toEl.TryGetInt32(out var toVal) ? toVal : 30;
            var desc = root.TryGetProperty("description", out var dEl) ? dEl.GetString() : null;

            // Resolve working dir: body override → host's localTargetPath/current
            // → system temp. Falling all the way through to temp lets the operator
            // smoke-test a hook even when no host is configured yet.
            string? workingDir = root.TryGetProperty("workingDir", out var wdEl) ? wdEl.GetString() : null;
            if (string.IsNullOrEmpty(workingDir))
            {
                try
                {
                    var settingsPath = DeploySettingsPath(domain);
                    if (File.Exists(settingsPath))
                    {
                        using var sdoc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath, ct));
                        if (sdoc.RootElement.TryGetProperty("hosts", out var hostsEl)
                            && hostsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            foreach (var hEl2 in hostsEl.EnumerateArray())
                            {
                                if (hEl2.TryGetProperty("localTargetPath", out var ltEl))
                                {
                                    var t = ltEl.GetString();
                                    if (!string.IsNullOrEmpty(t))
                                    {
                                        var c = Path.Combine(t, "current");
                                        if (Directory.Exists(c)) { workingDir = c; break; }
                                    }
                                }
                            }
                        }
                    }
                }
                catch { /* best-effort */ }
            }
            workingDir ??= Path.GetTempPath();

            var spec = new NKS.WebDevConsole.Daemon.Deploy.LocalDeployBackend.HookSpec(
                Event: "test", Type: ty, Command: cmd, TimeoutSeconds: to, Enabled: true, Description: desc);
            var (ok, durationMs, error) = await localBackend.TestHookAsync(spec, workingDir, null, ct);
            return Results.Ok(new { ok, durationMs, error, workingDir });
        });

        // Phase 7.5+++ — fire a test notification through the configured Slack
        // webhook so operator can verify the URL works without waiting for a
        // real deploy. Body shape: { slackWebhook?, host? } — both optional;
        // missing slackWebhook reads from the site settings.
        app.MapPost("/api/nks.wdc.deploy/sites/{domain}/notifications/test", async (
            string domain, HttpContext ctx,
            NKS.WebDevConsole.Daemon.Deploy.LocalDeployBackend localBackend,
            CancellationToken ct) =>
        {
            if (!IsDeployEnabled(ctx)) return Results.NotFound(new { error = "deploy_disabled" });
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
            var root = doc.RootElement;
            string? slack = root.TryGetProperty("slackWebhook", out var swEl) ? swEl.GetString() : null;
            var host = root.TryGetProperty("host", out var hEl) ? hEl.GetString() ?? "test" : "test";

            // Fall back to settings if body didn't supply slackWebhook.
            if (string.IsNullOrEmpty(slack))
            {
                try
                {
                    var sp = DeploySettingsPath(domain);
                    if (File.Exists(sp))
                    {
                        using var sd = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(sp, ct));
                        if (sd.RootElement.TryGetProperty("notifications", out var nEl)
                            && nEl.TryGetProperty("slackWebhook", out var swEl2))
                            slack = swEl2.GetString();
                    }
                }
                catch { /* best-effort */ }
            }
            if (string.IsNullOrEmpty(slack))
                return Results.BadRequest(new { error = "slack_webhook_not_configured" });

            var sw = System.Diagnostics.Stopwatch.StartNew();
            string? err = null;
            try
            {
                // Direct POST (bypasses dispatch try/catch) so the operator
                // sees real webhook errors here instead of silent success.
                await NKS.WebDevConsole.Daemon.Deploy.LocalDeployBackend.PostSlackAsync(
                    slack!, domain, host,
                    deployId: "test-" + Guid.NewGuid().ToString("D")[..8],
                    success: true, durationMs: 0, error: null, ct);
            }
            catch (Exception ex) { err = ex.Message; }
            sw.Stop();
            return Results.Ok(new { ok = err is null, durationMs = sw.ElapsedMilliseconds, error = err });
        });

        app.MapPost("/api/nks.wdc.deploy/test-host-connection", async (
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (!IsDeployEnabled(ctx)) return Results.NotFound(new { error = "deploy_disabled" });
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
            var root = doc.RootElement;
            var host = root.TryGetProperty("host", out var hEl) ? hEl.GetString() : null;
            var port = root.TryGetProperty("port", out var pEl) && pEl.TryGetInt32(out var p) ? p : 22;

            if (string.IsNullOrWhiteSpace(host))
                return Results.BadRequest(new { error = "host is required" });
            if (port < 1 || port > 65535)
                return Results.BadRequest(new { error = "port must be in [1, 65535]" });

            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var probe = new System.Net.Sockets.TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await probe.ConnectAsync(host!, port, cts.Token);
                sw.Stop();
                return Results.Ok(new { ok = true, latencyMs = sw.ElapsedMilliseconds });
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                sw.Stop();
                return Results.Ok(new
                {
                    ok = false, code = "timeout",
                    error = $"TCP probe to {host}:{port} timed out after 5s",
                });
            }
            catch (System.Net.Sockets.SocketException ex)
            {
                sw.Stop();
                return Results.Ok(new
                {
                    ok = false, code = "socket_error",
                    error = $"{host}:{port} unreachable: {ex.Message}",
                });
            }
            catch (Exception ex)
            {
                sw.Stop();
                return Results.Ok(new
                {
                    ok = false, code = "unexpected",
                    error = $"Probe failed: {ex.Message}",
                });
            }
        });
        } // end if (legacyHostHandlersAtBoot) — closes the 3 host-only daemon handler block

        // + DeploySiteTab's hasConfig probe (returns 404→empty when zero rows
        // would be returned, so frontend keeps showing the wizard CTA).
        //
        // Phase D (#109) — gated by legacyHostHandlersAtBoot. Plugin's history
        // route in NksDeployRoutes.cs reads the same IDeployRunsRepository
        // (cross-ALC shared) so the projection shape matches when plugin
        // authority kicks in. Pure read, no destructive surface.
        if (legacyHostHandlersAtBoot)
        {
        app.MapGet("/api/nks.wdc.deploy/sites/{domain}/history", async (
            string domain,
            int? limit,
            string? triggeredBy,
            NKS.WebDevConsole.Core.Interfaces.IDeployRunsRepository runs,
            CancellationToken ct) =>
        {
            var rows = await runs.ListForDomainAsync(domain, limit ?? 50, ct);
            // Phase 7.5+++ — optional triggeredBy filter (gui|mcp|cli|other).
            // In-memory filter is fine since the rowcount is already capped by
            // the limit param (default 50). Empty string = no filter applied.
            if (!string.IsNullOrWhiteSpace(triggeredBy))
            {
                rows = rows.Where(r => string.Equals(r.TriggeredBy, triggeredBy,
                    StringComparison.OrdinalIgnoreCase)).ToList();
            }
            var entries = rows.Select(r => new
            {
                deployId   = r.Id,
                domain     = r.Domain,
                host       = r.Host,
                branch     = r.Branch ?? "",
                finalPhase = MapStatusToPhase(r.Status),
                startedAt  = r.StartedAt.ToString("o"),
                completedAt = r.CompletedAt?.ToString("o"),
                commitSha  = r.CommitSha,
                releaseId  = r.ReleaseId,
                error      = r.ErrorMessage,
                // Phase 7.5+++ — surface trigger source so operators can audit
                // which deploys came from AI/MCP vs GUI vs CI/CLI.
                triggeredBy = r.TriggeredBy,
            }).ToList();
            return Results.Ok(new { domain, count = entries.Count, entries });
        });
        } // end if (legacyHostHandlersAtBoot) — history GET block

        // Snapshot list — pre-deploy DB snapshots that ran for this site.
        // Composed from deploy_runs rows with non-null pre_deploy_backup_path.
        // Real backend (when it ships) writes the snapshot path + size via
        // IDeployRunsRepository.UpdatePreDeployBackupAsync mid-run; this view
        // just projects those rows into the frontend's DeploySnapshotEntry shape.
        //
        // Phase D (#109) — gated, pure read, plugin's ListSnapshots handler
        // reads the same shared IDeployRunsRepository.
        if (legacyHostHandlersAtBoot)
        {
        app.MapGet("/api/nks.wdc.deploy/sites/{domain}/snapshots", async (
            string domain,
            NKS.WebDevConsole.Core.Interfaces.IDeployRunsRepository runs,
            CancellationToken ct) =>
        {
            var rows = await runs.ListForDomainAsync(domain, limit: 200, ct);
            var entries = rows
                .Where(r => !string.IsNullOrEmpty(r.PreDeployBackupPath))
                .Select(r => new
                {
                    id          = r.Id,
                    createdAt   = r.StartedAt.ToString("o"),
                    sizeBytes   = r.PreDeployBackupSizeBytes ?? 0,
                    path        = r.PreDeployBackupPath!,
                })
                .ToList();
            return Results.Ok(new { domain, count = entries.Count, entries });
        });
        } // end if (legacyHostHandlersAtBoot) — snapshots list GET block

        // Deploy settings persistence — JSON file under
        // {WdcPaths.DataRoot}/deploy-settings/{domain}.json. Frontend's
        // DeploySettingsPanel writes here when operator clicks Save in any tab.
        // Setup wizard's Finish button stores its first-host config here too,
        // which transitions the site from "wizard CTA" empty state to the full
        // command center on next page load (DeploySiteTab.refreshAll() now
        // has a hasConfig truthy signal).
        //
        // File-per-site keeps the schema dumb: we serialise the body the
        // frontend posts verbatim (Phase 7.5 stub). When the real backend ships
        // it can validate against a schema before persist.
        static string DeploySettingsPath(string domain)
        {
            var dir = Path.Combine(NKS.WebDevConsole.Core.Services.WdcPaths.DataRoot, "deploy-settings");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir,
                NKS.WebDevConsole.Daemon.Deploy.DeployRestHelpers.SanitiseDomainForFilename(domain) + ".json");
        }

        // Phase 7.5+++ — read settings.snapshot.retentionDays for a domain.
        // Returns null when settings are absent / malformed so callers can
        // fall back to a sensible default. Best-effort — never throws.
        static int? ReadSnapshotRetentionDays(string domain)
        {
            try
            {
                var sp = DeploySettingsPath(domain);
                if (!File.Exists(sp)) return null;
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(sp));
                if (doc.RootElement.TryGetProperty("snapshot", out var sEl)
                    && sEl.TryGetProperty("retentionDays", out var rdEl)
                    && rdEl.TryGetInt32(out var rd) && rd > 0)
                    return rd;
            }
            catch { /* best-effort */ }
            return null;
        }

        // Phase 7.5+++ — purge snapshot zips older than retentionDays in the
        // given backups subfolder ("manual" or "pre-deploy"). Glob-and-delete
        // based on file mtime. Called at snapshot creation moments so no
        // separate scheduler is needed; the zip dir stays bounded by the
        // operator's own snapshot cadence + retention setting.
        static int PurgeOldSnapshots(string subfolder, string domain, int retentionDays)
        {
            if (retentionDays <= 0) return 0;
            try
            {
                var dir = Path.Combine(NKS.WebDevConsole.Core.Services.WdcPaths.BackupsRoot, subfolder, domain);
                if (!Directory.Exists(dir)) return 0;
                var cutoff = DateTime.UtcNow - TimeSpan.FromDays(retentionDays);
                var purged = 0;
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(f) < cutoff)
                        {
                            File.Delete(f);
                            purged++;
                        }
                    }
                    catch { /* skip locked / permission denied */ }
                }
                return purged;
            }
            catch { return 0; }
        }

        // Phase D (#109) — gated, pure read of per-site deploy-settings.json.
        // PUT counterpart stays daemon-authoritative (intent-gated, mutates
        // state — gating it would also need plugin to honour the same intent
        // validator path; deferred to a future iter).
        if (legacyHostHandlersAtBoot)
        {
        app.MapGet("/api/nks.wdc.deploy/sites/{domain}/settings", (string domain) =>
        {
            var path = DeploySettingsPath(domain);
            if (!File.Exists(path))
            {
                // 404 lets the frontend fall back to defaultDeploySettings() —
                // keeps existing behaviour from when this endpoint didn't exist.
                return Results.NotFound(new { error = "no_settings_yet", domain });
            }
            try
            {
                var json = File.ReadAllText(path);
                // Stream the raw JSON back rather than re-deserialising —
                // frontend's DeploySettings shape is what we wrote, what we
                // read should round-trip byte-equivalent.
                return Results.Content(json, "application/json");
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = "read_failed", message = ex.Message }, statusCode: 500);
            }
        });
        } // end if (legacyHostHandlersAtBoot) — settings GET block

        app.MapPut("/api/nks.wdc.deploy/sites/{domain}/settings", async (
            string domain, HttpContext ctx,
            NKS.WebDevConsole.Core.Interfaces.IDeployIntentValidator intentValidator,
            CancellationToken ct) =>
        {
            // Phase 7.5+++ — optional MCP intent gate. settings_write rewrites
            // the per-site deploy config file; uncontrolled access lets an AI
            // plant hook payloads or change deploy targets. Validate-before-
            // write so a bogus token is denied without touching the file.
            var swIntentToken = ctx.Request.Headers["X-Intent-Token"].FirstOrDefault();
            if (!string.IsNullOrEmpty(swIntentToken))
            {
                var swAllowUnconfirmed = string.Equals(
                    ctx.Request.Headers["X-Allow-Unconfirmed"].FirstOrDefault(), "true",
                    StringComparison.OrdinalIgnoreCase);
                var swVerdict = await intentValidator.ValidateAndConsumeAsync(
                    swIntentToken, "settings_write", domain, host: "*", swAllowUnconfirmed, ct);
                if (!swVerdict.Ok)
                    return Results.Json(new { error = "intent_rejected", reason = swVerdict.Reason, detail = swVerdict.Detail },
                        statusCode: swVerdict.Reason == "pending_confirmation" ? 425 : 403);
            }

            // Read and validate body is JSON-shaped — anything past that is the
            // frontend's contract; we don't enforce per-field rules here so a new
            // setting can land without daemon restart.
            string body;
            using (var reader = new StreamReader(ctx.Request.Body))
                body = await reader.ReadToEndAsync(ct);
            try { System.Text.Json.JsonDocument.Parse(body); }
            catch { return Results.BadRequest(new { error = "invalid_json" }); }

            var path = DeploySettingsPath(domain);
            // Atomic write: temp file in same dir + File.Move with overwrite.
            // Avoids leaving a half-written file if the daemon crashes mid-flush.
            var tmp = path + ".tmp";
            await File.WriteAllTextAsync(tmp, body, ct);
            // File.Move on Windows pre-.NET 5 errored on overwrite; current .NET
            // overload accepts overwrite=true safely.
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
            return Results.Ok(new { domain, status = "saved", bytes = body.Length });
        });

        // Single deploy status — used by the drawer's status polling fallback.
        // Phase D (#109) — gated, pure read, plugin's GetDeploy handler in
        // NksDeployRoutes.cs reads the same shared repository.
        if (legacyHostHandlersAtBoot)
        {
        app.MapGet("/api/nks.wdc.deploy/sites/{domain}/deploys/{deployId}", async (
            string domain,
            string deployId,
            NKS.WebDevConsole.Core.Interfaces.IDeployRunsRepository runs,
            CancellationToken ct) =>
        {
            var row = await runs.GetByIdAsync(deployId, ct);
            if (row is null || !string.Equals(row.Domain, domain, StringComparison.OrdinalIgnoreCase))
                return Results.NotFound(new { error = "deploy_not_found", deployId });
            return Results.Ok(new
            {
                deployId   = row.Id,
                domain     = row.Domain,
                host       = row.Host,
                finalPhase = MapStatusToPhase(row.Status),
                startedAt  = row.StartedAt.ToString("o"),
                completedAt = row.CompletedAt?.ToString("o"),
                commitSha  = row.CommitSha,
                releaseId  = row.ReleaseId,
                error      = row.ErrorMessage,
                success    = row.Status == "completed",
            });
        });
        } // end if (legacyHostHandlersAtBoot) — deploys/{deployId} GET block

        // Phase 7.5+ — rollback a deploy. POST /sites/{domain}/deploys/{deployId}/rollback.
        // Real local-loopback rollback: when host has localTargetPath configured AND
        // {target}/.dep/previous_release exists, atomically swap `current` symlink
        // back to the path stored in previous_release. Otherwise the call still
        // records a rollback row in the DB so the audit log stays accurate, but
        // the filesystem state isn't touched (no localPaths configured).
        app.MapPost("/api/nks.wdc.deploy/sites/{domain}/deploys/{deployId}/rollback", async (
            string domain, string deployId, HttpContext rbCtx,
            NKS.WebDevConsole.Core.Interfaces.IDeployRunsRepository runs,
            NKS.WebDevConsole.Core.Interfaces.IDeployIntentValidator intentValidator,
            NKS.WebDevConsole.Core.Interfaces.IDeployEventBroadcaster eventsBus,
            CancellationToken ct) =>
        {
            // Phase 7.5+++ — optional MCP intent gate. Validate BEFORE the
            // not-found check so a bogus token can't be probed against arbitrary
            // deployIds to learn which exist (token validity 403 vs deploy
            // existence 404 would otherwise leak that signal to an attacker).
            // When X-Intent-Token header is present, validator enforces
            // kind=rollback + scope match. Without a token the endpoint stays
            // open (back-compat with GUI flows that don't request a token).
            // Host scope is validated as wildcard "*" since we don't yet know
            // the source row's host before the not-found check runs.
            var rbIntentToken = rbCtx.Request.Headers["X-Intent-Token"].FirstOrDefault();
            if (!string.IsNullOrEmpty(rbIntentToken))
            {
                var rbAllowUnconfirmed = string.Equals(
                    rbCtx.Request.Headers["X-Allow-Unconfirmed"].FirstOrDefault(), "true",
                    StringComparison.OrdinalIgnoreCase);
                var verdict = await intentValidator.ValidateAndConsumeAsync(
                    rbIntentToken, "rollback", domain, "*", rbAllowUnconfirmed, ct);
                if (!verdict.Ok)
                    return Results.Json(new { error = "intent_rejected", reason = verdict.Reason, detail = verdict.Detail },
                        statusCode: verdict.Reason == "pending_confirmation" ? 425 : 403);
            }

            var source = await runs.GetByIdAsync(deployId, ct);
            if (source is null || !string.Equals(source.Domain, domain, StringComparison.OrdinalIgnoreCase))
                return Results.NotFound(new { error = "deploy_not_found", deployId });

            // Resolve the local target for this host so we can perform a real
            // symlink swap. Mirrors the deploy endpoint's settings lookup.
            string? targetPath = null;
            try
            {
                var settingsPath = DeploySettingsPath(domain);
                if (File.Exists(settingsPath))
                {
                    using var sdoc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath, ct));
                    if (sdoc.RootElement.TryGetProperty("hosts", out var hostsEl)
                        && hostsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var hEl in hostsEl.EnumerateArray())
                        {
                            if (!hEl.TryGetProperty("name", out var nEl)) continue;
                            if (!string.Equals(nEl.GetString(), source.Host, StringComparison.OrdinalIgnoreCase)) continue;
                            if (hEl.TryGetProperty("localTargetPath", out var ltEl))
                                targetPath = ltEl.GetString();
                            break;
                        }
                    }
                }
            }
            catch { /* best-effort */ }

            string? swappedTo = null;
            string? rollbackError = null;
            if (!string.IsNullOrEmpty(targetPath))
            {
                var depPrev = Path.Combine(targetPath, ".dep", "previous_release");
                var currentLink = Path.Combine(targetPath, "current");
                if (File.Exists(depPrev))
                {
                    try
                    {
                        var prevRelease = (await File.ReadAllTextAsync(depPrev, ct)).Trim();
                        if (!string.IsNullOrEmpty(prevRelease) && Directory.Exists(prevRelease))
                        {
                            // Remove existing current link/dir, then recreate
                            if (Directory.Exists(currentLink))
                            {
                                var fi = new DirectoryInfo(currentLink);
                                if (fi.LinkTarget is not null) Directory.Delete(currentLink);
                                else Directory.Delete(currentLink, recursive: true);
                            }
                            Directory.CreateSymbolicLink(currentLink, prevRelease);
                            swappedTo = prevRelease;

                            // Rotate .dep state — current_release points to prev,
                            // and previous_release becomes the deploy we rolled back FROM
                            // so a subsequent rollback returns to the more-recent release.
                            var depCurrent = Path.Combine(targetPath, ".dep", "current_release");
                            var oldCurrent = File.Exists(depCurrent)
                                ? (await File.ReadAllTextAsync(depCurrent, ct)).Trim()
                                : string.Empty;
                            await File.WriteAllTextAsync(depCurrent, prevRelease, ct);
                            if (!string.IsNullOrEmpty(oldCurrent))
                                await File.WriteAllTextAsync(depPrev, oldCurrent, ct);
                        }
                        else
                        {
                            rollbackError = "previous_release path missing or empty";
                        }
                    }
                    catch (Exception ex) { rollbackError = ex.Message; }
                }
                else
                {
                    rollbackError = ".dep/previous_release file not found — nothing to roll back to";
                }
            }

            var rollbackId = Guid.NewGuid().ToString("D");
            var now = DateTimeOffset.UtcNow;
            await runs.InsertAsync(new NKS.WebDevConsole.Core.Interfaces.DeployRunRow(
                Id: rollbackId, Domain: domain, Host: source.Host,
                ReleaseId: now.ToString("yyyyMMdd_HHmmss") + "-rollback-of-" + deployId[..8],
                Branch: source.Branch, CommitSha: source.CommitSha,
                Status: rollbackError is null ? "completed" : "failed",
                IsPastPonr: true,
                StartedAt: now, CompletedAt: now,
                ExitCode: rollbackError is null ? 0 : -1,
                ErrorMessage: rollbackError, DurationMs: 50,
                TriggeredBy: "gui",
                BackendId: swappedTo is not null ? "local-rollback" : "noop-rollback",
                CreatedAt: now, UpdatedAt: now), ct);
            // Mark the source as rolled-back so the UI tag flips.
            await runs.UpdateStatusAsync(deployId, "rolled_back", ct);

            await eventsBus.BroadcastAsync("deploy:complete", new
            {
                deployId = rollbackId,
                success = rollbackError is null,
                sourceDeployId = deployId,
                kind = "rollback",
                swappedTo,
                error = rollbackError,
            });
            return Results.Ok(new
            {
                sourceDeployId = deployId,
                status = rollbackError is null ? "rolled_back" : "rollback_failed",
                swappedTo,
                error = rollbackError,
            });
        });

        // Phase 7.5+++ — rollback to a SPECIFIC historical release. Useful when
        // previous_release is itself broken (operator picks an earlier known-good
        // release from the Releases tab). Body: { host, releaseId }. Looks up
        // the host's localTargetPath, verifies releases/{releaseId} exists, then
        // performs the same atomic symlink swap + .dep rotation as the deploy-id
        // rollback path.
        app.MapPost("/api/nks.wdc.deploy/sites/{domain}/rollback-to", async (
            string domain, HttpContext ctx,
            NKS.WebDevConsole.Core.Interfaces.IDeployRunsRepository runs,
            NKS.WebDevConsole.Core.Interfaces.IDeployIntentValidator intentValidator,
            NKS.WebDevConsole.Core.Interfaces.IDeployEventBroadcaster eventsBus,
            CancellationToken ct) =>
        {
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
            var root = doc.RootElement;
            var host = root.TryGetProperty("host", out var hEl) ? hEl.GetString() : null;
            var releaseId = root.TryGetProperty("releaseId", out var rEl) ? rEl.GetString() : null;
            if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(releaseId))
                return Results.BadRequest(new { error = "host_and_releaseId_required" });

            // Phase 7.5+++ — optional MCP intent gate. Same shape as the deploy-id
            // rollback endpoint above. Token may also be provided in body for
            // clients that can't set custom headers (older HTTP libs).
            var rtIntentToken = ctx.Request.Headers["X-Intent-Token"].FirstOrDefault();
            if (string.IsNullOrEmpty(rtIntentToken)
                && root.TryGetProperty("intentToken", out var rtTokenEl))
                rtIntentToken = rtTokenEl.GetString();
            if (!string.IsNullOrEmpty(rtIntentToken))
            {
                var rtAllowUnconfirmed = string.Equals(
                    ctx.Request.Headers["X-Allow-Unconfirmed"].FirstOrDefault(), "true",
                    StringComparison.OrdinalIgnoreCase);
                var verdict = await intentValidator.ValidateAndConsumeAsync(
                    rtIntentToken, "rollback", domain, host, rtAllowUnconfirmed, ct);
                if (!verdict.Ok)
                    return Results.Json(new { error = "intent_rejected", reason = verdict.Reason, detail = verdict.Detail },
                        statusCode: verdict.Reason == "pending_confirmation" ? 425 : 403);
            }

            // Resolve target path from settings — same lookup as the deploy and
            // rollback endpoints so behaviour stays consistent.
            string? targetPath = null;
            try
            {
                var settingsPath = DeploySettingsPath(domain);
                if (File.Exists(settingsPath))
                {
                    using var sdoc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath, ct));
                    if (sdoc.RootElement.TryGetProperty("hosts", out var hostsEl)
                        && hostsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var hEl2 in hostsEl.EnumerateArray())
                        {
                            if (!hEl2.TryGetProperty("name", out var nEl)) continue;
                            if (!string.Equals(nEl.GetString(), host, StringComparison.OrdinalIgnoreCase)) continue;
                            if (hEl2.TryGetProperty("localTargetPath", out var ltEl))
                                targetPath = ltEl.GetString();
                            break;
                        }
                    }
                }
            }
            catch { /* best-effort */ }

            if (string.IsNullOrEmpty(targetPath))
                return Results.BadRequest(new { error = "no_local_target_configured", host });

            var releaseDir = Path.Combine(targetPath, "releases", releaseId);
            if (!Directory.Exists(releaseDir))
                return Results.NotFound(new { error = "release_not_found", releaseId, host });

            var currentLink = Path.Combine(targetPath, "current");
            var depDir = Path.Combine(targetPath, ".dep");
            Directory.CreateDirectory(depDir);
            var depCurrent = Path.Combine(depDir, "current_release");
            var depPrev = Path.Combine(depDir, "previous_release");

            string? oldCurrent = null;
            string? error = null;
            try
            {
                if (File.Exists(depCurrent))
                    oldCurrent = (await File.ReadAllTextAsync(depCurrent, ct)).Trim();

                if (Directory.Exists(currentLink))
                {
                    var fi = new DirectoryInfo(currentLink);
                    if (fi.LinkTarget is not null) Directory.Delete(currentLink);
                    else Directory.Delete(currentLink, recursive: true);
                }
                Directory.CreateSymbolicLink(currentLink, releaseDir);
                await File.WriteAllTextAsync(depCurrent, releaseDir, ct);
                if (!string.IsNullOrEmpty(oldCurrent) && oldCurrent != releaseDir)
                    await File.WriteAllTextAsync(depPrev, oldCurrent, ct);
            }
            catch (Exception ex) { error = ex.Message; }

            var rollbackId = Guid.NewGuid().ToString("D");
            var now = DateTimeOffset.UtcNow;
            await runs.InsertAsync(new NKS.WebDevConsole.Core.Interfaces.DeployRunRow(
                Id: rollbackId, Domain: domain, Host: host,
                ReleaseId: now.ToString("yyyyMMdd_HHmmss") + "-rollback-to-" + releaseId,
                Branch: null, CommitSha: null,
                Status: error is null ? "completed" : "failed",
                IsPastPonr: error is null,
                StartedAt: now, CompletedAt: now,
                ExitCode: error is null ? 0 : -1,
                ErrorMessage: error, DurationMs: 50,
                TriggeredBy: "gui", BackendId: "local-rollback-to",
                CreatedAt: now, UpdatedAt: now), ct);

            await eventsBus.BroadcastAsync("deploy:complete", new
            {
                deployId = rollbackId,
                success = error is null,
                kind = "rollback-to",
                host, releaseId, swappedTo = error is null ? releaseDir : null, error,
            });
            return Results.Ok(new
            {
                status = error is null ? "rolled_back" : "rollback_failed",
                host, releaseId, swappedTo = error is null ? releaseDir : null, error,
            });
        });

        // DELETE /sites/{domain}/deploys/{deployId} — cancel an in-flight deploy.
        // Phase 7.5+++ — actually trips the LocalDeployBackend's CancellationToken
        // so the running task bails at the next checkpoint (rather than just
        // flipping the DB row that the backend then overwrites with completed).
        // Pre-PONR only — past_point_of_no_return → use rollback instead.
        app.MapDelete("/api/nks.wdc.deploy/sites/{domain}/deploys/{deployId}", async (
            string domain, string deployId, HttpContext ctx,
            NKS.WebDevConsole.Core.Interfaces.IDeployRunsRepository runs,
            NKS.WebDevConsole.Core.Interfaces.IDeployIntentValidator intentValidator,
            NKS.WebDevConsole.Core.Interfaces.IDeployEventBroadcaster eventsBus,
            NKS.WebDevConsole.Daemon.Deploy.LocalDeployBackend localBackend,
            CancellationToken ct) =>
        {
            // Phase 7.5+++ — optional MCP intent gate. Cancel interrupts an
            // in-flight deploy; it's reversible (the operator can just re-deploy)
            // but counts as a destructive override of an active operation.
            // Validate-before-not-found prevents an oracle leak (bogus token
            // can't enumerate deployIds via 403 vs 404).
            var intentToken = ctx.Request.Headers["X-Intent-Token"].FirstOrDefault();
            if (!string.IsNullOrEmpty(intentToken))
            {
                var allowUnconfirmed = string.Equals(
                    ctx.Request.Headers["X-Allow-Unconfirmed"].FirstOrDefault(), "true",
                    StringComparison.OrdinalIgnoreCase);
                // Best-effort host resolution from the row (after we look it up
                // below, but for the intent we accept "*" host since cancel can
                // legitimately fire on any host the deploy was started against).
                var verdict = await intentValidator.ValidateAndConsumeAsync(
                    intentToken, "cancel", domain, host: "*", allowUnconfirmed, ct);
                if (!verdict.Ok)
                {
                    return Results.Json(
                        new { error = "intent_rejected", reason = verdict.Reason, detail = verdict.Detail },
                        statusCode: verdict.Reason == "pending_confirmation" ? 425 : 403);
                }
            }

            var row = await runs.GetByIdAsync(deployId, ct);
            if (row is null || !string.Equals(row.Domain, domain, StringComparison.OrdinalIgnoreCase))
                return Results.NotFound(new { error = "deploy_not_found", deployId });
            if (row.IsPastPonr)
                return Results.Conflict(new { error = "past_point_of_no_return", detail = "Use rollback instead" });
            if (row.Status is "completed" or "failed" or "cancelled" or "rolled_back")
                return Results.Conflict(new { error = "deploy_already_terminal", currentStatus = row.Status });

            // Real cancel — trip the backend's CTS BEFORE writing DB rows so the
            // backend's catch (OperationCanceledException) handles its own
            // status flip. tripped=false means the backend already finished
            // (race) so we still mark cancelled in DB for audit consistency.
            var tripped = localBackend.TryCancel(deployId);
            if (!tripped)
            {
                // Backend not in-flight (test fixture, dummy row, or finished
                // racing): fall back to legacy DB-only flip so the audit trail
                // still shows operator intent.
                await runs.MarkCompletedAsync(deployId, success: false, exitCode: -1,
                    errorMessage: "cancelled by operator", durationMs: 0, ct);
                await runs.UpdateStatusAsync(deployId, "cancelled", ct);
                await eventsBus.BroadcastAsync("deploy:complete",
                    new { deployId, success = false, error = "cancelled" });
            }
            return Results.Ok(new { deployId, status = "cancelled", interrupted = tripped });
        });

        // GET /sites/{domain}/groups — list multi-host deploy groups for site.
        // Phase D (#109) — gated, pure read.
        if (legacyHostHandlersAtBoot)
        {
        app.MapGet("/api/nks.wdc.deploy/sites/{domain}/groups", async (
            string domain, int? limit,
            NKS.WebDevConsole.Core.Interfaces.IDeployGroupsRepository groups,
            CancellationToken ct) =>
        {
            var rows = await groups.ListForDomainAsync(domain, limit ?? 50, ct);
            var entries = rows.Select(g => new
            {
                id          = g.Id,
                domain      = g.Domain,
                hosts       = g.Hosts,
                hostDeployIds = g.HostDeployIds,
                phase       = g.Phase,
                startedAt   = g.StartedAt.ToString("o"),
                completedAt = g.CompletedAt?.ToString("o"),
                errorMessage = g.ErrorMessage,
                triggeredBy = g.TriggeredBy,
            }).ToList();
            return Results.Ok(new { domain, count = entries.Count, entries });
        });
        } // end if (legacyHostHandlersAtBoot) — groups list GET block

        // POST /sites/{domain}/groups — start a multi-host deploy group.
        // Phase 7.5+++ — REAL fan-out via LocalDeployBackend when each host has
        // localPaths configured in settings. Hosts without localPaths get a
        // dummy-group row so they remain visible in the GUI Groups tab as a
        // noop entry (operator can spot which hosts are misconfigured).
        // Hosts list of length < 2 → 400.
        app.MapPost("/api/nks.wdc.deploy/sites/{domain}/groups", async (
            string domain, HttpContext ctx,
            NKS.WebDevConsole.Core.Interfaces.IDeployGroupsRepository groups,
            NKS.WebDevConsole.Core.Interfaces.IDeployRunsRepository runs,
            NKS.WebDevConsole.Core.Interfaces.IDeployIntentValidator intentValidator,
            NKS.WebDevConsole.Core.Interfaces.IDeployEventBroadcaster eventsBus,
            NKS.WebDevConsole.Daemon.Deploy.LocalDeployBackend localBackend,
            CancellationToken ct) =>
        {
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
            var root = doc.RootElement;
            var hosts = root.TryGetProperty("hosts", out var hEl) && hEl.ValueKind == System.Text.Json.JsonValueKind.Array
                ? hEl.EnumerateArray().Select(h => h.GetString() ?? "").Where(s => s.Length > 0).ToList()
                : new List<string>();
            if (hosts.Count < 2)
                return Results.BadRequest(new { error = "groups_require_2_or_more_hosts", got = hosts.Count });

            // Phase 7.5+++ — optional MCP intent gate. Group deploy uses kind=deploy
            // (not 'group') because the per-host underlying operation IS deploy;
            // a single intent token can authorize the whole fan-out. Validates
            // against the FIRST host so MCP grants matching by exact host still
            // work (the group shares one token across all hosts). Token can come
            // from header X-Intent-Token or body.intentToken.
            var grpIntentToken = ctx.Request.Headers["X-Intent-Token"].FirstOrDefault();
            if (string.IsNullOrEmpty(grpIntentToken)
                && root.TryGetProperty("intentToken", out var grpTokenEl))
                grpIntentToken = grpTokenEl.GetString();
            if (!string.IsNullOrEmpty(grpIntentToken))
            {
                var grpAllowUnconfirmed = string.Equals(
                    ctx.Request.Headers["X-Allow-Unconfirmed"].FirstOrDefault(), "true",
                    StringComparison.OrdinalIgnoreCase);
                var verdict = await intentValidator.ValidateAndConsumeAsync(
                    grpIntentToken, "deploy", domain, hosts[0], grpAllowUnconfirmed, ct);
                if (!verdict.Ok)
                    return Results.Json(new { error = "intent_rejected", reason = verdict.Reason, detail = verdict.Detail },
                        statusCode: verdict.Reason == "pending_confirmation" ? 425 : 403);
            }

            // Resolve per-host localPaths + shared/keepReleases options up-front
            // so we can decide which hosts will run real vs noop.
            var hostConfigs = new Dictionary<string, (string? src, string? tgt, IReadOnlyList<string>? sharedDirs, IReadOnlyList<string>? sharedFiles)>();
            int? siteKeepReleases = null;
            bool allowConcurrent = true;
            try
            {
                var settingsPath = DeploySettingsPath(domain);
                if (File.Exists(settingsPath))
                {
                    using var sdoc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath, ct));
                    var rootEl = sdoc.RootElement;
                    if (rootEl.TryGetProperty("hosts", out var hostsEl)
                        && hostsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var hEl2 in hostsEl.EnumerateArray())
                        {
                            if (!hEl2.TryGetProperty("name", out var nEl)) continue;
                            var name = nEl.GetString() ?? "";
                            if (!hosts.Contains(name)) continue;
                            string? src = hEl2.TryGetProperty("localSourcePath", out var lsEl) ? lsEl.GetString() : null;
                            string? tgt = hEl2.TryGetProperty("localTargetPath", out var ltEl) ? ltEl.GetString() : null;
                            List<string>? sd = null;
                            List<string>? sf = null;
                            if (hEl2.TryGetProperty("sharedDirs", out var sdEl)
                                && sdEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                                sd = sdEl.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToList();
                            if (hEl2.TryGetProperty("sharedFiles", out var sfEl)
                                && sfEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                                sf = sfEl.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToList();
                            hostConfigs[name] = (src, tgt, sd, sf);
                        }
                    }
                    if (rootEl.TryGetProperty("advanced", out var advEl))
                    {
                        if (advEl.TryGetProperty("keepReleases", out var krEl) && krEl.TryGetInt32(out var krVal))
                            siteKeepReleases = krVal;
                        if (advEl.TryGetProperty("allowConcurrentHosts", out var acEl)
                            && acEl.ValueKind == System.Text.Json.JsonValueKind.False)
                            allowConcurrent = false;
                    }
                }
            }
            catch { /* best-effort — hosts without config become noop entries */ }

            var groupId = Guid.NewGuid().ToString("D");
            var now = DateTimeOffset.UtcNow;
            var releaseId = now.ToString("yyyyMMdd_HHmmss");

            // Spawn one DeployRunRow per host. Real ones start in 'queued' so the
            // background backend can transition them; noop ones go straight to
            // 'completed' as before so they don't sit in queued forever.
            var hostDeployIds = new Dictionary<string, string>(hosts.Count);
            var realDeploys = new List<(string deployId, string host, string src, string tgt, IReadOnlyList<string>? sd, IReadOnlyList<string>? sf)>();
            foreach (var host in hosts)
            {
                var deployId = Guid.NewGuid().ToString("D");
                var (src, tgt, sd, sf) = hostConfigs.TryGetValue(host, out var c)
                    ? c : (null, null, null, null);
                var hasPaths = !string.IsNullOrEmpty(src) && !string.IsNullOrEmpty(tgt);
                await runs.InsertAsync(new NKS.WebDevConsole.Core.Interfaces.DeployRunRow(
                    Id: deployId, Domain: domain, Host: host,
                    ReleaseId: releaseId,
                    Branch: null, CommitSha: null,
                    Status: hasPaths ? "queued" : "completed",
                    IsPastPonr: !hasPaths,
                    StartedAt: now,
                    CompletedAt: hasPaths ? null : now,
                    ExitCode: hasPaths ? null : 0,
                    ErrorMessage: null,
                    DurationMs: hasPaths ? null : 50,
                    TriggeredBy: "gui",
                    BackendId: hasPaths ? "local" : "noop-group",
                    CreatedAt: now, UpdatedAt: now,
                    GroupId: groupId), ct);
                hostDeployIds[host] = deployId;
                if (hasPaths) realDeploys.Add((deployId, host, src!, tgt!, sd, sf));
            }

            await groups.InsertAsync(new NKS.WebDevConsole.Core.Interfaces.DeployGroupRow(
                Id: groupId, Domain: domain, Hosts: hosts,
                HostDeployIds: hostDeployIds,
                // Schema CHECK (migration 009) accepts: initializing, preflight,
                // deploying, awaiting_all_soak, all_succeeded, partial_failure,
                // rolling_back_all, rolled_back, group_failed.
                // 'deploying' when we have real backend work to do; 'all_succeeded'
                // when everything is noop (no localPaths configured for any host).
                Phase: realDeploys.Count > 0 ? "deploying" : "all_succeeded",
                StartedAt: now,
                CompletedAt: realDeploys.Count > 0 ? null : now,
                ErrorMessage: null, TriggeredBy: "gui",
                CreatedAt: now, UpdatedAt: now), ct);

            await eventsBus.BroadcastAsync("deploy:group-started",
                new { groupId, domain, hosts, realCount = realDeploys.Count });

            // Fan out — concurrent (default) or sequential per advanced config.
            _ = Task.Run(async () =>
            {
                if (allowConcurrent)
                {
                    var tasks = realDeploys.Select(r =>
                        localBackend.RunAsync(r.deployId, releaseId, r.src, r.tgt,
                            new NKS.WebDevConsole.Daemon.Deploy.LocalDeployBackend.Options(
                                SharedDirs: r.sd, SharedFiles: r.sf, KeepReleases: siteKeepReleases))).ToArray();
                    await Task.WhenAll(tasks);
                }
                else
                {
                    foreach (var r in realDeploys)
                    {
                        await localBackend.RunAsync(r.deployId, releaseId, r.src, r.tgt,
                            new NKS.WebDevConsole.Daemon.Deploy.LocalDeployBackend.Options(
                                SharedDirs: r.sd, SharedFiles: r.sf, KeepReleases: siteKeepReleases));
                    }
                }
                await groups.UpdatePhaseAsync(groupId, "all_succeeded", isTerminal: true, errorMessage: null, default);
                await eventsBus.BroadcastAsync("deploy:group-complete",
                    new { groupId, domain, success = true, realCount = realDeploys.Count });
            });

            return Results.Ok(new
            {
                groupId,
                idempotencyKey = Guid.NewGuid().ToString("D"),
                hostCount = hosts.Count,
                realCount = realDeploys.Count,
                noopCount = hosts.Count - realDeploys.Count,
            });
        });

        // POST /sites/{domain}/groups/{groupId}/rollback — cascade rollback
        // every committed host. Phase 7.5++ flipped per-host deploy_runs rows.
        // Phase 7.5+++ — also performs the REAL atomic symlink swap per host
        // when localTargetPath is configured + .dep/previous_release exists.
        // Hosts without localPaths get the legacy DB-only flip so the Groups
        // tab → drilldown shows them as rolled_back rather than stuck Done.
        app.MapPost("/api/nks.wdc.deploy/sites/{domain}/groups/{groupId}/rollback", async (
            string domain, string groupId, HttpContext grbCtx,
            NKS.WebDevConsole.Core.Interfaces.IDeployGroupsRepository groups,
            NKS.WebDevConsole.Core.Interfaces.IDeployRunsRepository runs,
            NKS.WebDevConsole.Core.Interfaces.IDeployIntentValidator intentValidator,
            CancellationToken ct) =>
        {
            var grp = await groups.GetByIdAsync(groupId, ct);
            if (grp is null || !string.Equals(grp.Domain, domain, StringComparison.OrdinalIgnoreCase))
                return Results.NotFound(new { error = "group_not_found", groupId });

            // Optional MCP intent gate (kind=rollback, host=*).
            var grbIntentToken = grbCtx.Request.Headers["X-Intent-Token"].FirstOrDefault();
            if (!string.IsNullOrEmpty(grbIntentToken))
            {
                var grbAllowUnconfirmed = string.Equals(
                    grbCtx.Request.Headers["X-Allow-Unconfirmed"].FirstOrDefault(), "true",
                    StringComparison.OrdinalIgnoreCase);
                var verdict = await intentValidator.ValidateAndConsumeAsync(
                    grbIntentToken, "rollback", domain, "*", grbAllowUnconfirmed, ct);
                if (!verdict.Ok)
                    return Results.Json(new { error = "intent_rejected", reason = verdict.Reason, detail = verdict.Detail },
                        statusCode: verdict.Reason == "pending_confirmation" ? 425 : 403);
            }

            // Resolve per-host localTargetPath up-front so the cascade can do
            // real swaps where configured.
            var hostTargets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var settingsPath = DeploySettingsPath(domain);
                if (File.Exists(settingsPath))
                {
                    using var sdoc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath, ct));
                    if (sdoc.RootElement.TryGetProperty("hosts", out var hostsEl)
                        && hostsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var hEl in hostsEl.EnumerateArray())
                        {
                            if (!hEl.TryGetProperty("name", out var nEl)) continue;
                            var name = nEl.GetString() ?? "";
                            if (hEl.TryGetProperty("localTargetPath", out var ltEl))
                            {
                                var t = ltEl.GetString();
                                if (!string.IsNullOrEmpty(t)) hostTargets[name] = t;
                            }
                        }
                    }
                }
            }
            catch { /* best-effort — hosts without entries get DB-only flip */ }

            var realSwaps = new List<object>();
            var noopHosts = new List<string>();
            foreach (var (host, deployId) in grp.HostDeployIds)
            {
                try { await runs.UpdateStatusAsync(deployId, "rolled_back", ct); } catch { }
                if (!hostTargets.TryGetValue(host, out var tgt))
                {
                    noopHosts.Add(host);
                    continue;
                }
                var depPrev = Path.Combine(tgt, ".dep", "previous_release");
                var currentLink = Path.Combine(tgt, "current");
                if (!File.Exists(depPrev))
                {
                    noopHosts.Add(host);
                    continue;
                }
                try
                {
                    var prevRelease = (await File.ReadAllTextAsync(depPrev, ct)).Trim();
                    if (string.IsNullOrEmpty(prevRelease) || !Directory.Exists(prevRelease))
                    {
                        noopHosts.Add(host);
                        continue;
                    }
                    // Atomic swap + .dep rotation (mirrors single-host rollback).
                    if (Directory.Exists(currentLink))
                    {
                        var fi = new DirectoryInfo(currentLink);
                        if (fi.LinkTarget is not null) Directory.Delete(currentLink);
                        else Directory.Delete(currentLink, recursive: true);
                    }
                    Directory.CreateSymbolicLink(currentLink, prevRelease);
                    var depCurrent = Path.Combine(tgt, ".dep", "current_release");
                    var oldCurrent = File.Exists(depCurrent)
                        ? (await File.ReadAllTextAsync(depCurrent, ct)).Trim() : string.Empty;
                    await File.WriteAllTextAsync(depCurrent, prevRelease, ct);
                    if (!string.IsNullOrEmpty(oldCurrent) && oldCurrent != prevRelease)
                        await File.WriteAllTextAsync(depPrev, oldCurrent, ct);
                    realSwaps.Add(new { host, swappedTo = prevRelease });
                }
                catch (Exception ex)
                {
                    realSwaps.Add(new { host, error = ex.Message });
                }
            }

            // Mark group as rolled_back via UpdatePhaseAsync — schema CHECK
            // accepts 'rolled_back' (migration 009).
            try { await groups.UpdatePhaseAsync(groupId, "rolled_back", isTerminal: true, errorMessage: null, ct); }
            catch { /* best-effort */ }

            return Results.Ok(new
            {
                groupId,
                status = "rolled_back",
                hostCount = grp.Hosts.Count,
                realSwaps,
                noopHosts,
            });
        });

        // Phase 7.5+ — on-demand snapshot WITHOUT a deploy. Frontend's
        // "Snapshot database now" button in DeploySettingsPanel hits this.
        // Real backend would actually run pg_dump / mysqldump; the dummy
        // records a synthetic deploy_runs row tagged backend_id='manual-snapshot'
        // so it surfaces in the snapshot list (which projects rows with
        // non-null pre_deploy_backup_path) without needing an actual deploy.
        //
        // Phase D (#109) — gated by legacyHostHandlersAtBoot. Plugin's
        // PostSnapshotNow ships an FS-ZIP-first path (commit 6cd22a5/6608838)
        // which captures the resolved host's current/ release directly without
        // shelling out to phar, so this endpoint is safe to delegate to plugin
        // authority when operator flips useLegacyHostHandlers=false. The DB
        // snapshotter fallback in plugin-mode handles sites without
        // localTargetPath the same way the daemon does.
        if (legacyHostHandlersAtBoot)
        {
        app.MapPost("/api/nks.wdc.deploy/sites/{domain}/snapshot-now", async (
            string domain, HttpContext ctx,
            NKS.WebDevConsole.Core.Interfaces.IDeployRunsRepository runs,
            NKS.WebDevConsole.Core.Interfaces.IDeployIntentValidator intentValidator,
            CancellationToken ct) =>
        {
            // Phase 7.5+++ — optional MCP intent gate. snapshot_create is
            // disk-fill territory if AI spams it; gate keeps the operator's
            // grant + always-confirm controls effective.
            var snIntentToken = ctx.Request.Headers["X-Intent-Token"].FirstOrDefault();
            if (!string.IsNullOrEmpty(snIntentToken))
            {
                var snAllowUnconfirmed = string.Equals(
                    ctx.Request.Headers["X-Allow-Unconfirmed"].FirstOrDefault(), "true",
                    StringComparison.OrdinalIgnoreCase);
                var snVerdict = await intentValidator.ValidateAndConsumeAsync(
                    snIntentToken, "snapshot_create", domain, host: "*", snAllowUnconfirmed, ct);
                if (!snVerdict.Ok)
                    return Results.Json(new { error = "intent_rejected", reason = snVerdict.Reason, detail = snVerdict.Detail },
                        statusCode: snVerdict.Reason == "pending_confirmation" ? 425 : 403);
            }

            var snapshotId = Guid.NewGuid().ToString("D");
            var now = DateTimeOffset.UtcNow;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // Phase 7.5+++ — when ANY host has localTargetPath configured + a
            // resolvable `current` symlink, ZIP that release dir into the manual
            // backups folder. Result is a REAL recovery artifact the operator
            // can extract back to the host. Without localPaths we keep the
            // historic fake-record behaviour so existing tests + the GUI list
            // still see an entry (back-compat).
            string? sourceCurrent = null;
            string? hostName = null;
            try
            {
                // Optional body { host: "..." } — picks a specific host's current/.
                // No body or no host → first host with localTargetPath wins.
                string? bodyHost = null;
                if (ctx.Request.ContentLength is > 0)
                {
                    try
                    {
                        using var bdoc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
                        if (bdoc.RootElement.TryGetProperty("host", out var hEl))
                            bodyHost = hEl.GetString();
                    }
                    catch { /* empty / non-JSON body is fine */ }
                }

                var settingsPath = DeploySettingsPath(domain);
                if (File.Exists(settingsPath))
                {
                    using var sdoc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath, ct));
                    if (sdoc.RootElement.TryGetProperty("hosts", out var hostsEl)
                        && hostsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var hEl2 in hostsEl.EnumerateArray())
                        {
                            if (!hEl2.TryGetProperty("name", out var nEl)) continue;
                            var n = nEl.GetString() ?? "";
                            if (!string.IsNullOrEmpty(bodyHost) && !string.Equals(n, bodyHost, StringComparison.OrdinalIgnoreCase))
                                continue;
                            if (hEl2.TryGetProperty("localTargetPath", out var ltEl))
                            {
                                var tgt = ltEl.GetString();
                                if (!string.IsNullOrEmpty(tgt))
                                {
                                    var candidate = Path.Combine(tgt, "current");
                                    if (Directory.Exists(candidate))
                                    {
                                        sourceCurrent = candidate;
                                        hostName = n;
                                        break;
                                    }
                                }
                            }
                            if (!string.IsNullOrEmpty(bodyHost)) break; // explicit host miss → don't fall through
                        }
                    }
                }
            }
            catch { /* best-effort */ }

            long sizeBytes;
            string returnedPath;
            if (sourceCurrent is not null)
            {
                // Real ZIP. Resolve `current` symlink target so the archive
                // captures the actual files, not symlink metadata.
                var realRoot = sourceCurrent;
                try
                {
                    var info = new DirectoryInfo(sourceCurrent);
                    if (info.LinkTarget is not null && Directory.Exists(info.LinkTarget))
                        realRoot = info.LinkTarget;
                }
                catch { /* fall back to current path */ }

                var backupsDir = Path.Combine(NKS.WebDevConsole.Core.Services.WdcPaths.BackupsRoot, "manual", domain);
                Directory.CreateDirectory(backupsDir);
                var realPath = Path.Combine(backupsDir, $"{snapshotId}.zip");
                try
                {
                    System.IO.Compression.ZipFile.CreateFromDirectory(
                        realRoot, realPath,
                        System.IO.Compression.CompressionLevel.Fastest,
                        includeBaseDirectory: false);
                    sizeBytes = new FileInfo(realPath).Length;

                    // Phase 7.5+++ — prune older zips per settings retention.
                    // Default 30 days when settings missing/malformed (matches
                    // defaultDeploySettings().snapshot.retentionDays in the GUI).
                    var rd = ReadSnapshotRetentionDays(domain) ?? 30;
                    PurgeOldSnapshots("manual", domain, rd);
                }
                catch (Exception ex)
                {
                    // Bubble up — operator sees the failure rather than getting a
                    // silently broken snapshot row. Common cause: file in release
                    // directory is locked by another process during the zip pass.
                    return Results.Json(new { error = "snapshot_zip_failed", detail = ex.Message },
                        statusCode: 500);
                }
                returnedPath = $"~/.wdc/backups/manual/{domain}/{snapshotId}.zip";
            }
            else
            {
                // No local target available — record a placeholder row so GUI
                // shows an entry but flag the path as the legacy stub shape.
                sizeBytes = 1024 * 512;
                returnedPath = $"~/.wdc/backups/manual/{domain}/{snapshotId}.sql.gz";
            }

            sw.Stop();
            await runs.InsertAsync(new NKS.WebDevConsole.Core.Interfaces.DeployRunRow(
                Id: snapshotId, Domain: domain, Host: hostName ?? "manual",
                ReleaseId: now.ToString("yyyyMMdd_HHmmss") + "-manual",
                Branch: null, CommitSha: null,
                Status: "completed", IsPastPonr: false,
                StartedAt: now, CompletedAt: DateTimeOffset.UtcNow,
                ExitCode: 0, ErrorMessage: null, DurationMs: sw.ElapsedMilliseconds,
                TriggeredBy: "gui", BackendId: "manual-snapshot",
                CreatedAt: now, UpdatedAt: now), ct);
            await runs.UpdatePreDeployBackupAsync(snapshotId, returnedPath, sizeBytes, ct);

            return Results.Ok(new
            {
                snapshotId, domain,
                path = returnedPath,
                sizeBytes,
                durationMs = sw.ElapsedMilliseconds,
                host = hostName,
            });
        });
        } // end if (legacyHostHandlersAtBoot) — snapshot-now block

        // Phase 7.5+ — restore a previous snapshot. The kind on the intent token
        // MUST be 'restore' (validator enforces) which the registry tags as
        // Destructive — banner uses the typed-host-name confirmation flow.
        //
        // Two route shapes accepted (both fixed to frontend expectations):
        //   POST /sites/{domain}/restore                       — body { snapshotId, intentToken }
        //   POST /sites/{domain}/snapshots/{snapshotId}/restore — header X-Intent-Token, body { confirm: true }
        // Both lower into the same handler below.
        //
        // This is a dummy that just verifies the snapshot existed; real backend
        // would actually `gunzip + mysql restore` from the path.
        static async Task<IResult> HandleRestoreAsync(
            string domain, string? snapshotIdFromRoute, HttpContext ctx,
            NKS.WebDevConsole.Core.Interfaces.IDeployRunsRepository runs,
            NKS.WebDevConsole.Core.Interfaces.IDeployIntentValidator intentValidator,
            NKS.WebDevConsole.Core.Interfaces.IDeployEventBroadcaster eventsBus,
            CancellationToken ct)
        {
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
            var root = doc.RootElement;
            // snapshotId can come from route path OR body — route wins (more specific).
            var snapshotId = !string.IsNullOrEmpty(snapshotIdFromRoute)
                ? snapshotIdFromRoute
                : (root.TryGetProperty("snapshotId", out var sEl) ? sEl.GetString() : null);
            // Intent token from header X-Intent-Token (frontend convention) OR body field.
            var intentToken = ctx.Request.Headers["X-Intent-Token"].FirstOrDefault();
            if (string.IsNullOrEmpty(intentToken) && root.TryGetProperty("intentToken", out var tEl))
                intentToken = tEl.GetString();
            var host = root.TryGetProperty("host", out var hEl) ? hEl.GetString() ?? "production" : "production";
            if (string.IsNullOrEmpty(snapshotId))
                return Results.BadRequest(new { error = "snapshotId is required" });

            // MCP intent gate — restore requires kind='restore' specifically (NOT
            // kind='deploy'); validator enforces the kind_match check. Caller
            // can pass X-Allow-Unconfirmed for headless flows.
            if (!string.IsNullOrEmpty(intentToken))
            {
                var allowUnconfirmed = string.Equals(
                    ctx.Request.Headers["X-Allow-Unconfirmed"].FirstOrDefault(), "true",
                    StringComparison.OrdinalIgnoreCase);
                var verdict = await intentValidator.ValidateAndConsumeAsync(
                    intentToken, "restore", domain, host, allowUnconfirmed, ct);
                if (!verdict.Ok)
                {
                    return Results.Json(
                        new { error = "intent_rejected", reason = verdict.Reason, detail = verdict.Detail },
                        statusCode: verdict.Reason == "pending_confirmation" ? 425 : 403);
                }
            }

            // Verify the snapshot row exists and actually has a backup path —
            // otherwise the restore would have nothing to restore from.
            var sourceRow = await runs.GetByIdAsync(snapshotId, ct);
            if (sourceRow is null)
                return Results.NotFound(new { error = "snapshot_not_found", snapshotId });
            if (string.IsNullOrEmpty(sourceRow.PreDeployBackupPath))
                return Results.BadRequest(new
                {
                    error = "snapshot_has_no_backup",
                    detail = $"Deploy {snapshotId[..8]} did not capture a pre-deploy snapshot.",
                });

            // Phase 7.5+++ — REAL extract when the backup path resolves to an
            // actual .zip file. Resolves the `~` prefix to the user's home dir.
            // Without a real .zip file (legacy fake snapshot rows), keeps the
            // dummy "verified-only" behaviour.
            var backupPath = sourceRow.PreDeployBackupPath;
            var resolvedBackupPath = backupPath.StartsWith("~/")
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    backupPath[2..].Replace('/', Path.DirectorySeparatorChar))
                : backupPath;

            string? targetPath = null;
            string? extractedTo = null;
            string? swappedTo = null;
            string? restoreError = null;

            if (File.Exists(resolvedBackupPath) && resolvedBackupPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                // Find the host's localTargetPath so we know where to restore.
                try
                {
                    var settingsPath = DeploySettingsPath(domain);
                    if (File.Exists(settingsPath))
                    {
                        using var sdoc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath, ct));
                        if (sdoc.RootElement.TryGetProperty("hosts", out var hostsEl)
                            && hostsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            foreach (var hEl2 in hostsEl.EnumerateArray())
                            {
                                if (!hEl2.TryGetProperty("name", out var nEl)) continue;
                                if (!string.Equals(nEl.GetString(), host, StringComparison.OrdinalIgnoreCase)) continue;
                                if (hEl2.TryGetProperty("localTargetPath", out var ltEl))
                                    targetPath = ltEl.GetString();
                                break;
                            }
                        }
                    }
                }
                catch { /* best-effort */ }

                if (!string.IsNullOrEmpty(targetPath))
                {
                    try
                    {
                        // Extract into a fresh release dir so the restore is auditable
                        // alongside normal deploys (shows up in releases/ + Releases tab).
                        var nowR = DateTimeOffset.UtcNow;
                        var releaseId = nowR.ToString("yyyyMMdd_HHmmss") + "-restored-" + snapshotId[..8];
                        var releaseDir = Path.Combine(targetPath, "releases", releaseId);
                        Directory.CreateDirectory(releaseDir);
                        System.IO.Compression.ZipFile.ExtractToDirectory(
                            resolvedBackupPath, releaseDir, overwriteFiles: true);
                        extractedTo = releaseDir;

                        // Atomic swap of current symlink → new release dir.
                        var currentLink = Path.Combine(targetPath, "current");
                        var depDir = Path.Combine(targetPath, ".dep");
                        Directory.CreateDirectory(depDir);
                        var depCurrent = Path.Combine(depDir, "current_release");
                        var depPrev = Path.Combine(depDir, "previous_release");
                        string? oldCurrent = File.Exists(depCurrent)
                            ? (await File.ReadAllTextAsync(depCurrent, ct)).Trim()
                            : null;
                        if (Directory.Exists(currentLink))
                        {
                            var fi = new DirectoryInfo(currentLink);
                            if (fi.LinkTarget is not null) Directory.Delete(currentLink);
                            else Directory.Delete(currentLink, recursive: true);
                        }
                        Directory.CreateSymbolicLink(currentLink, releaseDir);
                        await File.WriteAllTextAsync(depCurrent, releaseDir, ct);
                        if (!string.IsNullOrEmpty(oldCurrent) && oldCurrent != releaseDir)
                            await File.WriteAllTextAsync(depPrev, oldCurrent, ct);
                        swappedTo = releaseDir;

                        // Audit row in deploy_runs so the operation appears in
                        // history + the Releases sub-tab.
                        var restoreRunId = Guid.NewGuid().ToString("D");
                        await runs.InsertAsync(new NKS.WebDevConsole.Core.Interfaces.DeployRunRow(
                            Id: restoreRunId, Domain: domain, Host: host,
                            ReleaseId: releaseId,
                            Branch: null, CommitSha: null,
                            Status: "completed", IsPastPonr: true,
                            StartedAt: nowR, CompletedAt: DateTimeOffset.UtcNow,
                            ExitCode: 0, ErrorMessage: null, DurationMs: 50,
                            TriggeredBy: "gui", BackendId: "local-restore",
                            CreatedAt: nowR, UpdatedAt: nowR), ct);
                    }
                    catch (Exception ex) { restoreError = ex.Message; }
                }
                else
                {
                    restoreError = "no_local_target_for_host — restore would have nowhere to write";
                }
            }

            // Broadcast the audit event so the GUI's activity feed / drawer
            // sees something happened.
            await eventsBus.BroadcastAsync("restore:complete", new
            {
                domain, snapshotId, host,
                backupPath = sourceRow.PreDeployBackupPath,
                backupSizeBytes = sourceRow.PreDeployBackupSizeBytes ?? 0,
                extractedTo, swappedTo, error = restoreError,
            });

            return Results.Ok(new
            {
                restored = restoreError is null,
                sourceDeployId = snapshotId,
                backupPath = sourceRow.PreDeployBackupPath,
                backupSizeBytes = sourceRow.PreDeployBackupSizeBytes ?? 0,
                extractedTo,
                swappedTo,
                error = restoreError,
            });
        }

        // Both routes alias HandleRestoreAsync.
        //
        // Phase D (#109) — gated by legacyHostHandlersAtBoot. Daemon's restore
        // is pure direct-C# (ZipFile.ExtractToDirectory + symlink swap) — no
        // phar dep — and the plugin ships an equivalent PostSnapshotRestore
        // (NksDeployRoutes.cs) that validates the same intent kind 'restore'
        // against the shared validator. Safe for plugin to take over in
        // plugin-mode boot. Both alias routes flip together for consistency.
        if (legacyHostHandlersAtBoot)
        {
        app.MapPost("/api/nks.wdc.deploy/sites/{domain}/restore",
            (string domain, HttpContext ctx,
             NKS.WebDevConsole.Core.Interfaces.IDeployRunsRepository runs,
             NKS.WebDevConsole.Core.Interfaces.IDeployIntentValidator intentValidator,
             NKS.WebDevConsole.Core.Interfaces.IDeployEventBroadcaster eventsBus,
             CancellationToken ct) =>
                HandleRestoreAsync(domain, null, ctx, runs, intentValidator, eventsBus, ct));

        app.MapPost("/api/nks.wdc.deploy/sites/{domain}/snapshots/{snapshotId}/restore",
            (string domain, string snapshotId, HttpContext ctx,
             NKS.WebDevConsole.Core.Interfaces.IDeployRunsRepository runs,
             NKS.WebDevConsole.Core.Interfaces.IDeployIntentValidator intentValidator,
             NKS.WebDevConsole.Core.Interfaces.IDeployEventBroadcaster eventsBus,
             CancellationToken ct) =>
                HandleRestoreAsync(domain, snapshotId, ctx, runs, intentValidator, eventsBus, ct));
        } // end if (legacyHostHandlersAtBoot) — restore alias block

        // Phase 7.5 dummy backend with realistic state-machine + optional MCP
        // intent gate. POST body:
        //   { "host": "...", "branch": "...", "intentToken": "<id>.<nonce>.<sig>" }
        // If intentToken is provided, validator runs first (kind='deploy' enforced).
        // On success, a background task drives status: queued→running→awaiting_soak
        // →completed and broadcasts deploy events on each transition. Returns
        // immediately with 202 + deployId so the GUI can subscribe to SSE.
        app.MapPost("/api/nks.wdc.deploy/sites/{domain}/deploy", async (
            string domain,
            HttpContext ctx,
            NKS.WebDevConsole.Core.Interfaces.IDeployRunsRepository runs,
            NKS.WebDevConsole.Core.Interfaces.IDeployIntentValidator intentValidator,
            NKS.WebDevConsole.Core.Interfaces.IDeployEventBroadcaster eventsBus,
            NKS.WebDevConsole.Daemon.Deploy.LocalDeployBackend localBackend,
            SettingsStore drSettings,
            CancellationToken ct) =>
        {
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
            var root = doc.RootElement;
            var host = root.TryGetProperty("host", out var hEl) ? hEl.GetString() ?? "production" : "production";
            var branch = root.TryGetProperty("branch", out var bEl) ? bEl.GetString() : null;
            var intentToken = root.TryGetProperty("intentToken", out var tEl) ? tEl.GetString() : null;
            var triggeredBy = string.IsNullOrEmpty(intentToken) ? "gui" : "mcp";

            // Phase 7.5+++ — `localPaths: {source, target}` resolved in priority:
            //   1) Body wins (ad-hoc / E2E override).
            //   2) Fallback to per-host settings on disk so the GUI can dispatch
            //      a deploy with just `host` once the operator has configured
            //      localSourcePath/localTargetPath in the host edit dialog.
            // Real local-loopback backend only — no dummy state machine.
            // Without resolvable paths the deploy endpoint refuses with 400.
            string? localSource = null;
            string? localTarget = null;
            if (root.TryGetProperty("localPaths", out var lpEl) && lpEl.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                if (lpEl.TryGetProperty("source", out var srcEl)) localSource = srcEl.GetString();
                if (lpEl.TryGetProperty("target", out var tgtEl)) localTarget = tgtEl.GetString();
            }

            // Phase 7.5+++ nksdeploy compat — also resolve shared dirs/files +
            // keepReleases retention from settings so the LocalDeployBackend
            // can apply them. Body can override via `localOptions: {...}`.
            List<string>? optSharedDirs = null;
            List<string>? optSharedFiles = null;
            int? optKeepReleases = null;
            if (root.TryGetProperty("localOptions", out var loEl) && loEl.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                if (loEl.TryGetProperty("sharedDirs", out var sdEl) && sdEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                    optSharedDirs = sdEl.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToList();
                if (loEl.TryGetProperty("sharedFiles", out var sfEl) && sfEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                    optSharedFiles = sfEl.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToList();
                if (loEl.TryGetProperty("keepReleases", out var krEl) && krEl.TryGetInt32(out var krVal))
                    optKeepReleases = krVal;
            }

            if (string.IsNullOrEmpty(localSource) || string.IsNullOrEmpty(localTarget)
                || optSharedDirs is null || optSharedFiles is null || optKeepReleases is null)
            {
                // Look up settings JSON to fill in any missing values.
                // File-per-site shape mirrors what the frontend's DeploySettingsPanel writes.
                try
                {
                    var settingsPath = DeploySettingsPath(domain);
                    if (File.Exists(settingsPath))
                    {
                        using var sdoc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath, ct));
                        var rootEl = sdoc.RootElement;
                        if (rootEl.TryGetProperty("hosts", out var hostsEl)
                            && hostsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            foreach (var hEl2 in hostsEl.EnumerateArray())
                            {
                                if (!hEl2.TryGetProperty("name", out var nEl)) continue;
                                if (!string.Equals(nEl.GetString(), host, StringComparison.OrdinalIgnoreCase)) continue;
                                if (string.IsNullOrEmpty(localSource)
                                    && hEl2.TryGetProperty("localSourcePath", out var lsEl))
                                    localSource = lsEl.GetString();
                                if (string.IsNullOrEmpty(localTarget)
                                    && hEl2.TryGetProperty("localTargetPath", out var ltEl))
                                    localTarget = ltEl.GetString();
                                if (optSharedDirs is null && hEl2.TryGetProperty("sharedDirs", out var hsdEl)
                                    && hsdEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                                    optSharedDirs = hsdEl.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToList();
                                if (optSharedFiles is null && hEl2.TryGetProperty("sharedFiles", out var hsfEl)
                                    && hsfEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                                    optSharedFiles = hsfEl.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToList();
                                break;
                            }
                        }
                        // Site-wide retention from advanced.keepReleases when not host-overridden.
                        if (optKeepReleases is null
                            && rootEl.TryGetProperty("advanced", out var advEl)
                            && advEl.TryGetProperty("keepReleases", out var krsEl)
                            && krsEl.TryGetInt32(out var krsVal))
                            optKeepReleases = krsVal;
                    }
                }
                catch { /* swallow — fall through to 400 below if paths still empty */ }
            }

            // Phase 7.5+++ — load hooks + envVars + notifications from settings
            // for the backend. Done in a separate pass so it runs even when
            // localPaths came from body (E2E supplies paths in body but might
            // also rely on settings-defined hooks/notifications).
            var optHooks = new List<NKS.WebDevConsole.Daemon.Deploy.LocalDeployBackend.HookSpec>();
            var optEnvVars = new Dictionary<string, string>();
            NKS.WebDevConsole.Daemon.Deploy.LocalDeployBackend.NotificationsConfig? optNotifications = null;
            string? optHealthCheckUrl = null;
            int optSoakSeconds = 30;
            try
            {
                var settingsPath = DeploySettingsPath(domain);
                if (File.Exists(settingsPath))
                {
                    using var sdoc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath, ct));
                    var rootEl = sdoc.RootElement;
                    // Phase 7.5+++ — pull healthCheckUrl + soakSeconds for the
                    // selected host so the soak phase can probe it after switch.
                    if (rootEl.TryGetProperty("hosts", out var hostsEl3)
                        && hostsEl3.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var hEl3 in hostsEl3.EnumerateArray())
                        {
                            if (!hEl3.TryGetProperty("name", out var nEl3)) continue;
                            if (!string.Equals(nEl3.GetString(), host, StringComparison.OrdinalIgnoreCase)) continue;
                            if (hEl3.TryGetProperty("healthCheckUrl", out var hcEl))
                                optHealthCheckUrl = hcEl.GetString();
                            if (hEl3.TryGetProperty("soakSeconds", out var ssEl) && ssEl.TryGetInt32(out var ssVal))
                                optSoakSeconds = ssVal;
                            break;
                        }
                    }
                    if (rootEl.TryGetProperty("hooks", out var hooksEl)
                        && hooksEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var hkEl in hooksEl.EnumerateArray())
                        {
                            var ev = hkEl.TryGetProperty("event", out var eEl) ? eEl.GetString() ?? "" : "";
                            var ty = hkEl.TryGetProperty("type", out var hkTEl) ? hkTEl.GetString() ?? "shell" : "shell";
                            var cmd = hkEl.TryGetProperty("command", out var cEl) ? cEl.GetString() ?? "" : "";
                            if (string.IsNullOrEmpty(ev) || string.IsNullOrEmpty(cmd)) continue;
                            var to = hkEl.TryGetProperty("timeoutSeconds", out var toEl) && toEl.TryGetInt32(out var toVal) ? toVal : 60;
                            var en = !hkEl.TryGetProperty("enabled", out var enEl)
                                     || enEl.ValueKind != System.Text.Json.JsonValueKind.False; // default true
                            var desc = hkEl.TryGetProperty("description", out var dEl) ? dEl.GetString() : null;
                            optHooks.Add(new NKS.WebDevConsole.Daemon.Deploy.LocalDeployBackend.HookSpec(
                                Event: ev, Type: ty, Command: cmd, TimeoutSeconds: to, Enabled: en, Description: desc));
                        }
                    }
                    if (rootEl.TryGetProperty("advanced", out var advEl2)
                        && advEl2.TryGetProperty("envVars", out var evEl)
                        && evEl.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        foreach (var prop in evEl.EnumerateObject())
                        {
                            if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                                optEnvVars[prop.Name] = prop.Value.GetString() ?? "";
                        }
                    }
                    if (rootEl.TryGetProperty("notifications", out var ntfEl)
                        && ntfEl.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        string? slack = null;
                        List<string>? recipients = null;
                        List<string>? notifyOn = null;
                        if (ntfEl.TryGetProperty("slackWebhook", out var swEl)) slack = swEl.GetString();
                        if (ntfEl.TryGetProperty("emailRecipients", out var erEl)
                            && erEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                            recipients = erEl.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToList();
                        if (ntfEl.TryGetProperty("notifyOn", out var noEl)
                            && noEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                            notifyOn = noEl.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToList();
                        optNotifications = new NKS.WebDevConsole.Daemon.Deploy.LocalDeployBackend.NotificationsConfig(
                            SlackWebhook: slack, EmailRecipients: recipients, NotifyOn: notifyOn);
                    }
                }
            }
            catch { /* best-effort — hooks/envVars/notifications are optional */ }
            if (string.IsNullOrEmpty(localSource) || string.IsNullOrEmpty(localTarget))
            {
                return Results.BadRequest(new
                {
                    error = "localPaths_required",
                    detail = "Provide localPaths: {source, target} in body, or configure localSourcePath + localTargetPath on the host in deploy settings.",
                });
            }

            // Optional MCP gate. When token provided, must be valid + confirmed
            // (or the caller passes X-Allow-Unconfirmed for CI). Plugin-extensible
            // via the kinds registry — if mcp.strict_kinds is on, only registered
            // kinds pass.
            if (!string.IsNullOrEmpty(intentToken))
            {
                var allowUnconfirmed = string.Equals(
                    ctx.Request.Headers["X-Allow-Unconfirmed"].FirstOrDefault(), "true",
                    StringComparison.OrdinalIgnoreCase);
                var verdict = await intentValidator.ValidateAndConsumeAsync(
                    intentToken, "deploy", domain, host, allowUnconfirmed, ct);
                if (!verdict.Ok)
                {
                    return Results.Json(
                        new { error = "intent_rejected", reason = verdict.Reason, detail = verdict.Detail },
                        statusCode: verdict.Reason == "pending_confirmation" ? 425 : 403);
                }
            }

            var deployId = Guid.NewGuid().ToString("D");
            var now = DateTimeOffset.UtcNow;
            var releaseId = now.ToString("yyyyMMdd_HHmmss");

            // Phase 7.5+++ — dry-run preview. Body `dryRun:true` returns the
            // resolved plan WITHOUT inserting deploy_runs row, broadcasting SSE,
            // copying files, or running hooks. Operator uses this to inspect
            // what a deploy WOULD do (which hooks fire, retention impact,
            // shared symlinks to apply) before committing.
            var dryRun = root.TryGetProperty("dryRun", out var drEl)
                         && drEl.ValueKind == System.Text.Json.JsonValueKind.True;
            if (dryRun)
            {
                var existingReleases = Directory.Exists(Path.Combine(localTarget!, "releases"))
                    ? Directory.EnumerateDirectories(Path.Combine(localTarget!, "releases"))
                        .Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList()
                    : new List<string?>();
                var keep = optKeepReleases ?? 5;
                var prunedCount = Math.Max(0, existingReleases.Count - keep + 1); // +1 because the new one will displace
                var prevPath = File.Exists(Path.Combine(localTarget!, ".dep", "previous_release"))
                    ? File.ReadAllText(Path.Combine(localTarget!, ".dep", "previous_release")).Trim()
                    : null;
                // Resolve `current` symlink to the release ID it points at — lets the
                // operator see "this would replace release X" alongside the new ID.
                string? currentRelease = null;
                try
                {
                    var curDir = Path.Combine(localTarget!, "current");
                    if (Directory.Exists(curDir))
                    {
                        var info = new DirectoryInfo(curDir);
                        if (info.LinkTarget is not null)
                            currentRelease = Path.GetFileName(info.LinkTarget);
                    }
                }
                catch { /* best-effort */ }
                // Most-recent mtime in the source tree — operator can spot
                // re-deploys of unchanged source ("Source last changed: 12m ago").
                // Best-effort, top-level scan only to keep dry-run fast on large trees.
                DateTimeOffset? sourceLastModified = null;
                try
                {
                    if (!string.IsNullOrEmpty(localSource) && Directory.Exists(localSource))
                    {
                        var srcInfo = new DirectoryInfo(localSource);
                        var maxTicks = srcInfo.LastWriteTimeUtc.Ticks;
                        foreach (var entry in srcInfo.EnumerateFileSystemInfos())
                        {
                            if (entry.LastWriteTimeUtc.Ticks > maxTicks)
                                maxTicks = entry.LastWriteTimeUtc.Ticks;
                        }
                        sourceLastModified = new DateTimeOffset(maxTicks, TimeSpan.Zero);
                    }
                }
                catch { /* best-effort */ }

                // Compute "would this be a no-op re-deploy?" by comparing source mtime
                // vs the most recent successful deploy for this host. NULL when we
                // can't tell (no source mtime / no prior success). True when source
                // hasn't changed since the last green deploy — operator usually wants
                // to know before committing a redundant re-run.
                bool? sourceUnchangedSinceLastDeploy = null;
                DateTimeOffset? lastSuccessfulDeployAt = null;
                try
                {
                    var recent = await runs.ListForDomainAsync(domain, 50, ct);
                    var lastSuccess = recent
                        .Where(r => string.Equals(r.Host, host, StringComparison.OrdinalIgnoreCase)
                                 && r.Status == "completed")
                        .OrderByDescending(r => r.StartedAt)
                        .FirstOrDefault();
                    if (lastSuccess is not null)
                    {
                        lastSuccessfulDeployAt = lastSuccess.StartedAt;
                        if (sourceLastModified is not null)
                            sourceUnchangedSinceLastDeploy = sourceLastModified <= lastSuccess.StartedAt;
                    }
                }
                catch { /* best-effort */ }
                return Results.Ok(new
                {
                    dryRun = true,
                    deployId = (string?)null,
                    wouldRelease = releaseId,
                    wouldExtractTo = Path.Combine(localTarget!, "releases", releaseId),
                    wouldCopyFrom = localSource,
                    wouldSwapCurrentFrom = prevPath,
                    currentRelease,
                    sourceLastModified,
                    lastSuccessfulDeployAt,
                    sourceUnchangedSinceLastDeploy,
                    branch,
                    sharedDirs = optSharedDirs ?? new List<string> { "log", "temp" },
                    sharedFiles = optSharedFiles ?? new List<string>(),
                    keepReleases = keep,
                    existingReleaseCount = existingReleases.Count,
                    wouldPruneCount = Math.Max(0, prunedCount),
                    hooksWillFire = optHooks
                        .Where(h => h.Enabled)
                        .GroupBy(h => h.Event)
                        .ToDictionary(g => g.Key, g => g.Count()),
                    totalHooksEnabled = optHooks.Count(h => h.Enabled),
                    healthCheckUrl = optHealthCheckUrl,
                    soakSeconds = optSoakSeconds,
                    slackEnabled = !string.IsNullOrEmpty(optNotifications?.SlackWebhook),
                    // Phase 7.5+++ — true when the operator has marked the deploy
                    // kind as always-confirm in settings. GUI preview can warn
                    // "even with a grant you'll see the banner first" before
                    // the operator commits.
                    alwaysConfirmKind = (drSettings.GetString("mcp", "always_confirm_kinds") ?? "")
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Any(k => string.Equals(k, "deploy", StringComparison.OrdinalIgnoreCase)),
                });
            }

            await runs.InsertAsync(new NKS.WebDevConsole.Core.Interfaces.DeployRunRow(
                Id: deployId, Domain: domain, Host: host,
                ReleaseId: releaseId,
                Branch: branch, CommitSha: null,
                Status: "queued", IsPastPonr: false,
                StartedAt: now, CompletedAt: null,
                ExitCode: null, ErrorMessage: null, DurationMs: null,
                TriggeredBy: triggeredBy,
                BackendId: "local",
                CreatedAt: now, UpdatedAt: now), ct);

            // Phase 7.5+++ — body `snapshot: true` OR `snapshot: { include: true }`
            // (the latter from the GUI) triggers a REAL pre-deploy snapshot when
            // localTarget/current resolves to a real dir. Without a current dir
            // (first deploy ever), records a placeholder so the row still has a
            // backupPath for audit consistency.
            bool snapshotRequested = false;
            if (root.TryGetProperty("snapshot", out var sEl))
            {
                if (sEl.ValueKind == System.Text.Json.JsonValueKind.True)
                    snapshotRequested = true;
                else if (sEl.ValueKind == System.Text.Json.JsonValueKind.Object
                         && sEl.TryGetProperty("include", out var incEl)
                         && incEl.ValueKind == System.Text.Json.JsonValueKind.True)
                    snapshotRequested = true;
            }
            if (snapshotRequested)
            {
                var currentDir = Path.Combine(localTarget!, "current");
                if (Directory.Exists(currentDir))
                {
                    // Resolve symlink so we zip real contents (not link metadata).
                    var realRoot = currentDir;
                    try
                    {
                        var info = new DirectoryInfo(currentDir);
                        if (info.LinkTarget is not null && Directory.Exists(info.LinkTarget))
                            realRoot = info.LinkTarget;
                    }
                    catch { /* fall back to currentDir */ }

                    var preDir = Path.Combine(NKS.WebDevConsole.Core.Services.WdcPaths.BackupsRoot,
                        "pre-deploy", domain);
                    Directory.CreateDirectory(preDir);
                    var realPath = Path.Combine(preDir, $"{deployId}.zip");
                    try
                    {
                        System.IO.Compression.ZipFile.CreateFromDirectory(
                            realRoot, realPath,
                            System.IO.Compression.CompressionLevel.Fastest,
                            includeBaseDirectory: false);
                        var size = new FileInfo(realPath).Length;
                        await runs.UpdatePreDeployBackupAsync(deployId,
                            $"~/.wdc/backups/pre-deploy/{domain}/{deployId}.zip", size, ct);

                        // Phase 7.5+++ — retention prune. Default 30 days when
                        // settings missing (matches defaultDeploySettings()).
                        var rd = ReadSnapshotRetentionDays(domain) ?? 30;
                        PurgeOldSnapshots("pre-deploy", domain, rd);
                    }
                    catch
                    {
                        // Don't block the deploy if the snapshot fails — log a
                        // placeholder so audit shows the attempt + the failure
                        // is visible in deploy logs.
                        await runs.UpdatePreDeployBackupAsync(deployId,
                            $"~/.wdc/backups/pre-deploy/{domain}/{deployId}.zip.failed", 0, ct);
                    }
                }
                else
                {
                    // No prior deploy — placeholder for audit symmetry.
                    await runs.UpdatePreDeployBackupAsync(deployId,
                        $"~/.wdc/backups/pre-deploy/{domain}/{deployId}.empty", 0, ct);
                }
            }

            await eventsBus.BroadcastAsync("deploy:started",
                new { deployId, domain, host, triggeredBy, backend = "local" });

            // REAL local-loopback deploy. Background fire-and-forget — HTTP returns
            // 202 immediately, the backend writes status updates and SSE events as
            // it progresses through copy + symlink phases.
            var deployOptions = new NKS.WebDevConsole.Daemon.Deploy.LocalDeployBackend.Options(
                SharedDirs: optSharedDirs,
                SharedFiles: optSharedFiles,
                KeepReleases: optKeepReleases,
                Hooks: optHooks.Count > 0 ? optHooks : null,
                EnvVars: optEnvVars.Count > 0 ? optEnvVars : null,
                Notifications: optNotifications,
                Domain: domain,
                Host: host,
                HealthCheckUrl: optHealthCheckUrl,
                SoakSeconds: optSoakSeconds);
            _ = Task.Run(() => localBackend.RunAsync(deployId, releaseId, localSource!, localTarget!, deployOptions));
            return Results.Accepted($"/api/nks.wdc.deploy/sites/{domain}/deploys/{deployId}",
                new { deployId, status = "queued", note = "local backend — copying files" });
        });

        // Phase 7.5 — phase mapping moved to DeployRestHelpers for testability.
        static string MapStatusToPhase(string status) =>
            NKS.WebDevConsole.Daemon.Deploy.DeployRestHelpers.MapStatusToPhase(status);
    }
}
