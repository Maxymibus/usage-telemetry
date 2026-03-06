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
