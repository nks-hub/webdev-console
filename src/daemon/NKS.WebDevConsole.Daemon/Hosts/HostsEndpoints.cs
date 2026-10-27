using NKS.WebDevConsole.Daemon.Sites;

namespace NKS.WebDevConsole.Daemon.Hosts;

/// <summary>
/// Route registrations for the /api/hosts surface, plus the hosts-file
/// parsing and elevation helpers they own.
///
/// Lifted verbatim out of Program.cs, which carried all 191 endpoints inline.
/// </summary>
internal static class HostsEndpoints
{
    // UTF-8 without BOM: ASCII turned any non-ASCII text in the user's own
    // lines (e.g. Czech comments) into '?', and a BOM can break the first line.
    private static readonly System.Text.Encoding HostsEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static void MapHostsEndpoints(this WebApplication app)
    {
        static string GetHostsPath() =>
            OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts")
                : "/etc/hosts";

        static bool IsElevated()
        {
            if (!OperatingSystem.IsWindows()) return true;
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }

        static bool IsIpValid(string ip)
        {
            return System.Net.IPAddress.TryParse(ip.Trim(), out _);
        }

        static bool IsHostnameValid(string h)
        {
            if (string.IsNullOrWhiteSpace(h)) return false;
            h = h.Trim();
            if (h.Length > 253) return false;
            return System.Text.RegularExpressions.Regex.IsMatch(
                h, @"^[a-zA-Z0-9]([a-zA-Z0-9\-]{0,61}[a-zA-Z0-9])?(\.[a-zA-Z0-9]([a-zA-Z0-9\-]{0,61}[a-zA-Z0-9])?)*$");
        }

        /// <summary>Parses all lines of the hosts file into structured entries.</summary>
        // F82 helper: detect IP-like tokens so ParseHostsEntries can skip
        // comment headers (`## MAMP PRO hosts`) that would otherwise be
        // parsed as ip="MAMP" hostname="PRO".
        static bool LooksLikeIp(string token)
        {
            if (string.IsNullOrEmpty(token)) return false;
            // IPv6 has at least one ':' and no spaces.
            if (token.Contains(':')) return true;
            // IPv4 dotted-quad: 4 dot-separated decimal octets 0-255.
            var octets = token.Split('.');
            if (octets.Length != 4) return false;
            foreach (var o in octets)
            {
                if (!byte.TryParse(o, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out _))
                    return false;
            }
            return true;
        }

        static List<HostsEntryDto> ParseHostsEntries(string content, HashSet<string> wdcDomains)
        {
            const string beginMarker = "# BEGIN NKS WebDev Console";
            const string endMarker   = "# END NKS WebDev Console";

            // Well-known external entries that ship with Windows
            var windowsDefaults = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "localhost", "ip6-localhost", "ip6-loopback", "broadcasthost",
            };
            var windowsDefaultIps = new HashSet<string> { "127.0.0.1", "::1", "255.255.255.255", "fe80::1%lo0" };

            var entries = new List<HostsEntryDto>();
            var lines = content.Split('\n');
            bool inWdc = false;

            for (int i = 0; i < lines.Length; i++)
            {
                var raw = lines[i].TrimEnd('\r');
                var trimmed = raw.Trim();

                if (trimmed == beginMarker) { inWdc = true; continue; }
                if (trimmed == endMarker)   { inWdc = false; continue; }

                // Skip fully-empty lines
                if (trimmed.Length == 0) continue;

                bool enabled = !trimmed.StartsWith('#');
                var working = enabled ? trimmed : trimmed.TrimStart('#').Trim();

                // Extract inline comment
                string? comment = null;
                var commentIdx = working.IndexOf('#');
                if (commentIdx >= 0)
                {
                    comment = working[(commentIdx + 1)..].Trim();
                    if (comment.Length == 0) comment = null;
                    working = working[..commentIdx].Trim();
                }

                var parts = working.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;

                var ip = parts[0];
                // F82: reject lines whose first token doesn't look like an IP. MAMP /
                // XAMPP dump section headers like `## MAMP PRO hosts` into the file —
                // after strip-# + trim the working content becomes `MAMP PRO hosts`
                // and the old parser happily created an entry with ip="MAMP"
                // hostname="PRO", which is the "bordel" showing up in the UI table.
                // Treat such lines as pure comments (skip them). IPv4 dotted-quad OR
                // any token with a colon (IPv6) is accepted; everything else is junk.
                if (!LooksLikeIp(ip)) continue;

                for (int j = 1; j < parts.Length; j++)
                {
                    var hostname = parts[j];
                    string source;
                    if (inWdc)
                        source = "wdc";
                    else if (windowsDefaults.Contains(hostname) || windowsDefaultIps.Contains(ip))
                        source = "external";
                    else
                        source = "custom";

                    entries.Add(new HostsEntryDto(
                        Enabled:    enabled,
                        Ip:         ip,
                        Hostname:   hostname,
                        Source:     source,
                        Comment:    comment,
                        LineNumber: i + 1));
                }
            }
            return entries;
        }

