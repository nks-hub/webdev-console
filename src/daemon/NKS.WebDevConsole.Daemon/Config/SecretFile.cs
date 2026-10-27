namespace NKS.WebDevConsole.Daemon.Config;

/// <summary>
/// Creates short-lived files that hold credentials (MySQL init SQL, client
/// defaults files). Writing first and chmod-ing afterwards leaves a window in
/// which another local user can open the file in the shared temp dir; on Unix
/// the file is created 0600 from the start instead, and CreateNew refuses a
/// path someone pre-created.
/// </summary>
public static class SecretFile
{
    private static FileStreamOptions Options()
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return options;
    }

    public static void WriteAllText(string path, string contents)
    {
        using var stream = new FileStream(path, Options());
        using var writer = new StreamWriter(stream);
        writer.Write(contents);
    }

    public static async Task WriteAllTextAsync(string path, string contents, CancellationToken ct = default)
    {
        await using var stream = new FileStream(path, Options());
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(contents.AsMemory(), ct);
    }
}
