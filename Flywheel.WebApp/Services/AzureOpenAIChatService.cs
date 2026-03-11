using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using Flywheel.Configuration;
using Flywheel.Models;

namespace Flywheel.Services;

public interface IChatService
{
    Task<string> CompleteChatAsync(List<ChatMessageDto> history, float temperature = 1.0f);
    Task<string> RunDiscoveryPassAsync(object evidenceBundle, string groundingContext);
    Task<string> RunArchitecturePassAsync(object evidenceBundle, string discoveryHtml);
    Task<string> RunImplementationPassAsync(string discoveryHtml, string learnContext);
}

public sealed class AzureOpenAIChatService : IChatService
{
    private readonly ChatClient _chatClient;

    public AzureOpenAIChatService(IOptions<AzureOpenAIOptions> aoaiOpts)
    {
        var o = aoaiOpts.Value;
        if (string.IsNullOrWhiteSpace(o.Endpoint) || string.IsNullOrWhiteSpace(o.DeploymentName))
            throw new InvalidOperationException("AzureOpenAI:Endpoint and AzureOpenAI:DeploymentName are required.");

        var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            TenantId = string.IsNullOrWhiteSpace(o.TenantId) ? null : o.TenantId,
        });

        _chatClient = new AzureOpenAIClient(new Uri(o.Endpoint), credential).GetChatClient(o.DeploymentName);
    }

    public async Task<string> CompleteChatAsync(List<ChatMessageDto> history, float temperature = 1.0f)
    {
        var messages = history.Select(ToChatMessage).ToList();
        var response = await _chatClient.CompleteChatAsync(messages, new ChatCompletionOptions { Temperature = temperature, TopP = 1.0f });
        return response.Value.Content.FirstOrDefault()?.Text ?? "";
    }

    public async Task<string> RunDiscoveryPassAsync(object evidenceBundle, string groundingContext)
    {
        var system = new SystemChatMessage(Prompts.DiscoverySystemPrompt);

        var parts = new List<string>
        {
            $"INPUT EVIDENCE (JSON):\n{System.Text.Json.JsonSerializer.Serialize(evidenceBundle)}"
        };

        if (!string.IsNullOrWhiteSpace(groundingContext))
            parts.Add($"AI SEARCH GROUNDING (semantically retrieved):\n{groundingContext}");

        parts.Add(Prompts.OpportunityDiscovery);
        parts.Add(Prompts.DiscoveryUserPrompt);

        var user = new UserChatMessage(string.Join("\n\n", parts));
        var response = await _chatClient.CompleteChatAsync(
            [system, user],
            new ChatCompletionOptions { Temperature = 0.7f, TopP = 1.0f });

        return response.Value.Content.FirstOrDefault()?.Text
               ?? "<div class='generated-html'><p>No discovery results.</p></div>";
    }

    public async Task<string> RunArchitecturePassAsync(object evidenceBundle, string discoveryHtml)
    {
        var system = new SystemChatMessage(Prompts.ArchitectureSystemPrompt);

        var parts = new List<string>
        {
            $"RAW EVIDENCE (JSON):\n{System.Text.Json.JsonSerializer.Serialize(evidenceBundle)}",
            $"DISCOVERY CARDS FROM PASS 1 (reference these by name, do not repeat them):\n{discoveryHtml}",
            Prompts.ArchitectureUserPrompt
        };

        var user = new UserChatMessage(string.Join("\n\n", parts));
        var response = await _chatClient.CompleteChatAsync(
            [system, user],
            new ChatCompletionOptions { Temperature = 0.3f, TopP = 1.0f });

        return response.Value.Content.FirstOrDefault()?.Text
               ?? "<div class='generated-html'><p>No architecture results.</p></div>";
    }

    public async Task<string> RunImplementationPassAsync(string discoveryHtml, string learnContext)
    {
        var system = new SystemChatMessage(Prompts.ImplementationSystemPrompt);

        var parts = new List<string>
        {
            $"DISCOVERY CARDS (reference agents by name):\n{discoveryHtml}",
            $"MICROSOFT LEARN DOCUMENTATION (retrieved via MCP):\n{learnContext}",
            Prompts.ImplementationUserPrompt
        };

        var user = new UserChatMessage(string.Join("\n\n", parts));
        var response = await _chatClient.CompleteChatAsync(
            [system, user],
            new ChatCompletionOptions { Temperature = 0.3f, TopP = 1.0f });

        return response.Value.Content.FirstOrDefault()?.Text
               ?? "<div class='generated-html'><p>No implementation results.</p></div>";
    }

    private static ChatMessage ToChatMessage(ChatMessageDto dto) => dto.Role switch
    {
        "system" => new SystemChatMessage(dto.Content),
        "assistant" => new AssistantChatMessage(dto.Content),
        _ => new UserChatMessage(dto.Content),
    };
}
