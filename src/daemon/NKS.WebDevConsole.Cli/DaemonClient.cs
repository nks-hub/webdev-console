using System.Net.Http.Json;
using System.Text.Json;

namespace NKS.WebDevConsole.Cli;

public class DaemonClient : IDisposable
{
    private readonly HttpClient _http;
    private string? _baseUrl;

    public DaemonClient()
    {
        _http = new HttpClient();
    }

    public bool Connect()
    {
        var portFile = Path.Combine(Path.GetTempPath(), "nks-wdc-daemon.port");
        if (!File.Exists(portFile)) return false;

        var lines = File.ReadAllLines(portFile);
        if (lines.Length < 2) return false;

        _baseUrl = $"http://localhost:{lines[0]}";
        _http.DefaultRequestHeaders.Authorization = new("Bearer", lines[1]);
        return true;
    }

    public async Task<T?> GetAsync<T>(string path)
    {
        // `using` so the HttpResponseMessage (headers + content stream) is
        // released immediately instead of waiting for finalization. Matters
        // for long-running CLI invocations like `wdc watch` that poll.
        using var response = await _http.GetAsync($"{_baseUrl}{path}");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>();
    }

    public async Task<JsonElement> GetJsonAsync(string path)
    {
        using var response = await _http.GetAsync($"{_baseUrl}{path}");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> PostAsync(string path, HttpContent? content = null)
    {
        using var response = await _http.PostAsync($"{_baseUrl}{path}", content);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> PutAsync(string path, HttpContent? content = null)
    {
        using var response = await _http.PutAsync($"{_baseUrl}{path}", content ?? new StringContent(""));
        response.EnsureSuccessStatusCode();
        // Match the GET/POST shape: ReadFromJsonAsync<JsonElement> deep-copies
        // the element out of the parser's buffer so callers can outlive it.
        // The previous `JsonDocument.ParseAsync(...).ContinueWith(t => t.Result.RootElement)`
        // returned an element that still referenced the now-disposed parser
        // buffer — use-after-free hazard on any caller that held the element.
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task DeleteAsync(string path)
    {
        // Callers consistently discard the response (e.g. `await client.DeleteAsync(path)`
        // without capturing), so returning HttpResponseMessage leaked
        // the handle on every delete. Swallow the response internally
        // once we've confirmed success — if a future caller needs the
        // body, add a sibling helper rather than reintroducing the leak.
        using var response = await _http.DeleteAsync($"{_baseUrl}{path}");
        response.EnsureSuccessStatusCode();
    }

    public bool IsConnected => _baseUrl != null;
    public void Dispose() => _http.Dispose();
}
