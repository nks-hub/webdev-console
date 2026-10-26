using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NKS.WebDevConsole.Daemon.Mcp;

/// <summary>
/// Removes secrets from the tool-argument summary of an MCP audit row before
/// it is stored (and shown in the GUI). The MCP server redacts them too
/// (services/mcp-server/src/auditLog.ts, same key rule); this is the daemon's
/// own copy of that rule for any other caller of POST /api/mcp/tool-calls.
/// </summary>
public static partial class McpAuditRedactor
{
    public const string Placeholder = "[redacted]";

    // Keep non-ASCII text readable in the rewritten summary (it is shown as text, not HTML).
    private static readonly JsonSerializerOptions Compact =
        new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Keys whose values are secrets: any name containing password, pwd, secret or token.</summary>
    public static bool IsSecretKey(string key) => SecretKey().IsMatch(key);

    [GeneratedRegex("password|pwd|secret|token", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretKey();

    // "key": value — value is a string (possibly cut off by truncation) or a
    // bare scalar. Objects and arrays under a secret key are cut at their
    // opening bracket (the rest of the summary may be truncated JSON anyway).
    [GeneratedRegex("""(?<key>"(?:[^"\\]|\\.)*")(?<sep>\s*:\s*)(?<value>"(?:[^"\\]|\\.)*"?|[^,}\]\s]+|[\[{])""")]
    private static partial Regex KeyValue();

    /// <summary>
    /// Returns <paramref name="summary"/> with every secret value replaced by
    /// <see cref="Placeholder"/>. Whole JSON is parsed and rewritten; a summary
    /// the MCP server cut short (not valid JSON) is redacted textually.
    /// </summary>
    public static string? Redact(string? summary)
    {
        if (string.IsNullOrEmpty(summary)) return summary;
        try
        {
            var node = JsonNode.Parse(summary);
            if (node is JsonObject or JsonArray)
                return RedactNode(node) ? node.ToJsonString(Compact) : summary;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            // Truncated, not JSON, or duplicate keys: fall through to the textual pass.
        }
        return RedactText(summary);
    }

    private static bool RedactNode(JsonNode? node)
    {
        var changed = false;
        switch (node)
        {
            case JsonObject obj:
                foreach (var name in obj.Select(p => p.Key).ToList())
                {
                    if (IsSecretKey(name))
                    {
                        obj[name] = Placeholder;
                        changed = true;
                    }
                    else
                    {
                        changed |= RedactNode(obj[name]);
                    }
                }
                break;
            case JsonArray arr:
                foreach (var item in arr)
                    changed |= RedactNode(item);
                break;
        }
        return changed;
    }

    private static string RedactText(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        var pos = 0;
        foreach (Match m in KeyValue().Matches(text))
        {
            string key;
            try { key = JsonSerializer.Deserialize<string>(m.Groups["key"].Value) ?? ""; }
            catch (JsonException) { key = m.Groups["key"].Value; }
            if (!IsSecretKey(key)) continue;

            sb.Append(text, pos, m.Index - pos)
              .Append(m.Groups["key"].Value).Append(m.Groups["sep"].Value)
              .Append('"').Append(Placeholder).Append('"');
            pos = m.Index + m.Length;
            // An object or array under a secret key: drop the rest, its end
            // cannot be found reliably in text that may be cut short.
            if (m.Groups["value"].Value is "[" or "{")
                return sb.Append("...").ToString();
        }
        return sb.Append(text, pos, text.Length - pos).ToString();
    }
}