        app.MapGet("/api/hosts", (SiteManager sm) =>
        {
            var hostsPath = GetHostsPath();
            if (!File.Exists(hostsPath))
                return Results.Problem("Hosts file not found");

            try
            {
                var content = File.ReadAllText(hostsPath);
                var wdcDomains = sm.Sites.Values
                    .SelectMany(s => new[] { s.Domain }.Concat(s.Aliases ?? []))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var entries = ParseHostsEntries(content, wdcDomains);
                return Results.Ok(entries);
            }
            catch (Exception ex)
            {
                return Results.Problem($"Cannot read hosts file: {ex.Message}");
            }
        });

        app.MapPost("/api/hosts/apply", async (HttpContext ctx) =>
        {
            if (!IsElevated())
                return Results.Json(new { error = "Requires administrator — restart WDC as admin" }, statusCode: 403);

            HostsApplyRequest? req;
            try { req = await ctx.Request.ReadFromJsonAsync<HostsApplyRequest>(); }
            catch { return Results.BadRequest(new { error = "Invalid JSON body" }); }

            if (req?.Entries is null)
                return Results.BadRequest(new { error = "Missing entries array" });

            foreach (var e in req.Entries)
            {
                if (!IsIpValid(e.Ip))
                    return Results.BadRequest(new { error = $"Invalid IP: {e.Ip}" });
                if (!IsHostnameValid(e.Hostname))
                    return Results.BadRequest(new { error = $"Invalid hostname: {e.Hostname}" });
                // A CR/LF (or another control char) in a comment would end the line
                // and let the request append arbitrary entries to the hosts file.
                // TAB is allowed: GET returns comments with inner tabs verbatim.
                if (e.Comment is not null && e.Comment.Any(c => c != '\t' && char.IsControl(c)))
                    return Results.BadRequest(new { error = $"Invalid comment for {e.Hostname}" });
            }

            var hostsPath = GetHostsPath();
            const string beginMarker = "# BEGIN NKS WebDev Console";
            const string endMarker   = "# END NKS WebDev Console";

            try
            {
                var content = await File.ReadAllTextAsync(hostsPath);

                // Build new managed block from wdc entries
                var wdcEntries = req.Entries.Where(e => e.Source == "wdc").ToList();
                var wdcLines = new List<string> { beginMarker };
                foreach (var e in wdcEntries)
                {
                    var prefix = e.Enabled ? "" : "# ";
                    var comment = string.IsNullOrWhiteSpace(e.Comment) ? "" : $" # {e.Comment.Trim()}";
                    wdcLines.Add($"{prefix}{e.Ip}\t{e.Hostname}{comment}");
                }
                wdcLines.Add(endMarker);

                // Entries outside the managed block belong to the user's own part of
                // the file. The request's custom rows used to be built here and then
                // dropped, so "Add entry" reported success without touching the file.
                // Append rows that are not in the file yet; edits and removals of
                // existing lines are still left as they are and reported back.
                static string Key(string ip, string host) => $"{ip.Trim()}\u0000{host.Trim()}".ToLowerInvariant();
                var existingOutside = ParseHostsEntries(content, new HashSet<string>(StringComparer.OrdinalIgnoreCase))
                    .Where(x => x.Source != "wdc")
                    .ToList();
                var existingKeys = existingOutside.Select(x => Key(x.Ip, x.Hostname)).ToHashSet();
                var requested = req.Entries.Where(e => e.Source != "wdc").ToList();
                var addedLines = new List<string>();
                foreach (var e in requested.Where(q => !existingKeys.Contains(Key(q.Ip, q.Hostname))))
                {
                    var prefix = e.Enabled ? "" : "# ";
                    var comment = string.IsNullOrWhiteSpace(e.Comment) ? "" : $" # {e.Comment.Trim()}";
                    addedLines.Add($"{prefix}{e.Ip.Trim()}\t{e.Hostname.Trim()}{comment}");
                    existingKeys.Add(Key(e.Ip, e.Hostname));
                }
                var requestedByKey = requested
                    .GroupBy(e => Key(e.Ip, e.Hostname))
                    .ToDictionary(g => g.Key, g => g.First());
                var notApplied = existingOutside.Count(x =>
                    !requestedByKey.TryGetValue(Key(x.Ip, x.Hostname), out var r)
                    || r.Enabled != x.Enabled
                    || !string.Equals((r.Comment ?? "").Trim(), x.Comment ?? "", StringComparison.Ordinal));

                // Rebuild: strip existing managed block, replace with new one, keep external
                // Strategy: strip begin..end block, inject new block, rebuild non-wdc lines
                var beginIdx = content.IndexOf(beginMarker, StringComparison.Ordinal);
                var endIdx = content.IndexOf(endMarker, StringComparison.Ordinal);

                string before, after;
                if (beginIdx >= 0 && endIdx >= beginIdx)
                {
                    var endOfEnd = endIdx + endMarker.Length;
                    if (endOfEnd < content.Length && content[endOfEnd] == '\r') endOfEnd++;
                    if (endOfEnd < content.Length && content[endOfEnd] == '\n') endOfEnd++;
                    before = content[..beginIdx].TrimEnd();
                    after = content[endOfEnd..].TrimStart();
                }
                else
                {
                    before = content.TrimEnd();
                    after = string.Empty;
                }

                if (addedLines.Count > 0)
                {
                    var added = string.Join(Environment.NewLine, addedLines);
                    before = before.Length > 0 ? before + Environment.NewLine + added : added;
                }

                var sb = new System.Text.StringBuilder();
                if (before.Length > 0) sb.AppendLine(before).AppendLine();
                sb.AppendLine(string.Join(Environment.NewLine, wdcLines));
                if (after.Length > 0) sb.AppendLine().Append(after);

                var newContent = sb.ToString();

                // Safety check
                if (newContent.Length < content.Length / 4 && content.Length > 200)
                    return Results.Problem("Safety abort: new content suspiciously small");

                // Backup rotation (keep last 5)
                for (int i = 4; i >= 1; i--)
                {
                    var src = $"{hostsPath}.wdc-backup.{i}";
                    var dst = $"{hostsPath}.wdc-backup.{i + 1}";
                    if (File.Exists(src)) File.Move(src, dst, overwrite: true);
                }
                File.Copy(hostsPath, $"{hostsPath}.wdc-backup.1", overwrite: true);

                await File.WriteAllTextAsync(hostsPath, newContent, HostsEncoding);

                try { using var p = System.Diagnostics.Process.Start("ipconfig", "/flushdns"); p?.WaitForExit(5000); } catch { }

                return Results.Ok(new { applied = true, entryCount = req.Entries.Count, added = addedLines.Count, notApplied });
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Json(new { error = "Requires administrator — restart WDC as admin" }, statusCode: 403);
            }
            catch (Exception ex)
            {
                return Results.Problem($"Failed to apply hosts: {ex.Message}");
            }
        });

