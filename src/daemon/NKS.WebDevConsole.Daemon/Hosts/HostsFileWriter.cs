using System.Text;

namespace NKS.WebDevConsole.Daemon.Hosts;

/// <summary>
/// Single gate and crash-safe write for the system hosts file.
///
/// The hosts editor (/api/hosts/apply, /api/hosts/restore) and the site
/// orchestrator (managed block refresh on every site apply) all do
/// read → modify → write on the same file. Without a shared gate, a site
/// apply running while the user saved the hosts editor could write back
/// content read before the editor's save, silently dropping it. And the
/// in-place truncate + write left an empty or partial hosts file if the
/// daemon died mid-write.
/// </summary>
public static class HostsFileWriter
{
    /// <summary>Held for the whole read-modify-write cycle by every hosts writer.</summary>
    public static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>
    /// Writes <paramref name="content"/> via a temp file in the same directory
    /// plus rename, keeping the original file mode on Unix. Falls back to an
    /// in-place write when the file cannot be replaced by rename (container
    /// bind mounts report EBUSY, Windows DNS client may hold it without delete
    /// sharing, or the directory is not writable while the file is).
    /// </summary>
    public static async Task WriteAsync(string hostsPath, string content, Encoding encoding, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(hostsPath);
        if (!string.IsNullOrEmpty(dir))
        {
            var tmp = Path.Combine(dir, $".{Path.GetFileName(hostsPath)}.wdc-{Guid.NewGuid():N}.tmp");
            try
            {
                var bytes = encoding.GetBytes(content);
                await using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                {
                    await fs.WriteAsync(bytes, ct);
                    fs.Flush(flushToDisk: true);
                }
                if (!OperatingSystem.IsWindows() && File.Exists(hostsPath))
                {
                    try { File.SetUnixFileMode(tmp, File.GetUnixFileMode(hostsPath)); }
                    catch { /* keep default mode — still readable by the resolver */ }
                }
                File.Move(tmp, hostsPath, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
                ct.ThrowIfCancellationRequested();
            }
        }

        await File.WriteAllTextAsync(hostsPath, content, encoding, ct);
    }
}
