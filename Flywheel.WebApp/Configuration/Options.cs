namespace Flywheel.Configuration;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public string AccountName { get; set; } = "";
    public string? BlobServiceUri { get; set; }
    public string? QueueServiceUri { get; set; }
    public string UploadsContainer { get; set; } = "uploads";
    public string PatternsContainer { get; set; } = "patterns";
    public string QueueName { get; set; } = "ingestion-jobs";

    public string GetBlobServiceUri() =>
        BlobServiceUri ?? $"https://{AccountName}.blob.core.windows.net";

    public string GetQueueServiceUri() =>
        QueueServiceUri ?? $"https://{AccountName}.queue.core.windows.net";
}

public sealed class AzureOpenAIOptions
{
    public const string SectionName = "AzureOpenAI";

    public string Endpoint { get; set; } = "";
    public string DeploymentName { get; set; } = "";
    public string? TenantId { get; set; }
}

public sealed class AzureSearchOptions
{
    public const string SectionName = "AzureSearch";

    public string Endpoint { get; set; } = "";
    public string KnowledgeBaseName { get; set; } = "flywheel-kb";
    public string ApiVersion { get; set; } = "2025-11-01-preview";
    public bool Enabled { get; set; } = true;
}
