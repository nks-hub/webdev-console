namespace NKS.WebDevConsole.Core.Services;

/// <summary>
/// Detects whether a site's document root ships with a Docker Compose
/// stack. A small step toward the Phase 11 "Docker Compose integration"
/// roadmap item — just the detection layer so the UI can show a badge
/// and future iterations can wire up lifecycle control.
///
/// Looks for the canonical filenames that <c>docker compose</c> itself
/// accepts (<c>compose.yaml</c>, <c>compose.yml</c>, <c>docker-compose.yaml</c>,
/// <c>docker-compose.yml</c>), in priority order matching Compose v2's
/// own resolution rules. Subdirectories are NOT searched — compose files
/// by convention live at the project root.
/// </summary>
public static class DockerComposeDetector
{
    /// <summary>
    /// Canonical filenames the Docker Compose CLI itself accepts, in
    /// priority order. See
    /// https://docs.docker.com/compose/compose-file/03-compose-file/.
    /// </summary>
    public static readonly string[] ComposeFileNames =
    [
        "compose.yaml",
        "compose.yml",
        "docker-compose.yaml",
        "docker-compose.yml",
    ];

    /// <summary>
    /// Returns the absolute path of the first Compose file found in
    /// <paramref name="documentRoot"/>, or <c>null</c> when the directory
    /// doesn't exist, is empty, or contains no compose file.
    /// </summary>
    public static string? FindComposeFile(string? documentRoot)
    {
        if (string.IsNullOrWhiteSpace(documentRoot))
            return null;

        try
        {
            if (!Directory.Exists(documentRoot))
                return null;

            foreach (var name in ComposeFileNames)
            {
                var candidate = Path.Combine(documentRoot, name);
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        catch (Exception)
        {
            // Permission denied, path too long, etc. — treat as "not detected".
        }

        return null;
    }

    /// <summary>Shortcut: <c>FindComposeFile != null</c>.</summary>
    public static bool HasCompose(string? documentRoot) =>
        FindComposeFile(documentRoot) is not null;
}
