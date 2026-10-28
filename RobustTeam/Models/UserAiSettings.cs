
namespace AiUseCaseDiscovery.Models;

public class UserAiSettings
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;

    public string AzureOpenAiEndpoint { get; set; } = string.Empty;
    public string AzureOpenAiApiKey { get; set; } = string.Empty;
    public string DeploymentName { get; set; } = string.Empty;
}
