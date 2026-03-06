using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;
using Flywheel.Configuration;

namespace Flywheel.Services;

public interface ISearchService
{
    bool IsEnabled { get; }
    Task<string> RetrieveGroundingAsync(string userQuery, CancellationToken ct = default);
}

public sealed class SearchService : ISearchService
{
    private readonly AzureSearchOptions _opts;
    private readonly DefaultAzureCredential? _credential;
    private readonly HttpClient _http;

    public bool IsEnabled => _opts.Enabled && !string.IsNullOrWhiteSpace(_opts.Endpoint);

    public SearchService(
        IOptions<AzureSearchOptions> searchOpts,
        IOptions<AzureOpenAIOptions> aoaiOpts,
        IHttpClientFactory httpFactory)
    {
        _opts = searchOpts.Value;

        if (!IsEnabled) return;

        _credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            TenantId = string.IsNullOrWhiteSpace(aoaiOpts.Value.TenantId) ? null : aoaiOpts.Value.TenantId,
        });

        _http = httpFactory.CreateClient("AzureSearch");
    }

    public async Task<string> RetrieveGroundingAsync(string userQuery, CancellationToken ct = default)
    {
        if (!IsEnabled || _credential is null) return "";

        var token = await _credential.GetTokenAsync(
            new TokenRequestContext(["https://search.azure.com/.default"]), ct);

        var url = $"{_opts.Endpoint.TrimEnd('/')}/knowledgebases/{_opts.KnowledgeBaseName}/retrieve?api-version={_opts.ApiVersion}";

        var body = new
        {
            messages = new object[]
            {
                new { role = "system", content = new[] { new { type = "text", text = "You retrieve evidence about SaaS user behavioral patterns, agent architectures, and sales process optimization from KQL analysis results." } } },
                new { role = "user", content = new[] { new { type = "text", text = userQuery } } }
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        var response = await _http.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
            return "";

        var json = await response.Content.ReadAsStringAsync(ct);
        var doc = JsonDocument.Parse(json);

        var groundingParts = new List<string>();

        if (doc.RootElement.TryGetProperty("response", out var resp)
            && resp.TryGetProperty("content", out var content))
        {
            foreach (var item in content.EnumerateArray())
            {
                if (item.TryGetProperty("text", out var text))
                    groundingParts.Add(text.GetString() ?? "");
            }
        }

        if (doc.RootElement.TryGetProperty("references", out var refs))
        {
            foreach (var refItem in refs.EnumerateArray())
            {
                if (refItem.TryGetProperty("content", out var refContent))
                    groundingParts.Add(refContent.GetString() ?? "");
            }
        }

        return string.Join("\n\n", groundingParts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }
}
