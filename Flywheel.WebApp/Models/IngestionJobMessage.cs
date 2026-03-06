namespace Flywheel.Models;

public sealed record IngestionJobMessage(
    string JobId,
    string BlobName,
    string OriginalFileName,
    DateTimeOffset UploadedAt);

public sealed record GenerateUseCasesRequest(string JobId);
