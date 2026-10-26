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

namespace NKS.WebDevConsole.Daemon.Services;

/// <summary>
/// DNS cache flush.
/// Moved verbatim out of Program.cs; registered from there at the same
/// point in the pipeline so route order is unchanged.
/// </summary>
internal static class DnsEndpoints
{
    public static void MapDnsEndpoints(this WebApplication app)
    {
        // DNS flush
        app.MapPost("/api/dns/flush", async () =>
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    var result = await CliWrap.Cli.Wrap("ipconfig")
                        .WithArguments("/flushdns")
                        .WithValidation(CliWrap.CommandResultValidation.None)
                        .ExecuteBufferedAsync();
                    return Results.Ok(new { ok = result.ExitCode == 0, output = result.StandardOutput.Trim() });
                }
                return Results.Ok(new { ok = false, output = "Not supported on this platform" });
            }
            catch (Exception ex)
            {
                return Results.Ok(new { ok = false, output = ex.Message });
            }
        });
    }
}
