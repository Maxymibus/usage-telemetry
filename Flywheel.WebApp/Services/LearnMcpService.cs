using System.Text;
using System.Text.Json;

namespace Flywheel.Services;

public interface ILearnMcpService
{
    bool IsEnabled { get; }
    Task<List<LearnSearchResult>> SearchDocsAsync(string query, CancellationToken ct = default);
    Task<string> FetchDocAsync(string url, CancellationToken ct = default);
    Task<List<LearnSearchResult>> SearchCodeSamplesAsync(string query, string language = "csharp", CancellationToken ct = default);
}

public sealed record LearnSearchResult(string Title, string Url, string Snippet);

public sealed class LearnMcpService : ILearnMcpService
{
    private const string McpEndpoint = "https://learn.microsoft.com/api/mcp";
    private readonly HttpClient _http;
    private int _requestId;

    public bool IsEnabled => true;

    public LearnMcpService(IHttpClientFactory httpFactory)
    {
        _http = httpFactory.CreateClient("LearnMcp");
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task<List<LearnSearchResult>> SearchDocsAsync(string query, CancellationToken ct = default)
    {
        return await CallSearchToolAsync("microsoft_docs_search", new { query }, ct);
    }

    public async Task<string> FetchDocAsync(string url, CancellationToken ct = default)
    {
        var response = await CallToolAsync("microsoft_docs_fetch", new { url }, ct);
        if (response.RootElement.TryGetProperty("result", out var result)
            && result.TryGetProperty("content", out var content))
        {
            var parts = new List<string>();
            foreach (var item in content.EnumerateArray())
            {
                if (item.TryGetProperty("text", out var text))
                    parts.Add(text.GetString() ?? "");
            }
            var full = string.Join("\n", parts);
            return full.Length > 8000 ? full[..8000] : full;
        }
        return "";
    }

    public async Task<List<LearnSearchResult>> SearchCodeSamplesAsync(string query, string language = "csharp", CancellationToken ct = default)
    {
        return await CallSearchToolAsync("microsoft_code_sample_search", new { query, language }, ct);
    }

    private async Task<List<LearnSearchResult>> CallSearchToolAsync(string toolName, object arguments, CancellationToken ct)
    {
        var results = new List<LearnSearchResult>();

        try
        {
            var response = await CallToolAsync(toolName, arguments, ct);

            if (response.RootElement.TryGetProperty("result", out var result)
                && result.TryGetProperty("content", out var content))
            {
                foreach (var item in content.EnumerateArray())
                {
                    if (!item.TryGetProperty("text", out var text)) continue;
                    var raw = text.GetString() ?? "";

                    var title = ExtractField(raw, "title") ?? ExtractFirstLine(raw);
                    var url = ExtractField(raw, "url") ?? "";
                    var snippet = ExtractField(raw, "content") ?? ExtractField(raw, "snippet") ?? raw;

                    if (snippet.Length > 500) snippet = snippet[..500];

                    if (!string.IsNullOrWhiteSpace(title))
                        results.Add(new LearnSearchResult(title, url, snippet));
                }
            }
        }
        catch { }

        return results;
    }

    private async Task<JsonDocument> CallToolAsync(string toolName, object arguments, CancellationToken ct)
    {
        await InitializeSessionAsync(ct);

        var payload = new
        {
            jsonrpc = "2.0",
            id = Interlocked.Increment(ref _requestId),
            method = "tools/call",
            @params = new { name = toolName, arguments }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, McpEndpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Accept", "application/json, text/event-stream");

        var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        var jsonBody = ExtractJsonFromSse(body);
        return JsonDocument.Parse(jsonBody);
    }

    private async Task InitializeSessionAsync(CancellationToken ct)
    {
        var payload = new
        {
            jsonrpc = "2.0",
            id = Interlocked.Increment(ref _requestId),
            method = "initialize",
            @params = new
            {
                protocolVersion = "2025-03-26",
                capabilities = new { },
                clientInfo = new { name = "Flywheel", version = "1.0" }
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, McpEndpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Accept", "application/json, text/event-stream");

        try
        {
            var response = await _http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (response.Headers.TryGetValues("mcp-session-id", out var sessionIds))
            {
                var sessionId = sessionIds.FirstOrDefault();
                if (!string.IsNullOrEmpty(sessionId))
                    _http.DefaultRequestHeaders.TryAddWithoutValidation("mcp-session-id", sessionId);
            }
        }
        catch { }
    }

    private static string ExtractJsonFromSse(string body)
    {
        if (body.TrimStart().StartsWith('{'))
            return body;

        foreach (var line in body.Split('\n'))
        {
            if (line.StartsWith("data:"))
            {
                var json = line[5..].Trim();
                if (json.StartsWith('{'))
                    return json;
            }
        }

        return body;
    }

    private static string? ExtractField(string text, string fieldName)
    {
        try
        {
            var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty(fieldName, out var val))
                return val.GetString();
        }
        catch { }

        return null;
    }

    private static string ExtractFirstLine(string text)
    {
        var line = text.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "";
        return line.Length > 200 ? line[..200] : line;
    }
}
