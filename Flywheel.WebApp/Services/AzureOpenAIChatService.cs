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
    Task<string> GenerateUseCasesAsync(object evidenceBundle);
}

public sealed class AzureOpenAIChatService : IChatService
{
    private readonly ChatClient _chatClient;

    public AzureOpenAIChatService(IOptions<AzureOpenAIOptions> opts)
    {
        var o = opts.Value;
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

    public async Task<string> GenerateUseCasesAsync(object evidenceBundle)
    {
        var system = new SystemChatMessage(Prompts.UseCaseSystemPrompt);
        var user = new UserChatMessage(
            $"INPUT EVIDENCE (JSON):\n{System.Text.Json.JsonSerializer.Serialize(evidenceBundle)}\n\n{Prompts.UseCaseUserPrompt}\n\n{Prompts.OpportunityDiscovery}");

        var response = await _chatClient.CompleteChatAsync(
            [system, user],
            new ChatCompletionOptions { TopP = 1.0f });

        return response.Value.Content.FirstOrDefault()?.Text
               ?? "<div class='generated-html'><p>No response.</p></div>";
    }

    private static ChatMessage ToChatMessage(ChatMessageDto dto) => dto.Role switch
    {
        "system" => new SystemChatMessage(dto.Content),
        "assistant" => new AssistantChatMessage(dto.Content),
        _ => new UserChatMessage(dto.Content),
    };
}
