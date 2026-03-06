using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Azure.Storage.Sas;
using Microsoft.Extensions.Options;
using Flywheel.Configuration;

namespace Flywheel.Services;

public interface IStorageService
{
    Task<UploadSasResult> CreateUploadSasAsync(string fileName, CancellationToken ct = default);
    Task EnqueueJobAsync(string json, CancellationToken ct = default);
    Task<JobStatusResult> GetJobStatusAsync(string jobId, CancellationToken ct = default);
    Task<Dictionary<string, string>> LoadEvidenceCsvsAsync(string jobId, CancellationToken ct = default);
}

public sealed record UploadSasResult(string JobId, string BlobName, string UploadUrl);
public sealed record JobStatusResult(string JobId, string Status, List<ArtifactInfo> Artifacts);
public sealed record ArtifactInfo(string Name, DateTimeOffset? LastModified, long Size);

public sealed class StorageService : IStorageService
{
    private readonly StorageOptions _opts;
    private readonly DefaultAzureCredential _credential;
    private readonly BlobServiceClient _blobService;

    public StorageService(IOptions<StorageOptions> opts, IOptions<AzureOpenAIOptions> aoaiOpts)
    {
        _opts = opts.Value;
        _credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            TenantId = string.IsNullOrWhiteSpace(aoaiOpts.Value.TenantId) ? null : aoaiOpts.Value.TenantId,
        });
        _blobService = new BlobServiceClient(new Uri(_opts.GetBlobServiceUri()), _credential);
    }

    public async Task<UploadSasResult> CreateUploadSasAsync(string fileName, CancellationToken ct = default)
    {
        var container = _blobService.GetBlobContainerClient(_opts.UploadsContainer);
        await container.CreateIfNotExistsAsync(cancellationToken: ct);

        var jobId = Guid.NewGuid().ToString("N");
        var safeFile = Path.GetFileName(fileName);
        var blobName = $"{DateTime.UtcNow:yyyy/MM/dd}/{jobId}/{safeFile}";
        var blobClient = container.GetBlobClient(blobName);

        var startsOn = DateTimeOffset.UtcNow.AddMinutes(-5);
        var expiresOn = DateTimeOffset.UtcNow.AddMinutes(20);
        var keyResponse = await _blobService.GetUserDelegationKeyAsync(startsOn, expiresOn, ct);

        var sasBuilder = new BlobSasBuilder
        {
            BlobContainerName = _opts.UploadsContainer,
            BlobName = blobName,
            Resource = "b",
            StartsOn = startsOn,
            ExpiresOn = expiresOn,
        };
        sasBuilder.SetPermissions(BlobSasPermissions.Create | BlobSasPermissions.Write);

        if (string.IsNullOrWhiteSpace(_opts.AccountName))
            throw new InvalidOperationException("Storage:AccountName is required for user-delegation SAS.");

        var sasQuery = sasBuilder.ToSasQueryParameters(keyResponse, _opts.AccountName).ToString();
        var uploadUri = new UriBuilder(blobClient.Uri) { Query = sasQuery }.Uri;

        return new UploadSasResult(jobId, blobName, uploadUri.ToString());
    }

    public async Task EnqueueJobAsync(string json, CancellationToken ct = default)
    {
        var queueClient = BuildQueueClient();
        await queueClient.CreateIfNotExistsAsync(cancellationToken: ct);
        await queueClient.SendMessageAsync(json, ct);
    }

    public async Task<JobStatusResult> GetJobStatusAsync(string jobId, CancellationToken ct = default)
    {
        var container = _blobService.GetBlobContainerClient(_opts.PatternsContainer);
        var artifacts = new List<ArtifactInfo>();
        bool hasCsv = false, hasError = false;

        await foreach (var item in container.GetBlobsAsync(prefix: $"{jobId}/", cancellationToken: ct))
        {
            if (item.Name.EndsWith("patterns.ERROR.txt", StringComparison.OrdinalIgnoreCase)) hasError = true;
            if (item.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) hasCsv = true;
            artifacts.Add(new ArtifactInfo(item.Name, item.Properties.LastModified, item.Properties.ContentLength ?? 0));
        }

        var status = hasError ? "failed" : (hasCsv ? "ready" : "processing");
        return new JobStatusResult(jobId, status, artifacts);
    }

    public async Task<Dictionary<string, string>> LoadEvidenceCsvsAsync(string jobId, CancellationToken ct = default)
    {
        var container = _blobService.GetBlobContainerClient(_opts.PatternsContainer);
        var wantedPrefixes = BuildWantedPrefixes(jobId);
        var csvTexts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        await foreach (var item in container.GetBlobsAsync(prefix: $"{jobId}/", cancellationToken: ct))
        {
            if (!item.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) continue;
            if (!wantedPrefixes.Any(p => item.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;

            var blob = container.GetBlobClient(item.Name);
            var content = await blob.DownloadContentAsync(ct);
            csvTexts[item.Name] = content.Value.Content.ToString();
        }

        return csvTexts;
    }

    private QueueClient BuildQueueClient()
    {
        var queueUri = $"{_opts.GetQueueServiceUri().TrimEnd('/')}/{_opts.QueueName}";
        return new QueueClient(new Uri(queueUri), _credential, new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 });
    }

    private static string[] BuildWantedPrefixes(string jobId)
    {
        var prefixes = new List<string>();
        for (int i = 1; i <= 5; i++) prefixes.Add($"{jobId}/patterns.E{i:D2}.");
        for (int i = 1; i <= 22; i++) prefixes.Add($"{jobId}/patterns.Q{i:D2}.");
        prefixes.Add($"{jobId}/patterns.DQ01.");
        prefixes.Add($"{jobId}/raw.sample100.");
        return prefixes.ToArray();
    }
}
