using System.Text.RegularExpressions;

namespace NKS.WebDevConsole.Daemon.Apache;

/// <summary>
/// Phase 6.22 — pure helper extracted from the boot-heal sweep in
/// Program.cs ApplicationStarted hook. Scans Apache's sites-enabled
/// directory for per-site vhost configs whose <c>&lt;VirtualHost *:PORT&gt;</c>
/// directive references a port outside the current settings ports.
///
/// Returns the list of file basenames (e.g. <c>blog.loc.conf</c>) that
/// need regeneration. Empty list = no stale ports (clean install or
/// already-healed state). Caller is responsible for the actual
/// regenerate-each-site + Apache reload work.
///
/// Pure function: no I/O outside the directory enumeration the caller
/// passes in, no time, no network. Keeps the boot-heal sweep testable
/// without spinning the daemon's full ApplicationStarted hook. Pattern
/// mirrors <c>IntentSweeperService.SweepOrphanSnapshotFiles</c>.
/// </summary>
public static class VhostStalePortScanner
{
    /// <summary>
    /// Compiled regex matching <c>&lt;VirtualHost *:PORT&gt;</c> with
    /// optional whitespace, case-insensitive. Captures the port digits.
    /// </summary>
    private static readonly Regex PortRegex = new(
        @"<VirtualHost\s+\*:(\d+)\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Scan <paramref name="sitesEnabledDir"/> for *.conf files whose
    /// VirtualHost port directives reference any port NOT in the
    /// <paramref name="acceptablePorts"/> set. Returns the basenames of
    /// stale files; empty when the directory doesn't exist OR every
    /// VirtualHost line matches an acceptable port.
    ///
    /// A single .conf may declare BOTH HTTP + HTTPS VirtualHosts; if
    /// either references a stale port the file is reported once. Files
    /// with no <c>&lt;VirtualHost ...&gt;</c> match at all (e.g. only
    /// includes / global directives) are silently ignored.
    /// </summary>
    public static IReadOnlyList<string> FindStaleFiles(
        string sitesEnabledDir,
        IReadOnlySet<int> acceptablePorts)
    {
        if (!Directory.Exists(sitesEnabledDir)) return Array.Empty<string>();
        if (acceptablePorts.Count == 0) return Array.Empty<string>();

        var stale = new List<string>();
        foreach (var path in Directory.EnumerateFiles(sitesEnabledDir, "*.conf"))
        {
            string content;
            try { content = File.ReadAllText(path); }
            catch { continue; /* unreadable — skip; next sweep retries */ }

            var matches = PortRegex.Matches(content);
            foreach (Match m in matches)
            {
                if (!int.TryParse(m.Groups[1].Value, out var port)) continue;
                if (!acceptablePorts.Contains(port))
                {
                    stale.Add(Path.GetFileName(path));
                    break; // one report per file is enough
                }
            }
        }
        return stale;
    }
}
