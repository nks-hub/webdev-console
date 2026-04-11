using System.Diagnostics;
using System.Net.NetworkInformation;

namespace NKS.WebDevConsole.Core.Services;

/// <summary>
/// Per SPEC §9 Port Conflict Detection: before binding any port, check
/// availability. If in use, identify the owner process name and PID, and
/// suggest a fallback port. Service modules call this BEFORE spawning their
/// child process so the user gets a diagnostically-useful error instead of a
/// cryptic "port already in use" from httpd/mysqld at startup.
///
/// Works by enumerating <see cref="IPGlobalProperties.GetActiveTcpListeners"/>
/// (native API, no external tooling needed) and then matching the reported
/// IPEndPoints against running processes via an OS-specific strategy:
///   - Windows: parse <c>netstat -ano</c> output. The managed API does NOT
///     expose a PID mapping, and <c>GetExtendedTcpTable</c> requires P/Invoke
///     into iphlpapi.dll. netstat is unambiguous, ships with every Windows
///     install, and is cheap enough to run on demand (once per start).
///   - Linux: parse <c>/proc/net/tcp</c> + walk <c>/proc/*/fd</c> socket
///     inodes. Not implemented yet — falls back to returning "unknown owner"
///     when detection can't determine PID (caller still gets "port in use"
///     fact which is enough to present a port conflict error).
///
/// Returns a <see cref="PortConflictInfo"/> with the conflict details, or
/// null when the port is actually free.
/// </summary>
public static class PortConflictDetector
{
    /// <summary>
    /// Checks a single port on 127.0.0.1 / 0.0.0.0 / [::]. Returns null if
    /// free, or a populated <see cref="PortConflictInfo"/> when something is
    /// already listening.
    /// </summary>
    public static PortConflictInfo? CheckPort(int port)
    {
        if (port < 1 || port > 65535)
            return null; // nothing we can usefully do with out-of-range

        var props = IPGlobalProperties.GetIPGlobalProperties();
        var listeners = props.GetActiveTcpListeners();
        var match = listeners.FirstOrDefault(ep => ep.Port == port);
        if (match is null)
            return null;

        // Port is held — try to find the owning process. All failures here
        // are non-fatal: we still return a conflict record with "unknown"
        // fields so the caller can surface "port in use" to the user.
        var (ownerPid, ownerName) = TryIdentifyOwner(port);
        return new PortConflictInfo(port, ownerPid, ownerName, match.Address.ToString());
    }

    /// <summary>
    /// Checks a candidate set of fallback ports (primary, +10, +100, etc.)
    /// and returns the first one that's free, or null if none are.
    /// </summary>
    public static int? SuggestFallback(int primaryPort, IEnumerable<int>? candidates = null)
    {
        candidates ??= primaryPort switch
        {
            80 => new[] { 8080, 8000, 8888 },
            443 => new[] { 8443, 4443, 9443 },
            3306 => new[] { 3307, 3308, 33060 },
            6379 => new[] { 6380, 6381, 16379 },
            1025 => new[] { 1026, 2525, 25252 },
            8025 => new[] { 8026, 18025 },
            _ => new[] { primaryPort + 1, primaryPort + 10, primaryPort + 100 },
        };
        foreach (var candidate in candidates)
        {
            if (CheckPort(candidate) is null)
                return candidate;
        }
        return null;
    }

    private static (int? pid, string? name) TryIdentifyOwner(int port)
    {
        if (!OperatingSystem.IsWindows())
            return (null, null);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netstat",
                Arguments = "-ano -p TCP",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var netstat = Process.Start(psi);
            if (netstat is null) return (null, null);
            var output = netstat.StandardOutput.ReadToEnd();
            netstat.WaitForExit(2000);

            // Each data line looks like:
            //   TCP    0.0.0.0:80           0.0.0.0:0              LISTENING       4
            // We want the LAST column (PID) of the LISTENING line whose local
            // address ends with ":{port}".
            var portSuffix = ":" + port;
            foreach (var rawLine in output.Split('\n'))
            {
                var line = rawLine.Trim();
                if (!line.StartsWith("TCP", StringComparison.OrdinalIgnoreCase)) continue;
                if (!line.Contains("LISTENING", StringComparison.OrdinalIgnoreCase)) continue;
                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5) continue;
                var local = parts[1];
                if (!local.EndsWith(portSuffix, StringComparison.Ordinal)) continue;
                if (!int.TryParse(parts[^1], out var pid)) continue;
                try
                {
                    using var proc = Process.GetProcessById(pid);
                    return (pid, proc.ProcessName);
                }
                catch
                {
                    return (pid, null); // process gone between netstat and lookup
                }
            }
        }
        catch
        {
            // netstat unavailable or parsing failed — caller still gets the conflict fact
        }
        return (null, null);
    }
}

/// <summary>
/// Result of a port conflict check — returned only when a port is actually
/// held. All fields except <see cref="Port"/> may be null if the OS strategy
/// couldn't identify the owning process.
/// </summary>
public sealed record PortConflictInfo(
    int Port,
    int? OwnerPid,
    string? OwnerProcessName,
    string? ListenAddress)
{
    public string ToUserMessage(int? suggestedFallback)
    {
        var who = OwnerProcessName is not null
            ? $"'{OwnerProcessName}' (PID {OwnerPid})"
            : OwnerPid is not null
                ? $"PID {OwnerPid}"
                : "an unknown process";
        var tail = suggestedFallback is not null
            ? $" Try port {suggestedFallback} as an alternative, or stop the conflicting process."
            : " Stop the conflicting process and try again.";
        return $"Port {Port} is already in use by {who}.{tail}";
    }
}
