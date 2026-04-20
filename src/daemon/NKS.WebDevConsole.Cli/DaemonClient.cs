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
        var response = await _http.GetAsync($"{_baseUrl}{path}");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>();
    }

    public async Task<JsonElement> GetJsonAsync(string path)
    {
        var response = await _http.GetAsync($"{_baseUrl}{path}");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> PostAsync(string path, HttpContent? content = null)
    {
        var response = await _http.PostAsync($"{_baseUrl}{path}", content);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> PutAsync(string path, HttpContent? content = null)
    {
        var response = await _http.PutAsync($"{_baseUrl}{path}", content ?? new StringContent(""));
        response.EnsureSuccessStatusCode();
        // Match the GET/POST shape: ReadFromJsonAsync<JsonElement> deep-copies
        // the element out of the parser's buffer so callers can outlive it.
        // The previous `JsonDocument.ParseAsync(...).ContinueWith(t => t.Result.RootElement)`
        // returned an element that still referenced the now-disposed parser
        // buffer — use-after-free hazard on any caller that held the element.
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<HttpResponseMessage> DeleteAsync(string path)
    {
        var response = await _http.DeleteAsync($"{_baseUrl}{path}");
        response.EnsureSuccessStatusCode();
        return response;
    }

    public bool IsConnected => _baseUrl != null;
    public void Dispose() => _http.Dispose();
}
