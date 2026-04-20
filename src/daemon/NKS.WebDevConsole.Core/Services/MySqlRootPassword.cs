using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace NKS.WebDevConsole.Core.Services;

/// <summary>
/// DPAPI-protected MySQL root password store.
///
/// Plan item "Phase 2 — root password to DPAPI". After <c>mysqld --initialize-insecure</c>
/// MySQL has a passwordless root — fine for first boot but we immediately generate a random
/// password, set it on the running instance, and persist it encrypted with
/// <see cref="ProtectedData.Protect"/> under <see cref="DataProtectionScope.CurrentUser"/>.
/// Only the daemon user (and an admin who takes over the account) can decrypt.
///
/// Storage location: <c>%USERPROFILE%\.wdc\data\mysql-root.dpapi</c>.
///
/// No-op / in-memory fallback on non-Windows (DPAPI is Windows-only).
/// </summary>
public static class MySqlRootPassword
{
    private static readonly string StorePath = Path.Combine(
        WdcPaths.DataRoot, "mysql-root.dpapi");

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NKS.WebDevConsole.MySqlRoot.v1");

    /// <summary>Returns true if a password has been generated and persisted.</summary>
    public static bool Exists() => File.Exists(StorePath);

    /// <summary>
    /// Generates a cryptographically strong 32-character password and persists it
    /// encrypted with DPAPI. Overwrites any existing store. Returns the plaintext
    /// password so the caller can hand it to MySQL (e.g. via <c>SET PASSWORD</c>).
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static string GenerateAndStore()
    {
        var password = GenerateSecurePassword(32);
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);

        if (OperatingSystem.IsWindows())
        {
            var plaintextBytes = Encoding.UTF8.GetBytes(password);
            var encrypted = ProtectedData.Protect(plaintextBytes, Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(StorePath, encrypted);
        }
        else
        {
            // Non-Windows fallback: plaintext file with 0600 permissions.
            // Acceptable because the plan explicitly targets Windows for DPAPI; on
            // macOS/Linux a future iteration can switch to Keychain / libsecret.
            File.WriteAllText(StorePath, password);
            try
            {
                var fi = new FileInfo(StorePath);
                fi.UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            catch { /* best effort */ }
        }

        return password;
    }

    /// <summary>
    /// Reads and decrypts the stored password. Returns <c>null</c> if not yet generated
    /// or if decryption fails (corrupted file, wrong user profile, etc.).
    /// </summary>
    public static string? TryRead()
    {
        if (!File.Exists(StorePath)) return null;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var encrypted = File.ReadAllBytes(StorePath);
                var plaintext = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plaintext);
            }
            return File.ReadAllText(StorePath).Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Persists a caller-supplied password into the DPAPI store, overwriting
    /// whatever is there. Used when the user's mysqld root password was set
    /// outside WDC (external MySQL install, imported from MAMP, manually
    /// changed) and we just need to sync the stored copy so subsequent
    /// /api/databases calls can authenticate. Does NOT run
    /// `ALTER USER`; the caller is responsible for aligning mysqld's own
    /// record of the password with what's stored here.
    /// </summary>
    public static void SetPlaintext(string password)
    {
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("password must be non-empty", nameof(password));

        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        if (OperatingSystem.IsWindows())
        {
            var plaintextBytes = Encoding.UTF8.GetBytes(password);
            var encrypted = ProtectedData.Protect(plaintextBytes, Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(StorePath, encrypted);
        }
        else
        {
            File.WriteAllText(StorePath, password);
            try
            {
                var fi = new FileInfo(StorePath);
                fi.UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Ensures a password exists. If one is already stored, returns it. Otherwise
    /// generates a new one via <see cref="GenerateAndStore"/> and returns it.
    /// Callers should hand the returned password to <c>mysqladmin password</c> or
    /// <c>SET PASSWORD</c> only when the caller knows MySQL is currently running
    /// with a passwordless root (i.e. right after <c>--initialize-insecure</c>).
    /// </summary>
    public static string EnsureExists()
    {
        var existing = TryRead();
        if (existing is not null) return existing;
        if (OperatingSystem.IsWindows())
            return GenerateAndStore();
        // Non-Windows fallback path that skips the [SupportedOSPlatform] check
        var password = GenerateSecurePassword(32);
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        File.WriteAllText(StorePath, password);
        try
        {
            var fi = new FileInfo(StorePath);
            fi.UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        catch { }
        return password;
    }

    private static string GenerateSecurePassword(int length)
    {
        // Printable ASCII minus characters that give shells / SQL a hard time.
        const string alphabet =
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789" +
            "-_.~@#%+=";
        var bytes = RandomNumberGenerator.GetBytes(length);
        var sb = new StringBuilder(length);
        foreach (var b in bytes)
            sb.Append(alphabet[b % alphabet.Length]);
        return sb.ToString();
    }
}
