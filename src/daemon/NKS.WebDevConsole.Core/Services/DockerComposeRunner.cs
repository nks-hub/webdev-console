using System.Diagnostics;

namespace NKS.WebDevConsole.Core.Services;

/// <summary>
/// Executes <c>docker compose</c> commands in a site's document root.
/// Phase 11 lifecycle layer on top of <see cref="DockerComposeDetector"/>.
///
/// All methods run the compose CLI as a child process and return the
/// combined stdout+stderr output. The caller (daemon API endpoint) is
/// responsible for streaming or buffering the output.
///
/// Requires Docker Desktop or Docker Engine with the Compose V2 plugin
/// installed. Falls back to <c>docker-compose</c> (V1 standalone binary)
/// if the V2 invocation fails.
/// </summary>
public static class DockerComposeRunner
{
    public record ComposeResult(bool Success, int ExitCode, string Output);

    /// <summary>
    /// Run <c>docker compose ps --format json</c> in the given directory
    /// to list running containers for the project.
    /// </summary>
    public static async Task<ComposeResult> PsAsync(string workingDir, CancellationToken ct = default)
        => await RunAsync(workingDir, "ps --format json", ct);

    /// <summary>
    /// Run <c>docker compose up -d</c> to start all services in detached mode.
    /// </summary>
    public static async Task<ComposeResult> UpAsync(string workingDir, CancellationToken ct = default)
        => await RunAsync(workingDir, "up -d", ct);

    /// <summary>
    /// Run <c>docker compose down</c> to stop and remove containers.
    /// </summary>
    public static async Task<ComposeResult> DownAsync(string workingDir, CancellationToken ct = default)
        => await RunAsync(workingDir, "down", ct);

    /// <summary>
    /// Run <c>docker compose restart</c> to restart all services.
    /// </summary>
    public static async Task<ComposeResult> RestartAsync(string workingDir, CancellationToken ct = default)
        => await RunAsync(workingDir, "restart", ct);

    /// <summary>
    /// Run <c>docker compose logs --tail=100</c> to fetch recent log output.
    /// </summary>
    public static async Task<ComposeResult> LogsAsync(string workingDir, int tail = 100, CancellationToken ct = default)
        => await RunAsync(workingDir, $"logs --tail={tail} --no-color", ct);

    private static async Task<ComposeResult> RunAsync(string workingDir, string args, CancellationToken ct)
    {
        if (!Directory.Exists(workingDir))
            return new ComposeResult(false, -1, $"Directory not found: {workingDir}");

        // Try Compose V2 first (docker compose), fall back to V1 (docker-compose)
        var result = await ExecuteAsync("docker", $"compose {args}", workingDir, ct);
        if (result.ExitCode == 0 || !result.Output.Contains("is not a docker command"))
            return result;

        // Fallback to docker-compose standalone
        return await ExecuteAsync("docker-compose", args, workingDir, ct);
    }

    private static async Task<ComposeResult> ExecuteAsync(
        string exe, string args, string workingDir, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = new Process { StartInfo = psi };
            process.Start();

            // Read stdout and stderr concurrently to avoid deadlock
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            var combined = string.IsNullOrEmpty(stderr) ? stdout : $"{stdout}\n{stderr}".Trim();

            return new ComposeResult(process.ExitCode == 0, process.ExitCode, combined);
        }
        catch (Exception ex)
        {
            return new ComposeResult(false, -1, $"Failed to run {exe}: {ex.Message}");
        }
    }
}
