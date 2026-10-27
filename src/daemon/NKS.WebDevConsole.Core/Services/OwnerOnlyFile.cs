using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace NKS.WebDevConsole.Core.Services;

/// <summary>
/// Writes files that hold credentials (bearer tokens, passwords) so that no
/// other user can read them at any point, and replaces the target atomically.
///
/// The content goes to a new temp file with a random name in the target's
/// folder, opened with CreateNew (a pre-created file or link at that name
/// fails instead of being reused). On Unix the temp file is created 0600; on
/// Windows it is created with a protected DACL that grants full control to the
/// current user only, with inherited entries removed. The temp file is then
/// renamed over the target, which keeps that mode/DACL. If anything fails the
/// temp file is deleted and the exception propagates.
///
/// The Windows DACL keeps other user accounts out; it does not separate
/// integrity levels of the same user, and no mandatory label is set.
/// </summary>
public static class OwnerOnlyFile
{
    public static void WriteAllTextAtomic(string path, string contents) =>
        WriteAllBytesAtomic(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(contents));

    public static void WriteAllBytesAtomic(string path, byte[] contents)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full)!;
        var tmp = Path.Combine(dir, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = CreateNew(tmp))
            {
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }
            MoveWithRetry(tmp, full);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best effort; the temp file is owner-only */ }
            throw;
        }
    }

    /// <summary>Opens a new file that only the current user can access.</summary>
    public static FileStream CreateNew(string path)
    {
        if (OperatingSystem.IsWindows())
            return CreateNewWindows(path);
        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
    }

    [SupportedOSPlatform("windows")]
    private static FileStream CreateNewWindows(string path)
    {
        return new FileInfo(path).Create(
            FileMode.CreateNew,
            FileSystemRights.FullControl,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.None,
            CurrentUserOnlySecurity());
    }

    /// <summary>DACL with a single full-control entry for the current user; inheritance removed.</summary>
    [SupportedOSPlatform("windows")]
    public static FileSecurity CurrentUserOnlySecurity()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("Current Windows user has no SID");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }

    // On Windows a reader that has the target open without FILE_SHARE_DELETE
    // makes the rename fail for a moment; retry briefly before giving up.
    private static void MoveWithRetry(string source, string destination)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(source, destination, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < 5 && OperatingSystem.IsWindows()
                                       && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(50 * attempt);
            }
        }
    }
}
