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
/// Live channels: per-service log WebSocket and the SSE event stream.
/// Moved verbatim out of Program.cs; registered from there at the same
/// point in the pipeline so route order is unchanged.
/// </summary>
internal static class EventStreamEndpoints
{
    public static void MapEventStreamEndpoints(this WebApplication app)
    {
        // WebSocket log streaming — real-time per-service log lines with zero
        // batching delay. Complements the SSE /api/events endpoint which handles
        // service state, metrics, and validation events. The log viewer in the
        // frontend connects here for immediate log output.
        app.Map("/api/logs/{id}/stream", async (HttpContext ctx, string id, WebSocketLogStreamer streamer) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsync("WebSocket upgrade required");
                return;
            }

            var modules = ctx.RequestServices.GetServices<IServiceModule>();
            var module = modules.FirstOrDefault(m => m.ServiceId.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (module == null)
            {
                ctx.Response.StatusCode = 404;
                await ctx.Response.WriteAsync($"Service '{id}' not found");
                return;
            }

            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();

            // Send recent log history first so the client doesn't start with an empty viewer
            var history = await ctx.RequestServices.GetRequiredService<ServiceLogBuffer>().TailAsync(module, 50, ctx.RequestAborted);
            foreach (var line in history)
            {
                var payload = System.Text.Json.JsonSerializer.Serialize(new { line, ts = DateTime.UtcNow });
                var bytes = System.Text.Encoding.UTF8.GetBytes(payload);
                await ws.SendAsync(new ArraySegment<byte>(bytes),
                    System.Net.WebSockets.WebSocketMessageType.Text, true, ctx.RequestAborted);
            }

            // Stream new lines as they arrive
            await streamer.StreamAsync(id, ws, ctx.RequestAborted);
        });

        // SSE endpoint
        app.MapGet("/api/events", async (HttpContext ctx, SseService sse) =>
        {
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers.Connection = "keep-alive";

            var clientId = sse.AddClient(ctx.Response);

            // Send initial connected event
            await ctx.Response.WriteAsync($"event: connected\ndata: {{\"clientId\":\"{clientId}\"}}\n\n");
            await ctx.Response.Body.FlushAsync();

            // Keep connection alive until client disconnects
            try
            {
                await Task.Delay(Timeout.Infinite, ctx.RequestAborted);
            }
            catch (TaskCanceledException) { }
            finally
            {
                sse.RemoveClient(clientId);
            }
        });
    }
}
