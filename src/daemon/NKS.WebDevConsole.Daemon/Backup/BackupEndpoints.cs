using System.Text.Json;
using System.Net;
using System.Net.NetworkInformation;
using Dapper;
using NKS.WebDevConsole.Daemon.Plugin;
using NKS.WebDevConsole.Daemon.Services;
using NKS.WebDevConsole.Daemon.Sites;
using NKS.WebDevConsole.Daemon.Binaries;
using NKS.WebDevConsole.Daemon.Backup;
using NKS.WebDevConsole.Daemon.Config;
using NKS.WebDevConsole.Daemon.Data;
using NKS.WebDevConsole.Daemon.Mcp;
using NKS.WebDevConsole.Daemon.Cloudflare;
using NKS.WebDevConsole.Daemon.Ssl;
using NKS.WebDevConsole.Daemon.Hosts;
using NKS.WebDevConsole.Daemon.Deploy;
using CliWrap.Buffered;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Core.Services;

namespace NKS.WebDevConsole.Daemon.Backup;

/// <summary>
/// Backup list/create/delete/download and restore routes.
/// Moved verbatim out of Program.cs; registered from there at the same
/// point in the pipeline so route order is unchanged.
/// </summary>
internal static class BackupEndpoints
{
    public static void MapBackupEndpoints(this WebApplication app, System.Text.Json.JsonSerializerOptions caseInsensitiveJson)
    {
        // ── Backup helper functions ───────────────────────────────────────────────

        // A failed broadcast must never turn a finished backup into an error response.
        async Task BroadcastBackupEventAsync(SseService sse, string eventType, object payload)
        {
            try { await sse.BroadcastAsync(eventType, payload); }
            catch { /* SSE is best-effort */ }
        }

        BackupContentFlags ReadContentFlagsFromSettings(SettingsStore s)
        {
            var flags = BackupContentFlags.None;
            if (s.GetBool("backup", "content.vhosts",      defaultValue: true))  flags |= BackupContentFlags.Vhosts;
            if (s.GetBool("backup", "content.pluginConfigs",defaultValue: true))  flags |= BackupContentFlags.PluginConfigs;
            if (s.GetBool("backup", "content.ssl",          defaultValue: true))  flags |= BackupContentFlags.Ssl;
            if (s.GetBool("backup", "content.databases",    defaultValue: false)) flags |= BackupContentFlags.Databases;
            if (s.GetBool("backup", "content.docroots",     defaultValue: false)) flags |= BackupContentFlags.Docroots;
            return flags == BackupContentFlags.None ? BackupContentFlags.Default : flags;
        }

        BackupContentFlags ParseContentFlagsFromDict(Dictionary<string, bool> dict)
        {
            var flags = BackupContentFlags.None;
            if (dict.GetValueOrDefault("vhosts",       true))  flags |= BackupContentFlags.Vhosts;
            if (dict.GetValueOrDefault("pluginConfigs", true))  flags |= BackupContentFlags.PluginConfigs;
            if (dict.GetValueOrDefault("ssl",           true))  flags |= BackupContentFlags.Ssl;
            if (dict.GetValueOrDefault("databases",     false)) flags |= BackupContentFlags.Databases;
            if (dict.GetValueOrDefault("docroots",      false)) flags |= BackupContentFlags.Docroots;
            return flags == BackupContentFlags.None ? BackupContentFlags.Default : flags;
        }

        // Backup / restore — tasks 14/22/35.
        // Content-flag-aware backup: vhosts, SSL, plugin-configs default ON;
        // databases and docroots are opt-in.
        app.MapGet("/api/backup/list", (BackupManager bm) =>
        {
            var list = bm.ListBackups()
                .Select(b => new { path = b.Path, size = b.Size, createdUtc = b.Created, contentFlags = b.ContentFlags.ToString() })
                .ToList();
            return Results.Ok(new { count = list.Count, backups = list });
        });

        app.MapGet("/api/backup/stats", (BackupManager bm) =>
        {
            var backups = bm.ListBackups();
            var totalSize = backups.Sum(b => b.Size);
            var lastCreated = backups.Count > 0 ? backups[0].Created : (DateTime?)null;
            return Results.Ok(new { count = backups.Count, totalSize, lastCreatedUtc = lastCreated });
        });

        app.MapPost("/api/backup", async (BackupManager bm, SettingsStore settings, SseService sse, HttpContext ctx) =>
        {
            try
            {
                var outPath = ctx.Request.Query["out"].FirstOrDefault();
                BackupContentFlags flags = BackupContentFlags.Default;

                // Body may contain explicit contentFlags override
                if (ctx.Request.ContentLength > 0 && ctx.Request.ContentType?.Contains("json") == true)
                {
                    try
                    {
                        var body = await ctx.Request.ReadFromJsonAsync<BackupRequestBody>(caseInsensitiveJson);
                        if (body?.ContentFlags is not null)
                            flags = ParseContentFlagsFromDict(body.ContentFlags);
                        else
                            flags = ReadContentFlagsFromSettings(settings);
                    }
                    catch { flags = ReadContentFlagsFromSettings(settings); }
                }
                else
                {
                    flags = ReadContentFlagsFromSettings(settings);
                }

                var (path, files, size, resultFlags) = bm.CreateBackup(outPath, flags);
                // backup:completed / backup:failed — the desktop app shows an OS
                // notification (backup channel); same payload as BackupScheduler.
                await BroadcastBackupEventAsync(sse, "backup:completed", new
                {
                    trigger = "manual", path, files, size, contentFlags = resultFlags.ToString(),
                });
                return Results.Ok(new { path, files, size, contentFlags = resultFlags.ToString() });
            }
            catch (Exception ex)
            {
                await BroadcastBackupEventAsync(sse, "backup:failed", new { trigger = "manual", error = ex.Message });
                return Results.Problem($"Backup failed: {ex.Message}");
            }
        });

        app.MapDelete("/api/backup/{id}", (BackupManager bm, string id) =>
        {
            var backupRoot = Path.GetFullPath(NKS.WebDevConsole.Core.Services.WdcPaths.BackupsRoot);
            var resolved = Path.GetFullPath(Path.Combine(backupRoot, id));
            if (!PathContainment.IsUnder(backupRoot, resolved))
                return Results.BadRequest(new { error = "Path escapes backup root" });
            if (!File.Exists(resolved)) return Results.NotFound(new { error = "Backup not found" });
            try { File.Delete(resolved); return Results.Ok(new { deleted = id }); }
            catch (Exception ex) { return Results.Problem($"Delete failed: {ex.Message}"); }
        });

        app.MapGet("/api/backup/download", (BackupManager bm, string? path) =>
        {
            string target;
            if (string.IsNullOrEmpty(path))
            {
                var backupsList = bm.ListBackups();
                if (backupsList.Count == 0) return Results.NotFound(new { error = "No backups available" });
                var latest = backupsList[0];
                target = latest.Path;
            }
            else
            {
                var backupRoot = Path.GetFullPath(NKS.WebDevConsole.Core.Services.WdcPaths.BackupsRoot);
                var resolved = Path.GetFullPath(path);
                if (!PathContainment.IsUnder(backupRoot, resolved))
                    return Results.BadRequest(new { error = "Path escapes backup root" });
                target = resolved;
            }
            if (!File.Exists(target)) return Results.NotFound(new { error = "Backup file not found" });
            return Results.File(target, "application/zip", Path.GetFileName(target));
        });

        app.MapPost("/api/restore", async (BackupManager bm, HttpContext ctx,
            NKS.WebDevConsole.Core.Interfaces.IDeployIntentValidator intentValidator) =>
        {
            // Restoring replaces state.db, sites and certificates. GUI callers keep the
            // existing confirm dialog; an MCP-token caller needs a confirmed intent.
            var brIntentToken = ctx.Request.Headers["X-Intent-Token"].FirstOrDefault();
            if (!string.IsNullOrEmpty(brIntentToken) || McpCallerContext.IsMcpToken)
            {
                var brVerdict = await intentValidator.ValidateAndConsumeAsync(
                    brIntentToken ?? "", "backup_restore", domain: "*backup*", host: "*backup*", allowUnconfirmed: false, ctx.RequestAborted);
                if (!brVerdict.Ok)
                    return Results.Json(
                        new { error = "intent_rejected", reason = brVerdict.Reason, detail = brVerdict.Detail },
                        statusCode: brVerdict.Reason == "pending_confirmation" ? 425 : 403);
            }
            // An uploaded archive holds state.db and the sites' TLS private keys. It
            // used to stay in the shared temp dir with default permissions forever;
            // write it owner-only and delete it once the restore is done.
            string? uploadedTmp = null;
            try
            {
                string archivePath;
                if (ctx.Request.HasFormContentType)
                {
                    var form = await ctx.Request.ReadFormAsync();
                    var file = form.Files.FirstOrDefault();
                    if (file is null) return Results.BadRequest(new { error = "No file uploaded" });
                    uploadedTmp = Path.Combine(Path.GetTempPath(), $"wdc-restore-{Guid.NewGuid():N}.zip");
                    var tmpOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
                    if (!OperatingSystem.IsWindows())
                        tmpOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                    using (var fs = new FileStream(uploadedTmp, tmpOptions))
                        await file.CopyToAsync(fs);
                    archivePath = uploadedTmp;
                }
                else
                {
                    var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>();
                    var requested = body?.GetValueOrDefault("path");
                    if (string.IsNullOrWhiteSpace(requested))
                        return Results.BadRequest(new { error = "path or multipart file required" });
                    var backupRoot = Path.GetFullPath(NKS.WebDevConsole.Core.Services.WdcPaths.BackupsRoot);
                    var resolved = Path.GetFullPath(requested);
                    if (!PathContainment.IsUnder(backupRoot, resolved))
                        return Results.BadRequest(new { error = "Path escapes backup root" });
                    archivePath = resolved;
                }

                var (restored, safety) = bm.RestoreBackup(archivePath);
                return Results.Ok(new { restored, safetyBackup = safety, archive = uploadedTmp is null ? archivePath : null });
            }
            catch (System.Text.Json.JsonException ex)
            {
                return Results.BadRequest(new { error = $"Invalid JSON body: {ex.Message}" });
            }
            catch (FileNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (Exception ex) { return Results.Problem($"Restore failed: {ex.Message}"); }
            finally
            {
                if (uploadedTmp is not null)
                {
                    try { File.Delete(uploadedTmp); } catch { /* best-effort; temp cleaner will catch it */ }
                }
            }
        });

        app.MapPost("/api/backup/{id}/restore", async (BackupManager bm, string id, HttpContext ctx,
            NKS.WebDevConsole.Core.Interfaces.IDeployIntentValidator intentValidator) =>
        {
            // Restoring replaces state.db, sites and certificates. GUI callers keep the
            // existing confirm dialog; an MCP-token caller needs a confirmed intent.
            var brIntentToken = ctx.Request.Headers["X-Intent-Token"].FirstOrDefault();
            if (!string.IsNullOrEmpty(brIntentToken) || McpCallerContext.IsMcpToken)
            {
                var brVerdict = await intentValidator.ValidateAndConsumeAsync(
                    brIntentToken ?? "", "backup_restore", domain: "*backup*", host: "*backup*", allowUnconfirmed: false, ctx.RequestAborted);
                if (!brVerdict.Ok)
                    return Results.Json(
                        new { error = "intent_rejected", reason = brVerdict.Reason, detail = brVerdict.Detail },
                        statusCode: brVerdict.Reason == "pending_confirmation" ? 425 : 403);
            }
            var backupRoot = Path.GetFullPath(NKS.WebDevConsole.Core.Services.WdcPaths.BackupsRoot);
            var resolved = Path.GetFullPath(Path.Combine(backupRoot, id));
            if (!PathContainment.IsUnder(backupRoot, resolved))
                return Results.BadRequest(new { error = "Path escapes backup root" });
            if (!File.Exists(resolved)) return Results.NotFound(new { error = "Backup not found" });
            try
            {
                var (restored, safety) = bm.RestoreBackup(resolved);
                return Results.Ok(new { restored, safetyBackup = safety, archive = id });
            }
            catch (Exception ex) { return Results.Problem($"Restore failed: {ex.Message}"); }
        });
    }
}
