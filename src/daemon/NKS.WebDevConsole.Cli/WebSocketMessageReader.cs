using System.Net.WebSockets;
using System.Text;

namespace NKS.WebDevConsole.Cli;

/// <summary>
/// Reads whole text messages from a WebSocket. A message larger than the
/// receive buffer arrives in several frames; decoding each frame on its own
/// split long log lines and broke multi-byte UTF-8 characters at the frame
/// boundary, so fragments are accumulated until EndOfMessage and decoded once.
/// </summary>
internal static class WebSocketMessageReader
{
    /// <summary>
    /// Returns the next complete text message, or null when the peer closed
    /// the socket. Binary messages are skipped.
    /// </summary>
    public static async Task<string?> ReceiveTextAsync(WebSocket socket, byte[] buffer, CancellationToken ct)
    {
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;

            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;

            if (result.MessageType == WebSocketMessageType.Text)
                return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);

            message.SetLength(0);
        }
    }
}