        app.MapPost("/api/hosts/backup", () =>
        {
            var hostsPath = GetHostsPath();
            if (!File.Exists(hostsPath))
                return Results.NotFound(new { error = "Hosts file not found" });

            var backupsDir = Path.Combine(NKS.WebDevConsole.Core.Services.WdcPaths.BackupsRoot, "hosts");
            Directory.CreateDirectory(backupsDir);

            var ts = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ");
            var destPath = Path.Combine(backupsDir, $"hosts-{ts}.bak");

            try
            {
                File.Copy(hostsPath, destPath, overwrite: false);
                return Results.Ok(new { path = destPath, timestamp = ts });
            }
            catch (Exception ex)
            {
                return Results.Problem($"Backup failed: {ex.Message}");
            }
        });

        app.MapPost("/api/hosts/restore", async (HttpContext ctx) =>
        {
            if (!IsElevated())
                return Results.Json(new { error = "Requires administrator — restart WDC as admin" }, statusCode: 403);

            HostsRestoreRequest? req;
            try { req = await ctx.Request.ReadFromJsonAsync<HostsRestoreRequest>(); }
            catch { return Results.BadRequest(new { error = "Invalid JSON body" }); }

            if (req is null || (string.IsNullOrWhiteSpace(req.Path) && string.IsNullOrWhiteSpace(req.Content)))
                return Results.BadRequest(new { error = "Provide path or content" });

            // F58: Clamp content size to 10 MiB — defense-in-depth against malicious
            // or accidental oversized upload. A real hosts file is never more than a
            // few hundred KiB; anything north of 10 MiB is a bug or abuse.
            const int MaxHostsBytes = 10 * 1024 * 1024;

            var hostsPath = GetHostsPath();
            try
            {
                string newContent;
                if (!string.IsNullOrWhiteSpace(req.Path))
                {
                    if (!File.Exists(req.Path))
                        return Results.NotFound(new { error = $"Backup file not found: {req.Path}" });
                    var fi = new FileInfo(req.Path);
                    if (fi.Length > MaxHostsBytes)
                        return Results.BadRequest(new { error = $"Backup file too large ({fi.Length} bytes, max {MaxHostsBytes})" });
                    newContent = await File.ReadAllTextAsync(req.Path);
                }
                else
                {
                    if (req.Content!.Length > MaxHostsBytes)
                        return Results.BadRequest(new { error = $"Content too large ({req.Content.Length} chars, max {MaxHostsBytes})" });
                    newContent = req.Content!;
                }

                // Safety: backup current before restore
                for (int i = 4; i >= 1; i--)
                {
                    var src = $"{hostsPath}.wdc-backup.{i}";
                    var dst = $"{hostsPath}.wdc-backup.{i + 1}";
                    if (File.Exists(src)) File.Move(src, dst, overwrite: true);
                }
                if (File.Exists(hostsPath))
                    File.Copy(hostsPath, $"{hostsPath}.wdc-backup.1", overwrite: true);

                await File.WriteAllTextAsync(hostsPath, newContent, HostsEncoding);
                try { using var p = System.Diagnostics.Process.Start("ipconfig", "/flushdns"); p?.WaitForExit(5000); } catch { }

                return Results.Ok(new { restored = true });
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Json(new { error = "Requires administrator — restart WDC as admin" }, statusCode: 403);
            }
            catch (Exception ex)
            {
                return Results.Problem($"Restore failed: {ex.Message}");
            }
        });
    }
}
