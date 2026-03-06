using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Identity;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables()
    .Build();

var workspaceId = config["LOG_ANALYTICS_WORKSPACE_ID"] ?? config["LogAnalytics:WorkspaceId"];
if (!Guid.TryParse(workspaceId, out _))
    throw new InvalidOperationException($"LogAnalytics:WorkspaceId must be a GUID. Current: '{workspaceId ?? "<null>"}'");

var credential = BuildCredential(config);
var logsClient = new LogsQueryClient(credential);

using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
var log = loggerFactory.CreateLogger("Flywheel.Ingestion");

var accountName = Must(config["Storage:AccountName"], "Storage:AccountName");
var blobServiceUri = MustUri(config["Storage:BlobServiceUri"] ?? $"https://{accountName}.blob.core.windows.net", "Storage:BlobServiceUri");
var queueServiceUri = MustUri(config["Storage:QueueServiceUri"] ?? $"https://{accountName}.queue.core.windows.net", "Storage:QueueServiceUri");
var uploadsContainer = config["Storage:UploadsContainer"] ?? "uploads";
var patternsContainer = config["Storage:PatternsContainer"] ?? "patterns";
var queueName = config["Storage:QueueName"] ?? "ingestion-jobs";

var blobService = new BlobServiceClient(blobServiceUri, credential);
var uploads = blobService.GetBlobContainerClient(uploadsContainer);
var patterns = blobService.GetBlobContainerClient(patternsContainer);
await patterns.CreateIfNotExistsAsync();

var queueClient = new QueueClient(new Uri($"{queueServiceUri}/{queueName}"), credential);
await queueClient.CreateIfNotExistsAsync();

var aiConn = Must(config["ApplicationInsights:ConnectionString"], "ApplicationInsights:ConnectionString");
var tc = new TelemetryClient(new TelemetryConfiguration { ConnectionString = aiConn });

var aoaiEndpoint = Must(config["AzureOpenAI:Endpoint"], "AzureOpenAI:Endpoint");
var aoaiDeployment = Must(config["AzureOpenAI:DeploymentName"], "AzureOpenAI:DeploymentName");
var aoaiClient = new AzureOpenAIClient(new Uri(aoaiEndpoint), credential);
ChatClient chatClient = aoaiClient.GetChatClient(aoaiDeployment);

var queryLibraryPath = config["Flywheel:QueryLibraryPath"] ?? "Queries/kql-library.json";
var kqlLibrary = KqlLibrary.Load(Path.Combine(AppContext.BaseDirectory, queryLibraryPath));
log.LogInformation("Loaded {Count} KQL queries from {Path}", kqlLibrary.Queries.Count, queryLibraryPath);

int sampleSize = ParseConfigInt(config, "Flywheel:SampleRows", 100, 10, 200);
int minQueries = ParseConfigInt(config, "Flywheel:MinQueries", 22, 1, 50);
int initialVisibilityMinutes = ParseConfigInt(config, "Flywheel:InitialVisibilityMinutes", 30, 5, 120);
int poisonThreshold = ParseConfigInt(config, "Flywheel:MaxDequeue", 5, 1, 50);

log.LogInformation("Flywheel started. Polling queue '{QueueName}'", queueName);

while (true)
{
    try
    {
        var received = await queueClient.ReceiveMessagesAsync(maxMessages: 1, visibilityTimeout: TimeSpan.FromMinutes(initialVisibilityMinutes));
        if (received.Value.Length == 0) { await Task.Delay(2000); continue; }

        var msg = received.Value[0];

        if (msg.DequeueCount >= poisonThreshold)
        {
            var poison = new QueueClient(new Uri($"{queueServiceUri}/{queueName}-poison"), credential);
            await poison.CreateIfNotExistsAsync();
            await poison.SendMessageAsync(msg.MessageText);
            await queueClient.DeleteMessageAsync(msg.MessageId, msg.PopReceipt);
            log.LogWarning("Moved message to poison after {Count} dequeues", msg.DequeueCount);
            continue;
        }

        var cts = new CancellationTokenSource();
        string currentPop = msg.PopReceipt;
        var heartbeat = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var update = await queueClient.UpdateMessageAsync(msg.MessageId, currentPop, msg.MessageText, TimeSpan.FromMinutes(10));
                    currentPop = update.Value.PopReceipt;
                }
                catch { }
                try { await Task.Delay(TimeSpan.FromMinutes(2), cts.Token); } catch { }
            }
        }, cts.Token);

        try
        {
            var json = TryDecodeToJson(msg.MessageText);
            var job = JsonSerializer.Deserialize<IngestionJobMessage>(json) ?? throw new InvalidOperationException("Invalid job JSON");

            log.LogInformation("Dequeued JobId={JobId} BlobName={BlobName} DequeueCount={Dequeue}", job.JobId, job.BlobName, msg.DequeueCount);

            var lockBlob = patterns.GetBlobClient($"{job.JobId}/.lock");
            if (await lockBlob.ExistsAsync())
            {
                await queueClient.DeleteMessageAsync(msg.MessageId, currentPop);
                log.LogInformation("Job {JobId} already locked; skipping", job.JobId);
                continue;
            }
            await lockBlob.UploadAsync(new BinaryData($"started {DateTime.UtcNow:o}"), overwrite: false);

            try
            {
                await ProcessJobAsync(job, uploads, patterns, tc, logsClient, workspaceId!, chatClient, kqlLibrary, log, sampleSize, minQueries);
                await patterns.GetBlobClient($"{job.JobId}/.done").UploadAsync(new BinaryData($"completed {DateTime.UtcNow:o}"), overwrite: true);
                await queueClient.DeleteMessageAsync(msg.MessageId, currentPop);
            }
            finally { try { await lockBlob.DeleteIfExistsAsync(); } catch { } }
        }
        catch (Exception ex)
        {
            var errName = "unknown";
            try { var job = JsonSerializer.Deserialize<IngestionJobMessage>(TryDecodeToJson(msg.MessageText)); if (job != null) errName = $"{job.JobId}/patterns.ERROR.txt"; }
            catch { errName = "unknown.ERROR.txt"; }
            try { if (errName != "unknown") await patterns.GetBlobClient(errName).UploadAsync(new BinaryData(ex.ToString()), overwrite: true); } catch { }
            log.LogError(ex, "Job failed; will retry until poison threshold");
        }
        finally { cts.Cancel(); try { await heartbeat; } catch { } }
    }
    catch (Exception loopEx) { log.LogError(loopEx, "WebJob loop error"); await Task.Delay(5000); }
}

static async Task ProcessJobAsync(
    IngestionJobMessage job, BlobContainerClient uploads, BlobContainerClient patterns,
    TelemetryClient tc, LogsQueryClient logsClient, string workspaceId,
    ChatClient chatClient, KqlLibrary kqlLibrary, ILogger log, int sampleSize, int minQueries)
{
    var blob = uploads.GetBlobClient(job.BlobName);

    var csvConfigNoHeader = new CsvConfiguration(CultureInfo.InvariantCulture)
    {
        HasHeaderRecord = false, DetectDelimiter = true, BadDataFound = null,
        MissingFieldFound = null, HeaderValidated = null, IgnoreBlankLines = true, TrimOptions = TrimOptions.Trim
    };

    int fieldCount;
    List<List<string>> sampleRows;

    await using (var s = await blob.OpenReadAsync())
    using (var r = new StreamReader(s))
    using (var csv = new CsvReader(r, csvConfigNoHeader))
    {
        sampleRows = new List<List<string>>(sampleSize);
        if (!await csv.ReadAsync()) throw new InvalidOperationException("CSV is empty.");
        fieldCount = csv.Parser.Count;
        sampleRows.Add(ReadRowAsList(csv, fieldCount));
        while (sampleRows.Count < sampleSize && await csv.ReadAsync())
            sampleRows.Add(ReadRowAsList(csv, fieldCount));
    }

    log.LogInformation("Sampled {Count} rows. FieldCount={FieldCount}", sampleRows.Count, fieldCount);

    bool hasHeader = FirstRowLooksHeader(sampleRows[0]);
    var csvConfig = new CsvConfiguration(CultureInfo.InvariantCulture)
    {
        HasHeaderRecord = hasHeader, DetectDelimiter = true, BadDataFound = null,
        MissingFieldFound = null, HeaderValidated = null, IgnoreBlankLines = true, TrimOptions = TrimOptions.Trim
    };

    var profiles = Enumerable.Range(0, fieldCount).Select(i => new ColumnProfile(i)).ToList();
    long totalRows = 0;

    await using (var s2 = await blob.OpenReadAsync())
    using (var r2 = new StreamReader(s2))
    using (var csv2 = new CsvReader(r2, csvConfig))
    {
        if (hasHeader) { await csv2.ReadAsync(); csv2.ReadHeader(); }
        while (await csv2.ReadAsync())
        {
            totalRows++;
            for (int i = 0; i < fieldCount; i++)
            {
                var v = (i < csv2.Parser.Count ? csv2.GetField(i) ?? "" : "").Trim();
                profiles[i].SampleCount++;
                if (string.IsNullOrEmpty(v)) { profiles[i].EmptyCount++; continue; }
                if (DateTimeOffset.TryParse(v, out _)) profiles[i].ParsedDateCount++;
                if (double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out _)) profiles[i].ParsedDoubleCount++;
                if (v.Contains('@') && v.Contains('.')) profiles[i].LooksEmailLike = true;
                if (v.Length <= 64 && v.Any(char.IsLetter)) profiles[i].LooksAlphaCategorical = true;
                profiles[i].Distinct.Add(v);
            }
        }
    }

    foreach (var p in profiles) { p.DistinctCount = p.Distinct.Count; p.Distinct = []; }

    var sampleCsvText = BuildRawSampleCsv(sampleRows);
    await patterns.GetBlobClient($"{job.JobId}/raw.sample{sampleSize}.csv").UploadAsync(new BinaryData(sampleCsvText), overwrite: true);

    KqlPlan plan;
    try
    {
        plan = await GetKqlPlanFromAoai(job, fieldCount, sampleRows, profiles, totalRows, sampleCsvText, chatClient);
        ValidateKqlPlan(plan);

        var libraryQueries = kqlLibrary.BuildQueries();
        plan = plan with { Queries = MergeQueries(plan.Queries, libraryQueries, minQueries) };

        var heurMap = InferMappingFromProfiles(fieldCount, profiles);
        if (MappingLooksWeak(plan.Mapping))
        {
            plan = plan with { Mapping = heurMap };
            log.LogWarning("AOAI mapping weak; applied heuristic override");
        }
        log.LogInformation("Plan merged. Total queries={Count}", plan.Queries.Count);
    }
    catch (Exception aoaiEx)
    {
        log.LogError(aoaiEx, "Azure OpenAI call failed. Using library plan.");
        plan = new KqlPlan("SaaSUserAction", kqlLibrary.BuildDefaultMapping(), kqlLibrary.BuildQueries());
    }

    int rows = 0;
    await using (var s3 = await blob.OpenReadAsync())
    using (var r3 = new StreamReader(s3))
    using (var csv3 = new CsvReader(r3, csvConfig))
    {
        if (hasHeader) { await csv3.ReadAsync(); csv3.ReadHeader(); }
        while (await csv3.ReadAsync())
        {
            rows++;
            string Get(int? idx) => idx.HasValue && idx.Value >= 0 && idx.Value < csv3.Parser.Count ? (csv3.GetField(idx.Value) ?? "") : "";

            var timeRaw = Get(plan.Mapping.TimestampIndex);
            DateTimeOffset dtoTime = DateTimeOffset.TryParse(timeRaw, out var dto) ? dto : DateTimeOffset.UtcNow;
            var otherFields = Enumerable.Range(0, csv3.Parser.Count).Select(i => csv3.GetField(i) ?? "");

            var props = new Dictionary<string, string>
            {
                ["JobId"] = job.JobId, ["BlobName"] = job.BlobName, ["OriginalFileName"] = job.OriginalFileName,
                ["RowTimestamp"] = dtoTime.ToString("o"),
                ["User"] = DeriveUser(Get(plan.Mapping.UserIndex), otherFields),
                ["Role"] = NormalizeCategorical(Get(plan.Mapping.RoleIndex), "Role"),
                ["Task"] = NormalizeCategorical(Get(plan.Mapping.TaskIndex), "Task"),
                ["Action"] = NormalizeCategorical(Get(plan.Mapping.ActionIndex), "Action"),
                ["SessionId"] = DeriveSessionId(Get(plan.Mapping.SessionIdIndex), DeriveUser(Get(plan.Mapping.UserIndex), otherFields), dtoTime),
                ["FieldCount"] = fieldCount.ToString(CultureInfo.InvariantCulture),
            };
            double.TryParse(Get(plan.Mapping.DurationMsIndex), NumberStyles.Any, CultureInfo.InvariantCulture, out var durationMs);
            props["DurationMs"] = durationMs.ToString(CultureInfo.InvariantCulture);

            tc.TrackEvent(plan.EventName, props);
            if (rows % 200 == 0) tc.Flush();
        }
    }
    tc.Flush();
    log.LogInformation("Ingested {Rows} rows for JobId={JobId}", rows, job.JobId);

    foreach (var q in plan.Queries)
    {
        var query = q.Kql.Replace("{{JOBID}}", job.JobId);
        LogsTable? table = null;
        try
        {
            for (int attempt = 1; attempt <= 5; attempt++)
            {
                var resp = await logsClient.QueryWorkspaceAsync(workspaceId, query, new QueryTimeRange(TimeSpan.FromHours(24)));
                table = resp.Value.Table;
                if (table?.Rows.Count > 0) break;
                await Task.Delay(2000);
            }
        }
        catch (Exception rfe)
        {
            await patterns.GetBlobClient($"{job.JobId}/patterns.{q.Id}.ERROR.txt").UploadAsync(new BinaryData(rfe.ToString()), overwrite: true);
            continue;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"# {q.Id} - {q.Name}");
        sb.AppendLine($"# Purpose: {q.Purpose}");
        sb.AppendLine($"# AppendTo: {q.AppendTo}");

        if (table == null) sb.AppendLine("NoResults");
        else
        {
            sb.AppendLine(string.Join(',', table.Columns.Select(c => CsvEscape(c.Name))));
            foreach (var row in table.Rows)
                sb.AppendLine(string.Join(',', row.Select(v => CsvEscape(v?.ToString() ?? ""))));
        }

        await patterns.GetBlobClient($"{job.JobId}/patterns.{q.Id}.{Sanitize(q.Name)}.csv").UploadAsync(new BinaryData(sb.ToString()), overwrite: true);
    }

    var dqQuery = kqlLibrary.DataQualityQuery.Replace("{{JOBID}}", job.JobId);
    try
    {
        var resp = await logsClient.QueryWorkspaceAsync(workspaceId, dqQuery, new QueryTimeRange(TimeSpan.FromHours(24)));
        var table = resp.Value.Table;
        var sb = new StringBuilder();
        sb.AppendLine("Field,UnlabeledCount,Total,UnlabeledPct");
        if (table != null) foreach (var row in table.Rows) sb.AppendLine(string.Join(',', row.Select(v => CsvEscape(v?.ToString() ?? ""))));
        await patterns.GetBlobClient($"{job.JobId}/patterns.DQ01.UnlabeledRates.csv").UploadAsync(new BinaryData(sb.ToString()), overwrite: true);
    }
    catch (Exception dqEx) { log.LogWarning(dqEx, "Failed to produce Data Quality artifact"); }
}

static TokenCredential BuildCredential(IConfiguration config)
{
    var inAzure = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID"));
    var tenantId = config["AzureOpenAI:TenantId"] ?? config["AZURE_TENANT_ID"] ?? config["TenantId"];
    if (inAzure) return new ManagedIdentityCredential();
    return !string.IsNullOrWhiteSpace(tenantId)
        ? new AzureCliCredential(new AzureCliCredentialOptions { TenantId = tenantId })
        : new AzureCliCredential();
}

static int ParseConfigInt(IConfiguration config, string key, int defaultVal, int min, int max) =>
    int.TryParse(config[key], out var v) && v >= min && v <= max ? v : defaultVal;

static List<string> ReadRowAsList(CsvReader csv, int fieldCount) =>
    Enumerable.Range(0, fieldCount).Select(i => csv.GetField(i) ?? "").ToList();

static string BuildRawSampleCsv(List<List<string>> sampleRows) =>
    string.Join('\n', sampleRows.Select(row => string.Join(',', row.Select(CsvEscape))));

static bool MappingLooksWeak(MappingPlan map)
{
    int nulls = 0;
    if (map.TimestampIndex is null) nulls++;
    if (map.UserIndex is null) nulls++;
    if (map.RoleIndex is null) nulls++;
    if (map.TaskIndex is null) nulls++;
    if (map.ActionIndex is null) nulls++;
    if (map.DurationMsIndex is null) nulls++;
    return nulls >= 2;
}

static MappingPlan InferMappingFromProfiles(int fieldCount, List<ColumnProfile> profiles)
{
    var used = new HashSet<int>();

    var ts = profiles.OrderByDescending(p => p.ParsedDateCount).ThenBy(p => p.EmptyCount).FirstOrDefault();
    if (ts != null) used.Add(ts.Index);

    var user = profiles.Where(p => !used.Contains(p.Index)).OrderByDescending(p => p.LooksEmailLike ? 2 : 0).ThenByDescending(p => p.LooksAlphaCategorical ? 1 : 0).ThenBy(p => p.ParsedDoubleCount).FirstOrDefault();
    if (user != null) used.Add(user.Index);

    ColumnProfile? PickCategorical() => profiles.Where(p => !used.Contains(p.Index)).OrderByDescending(p => p.LooksAlphaCategorical).ThenBy(p => p.ParsedDoubleCount).ThenBy(p => p.DistinctCount).FirstOrDefault();

    var role = PickCategorical(); if (role != null) used.Add(role.Index);
    var task = PickCategorical(); if (task != null) used.Add(task.Index);
    var action = profiles.Where(p => !used.Contains(p.Index)).OrderByDescending(p => p.LooksAlphaCategorical).ThenByDescending(p => p.DistinctCount).FirstOrDefault();
    if (action != null) used.Add(action.Index);
    var duration = profiles.Where(p => !used.Contains(p.Index)).OrderByDescending(p => p.ParsedDoubleCount).FirstOrDefault();

    return new MappingPlan(
        ts?.ParsedDateCount > 0 ? ts?.Index : null, user?.Index, role?.Index,
        task?.Index, action?.Index, null, duration?.ParsedDoubleCount > 0 ? duration?.Index : null);
}

static async Task<KqlPlan> GetKqlPlanFromAoai(
    IngestionJobMessage job, int fieldCount, List<List<string>> sampleRows,
    List<ColumnProfile> profiles, long totalRows, string sampleCsvText, ChatClient chatClient)
{
    var input = new
    {
        fileName = job.OriginalFileName, fieldCount, totalRows, sampleRows, sampleCsvText,
        columnProfiles = profiles.Select(p => new
        {
            index = p.Index, sampleCount = p.SampleCount, parsedDateCount = p.ParsedDateCount,
            parsedDoubleCount = p.ParsedDoubleCount, emptyCount = p.EmptyCount, distinctCount = p.DistinctCount,
            looksEmailLike = p.LooksEmailLike, looksAlphaCategorical = p.LooksAlphaCategorical
        })
    };

    var system = new SystemChatMessage(
        "You are a telemetry analyst and KQL expert for Azure Monitor Logs using workspace-based Application Insights. " +
        "Infer CSV schema WITHOUT headers (but a header may exist), use the raw sample CSV and full-file column profiles, and generate a MULTI-QUERY KQL plan. " +
        "Return ONLY valid JSON matching the schema. No markdown. No additional text.");

    var user = new UserChatMessage(
        "We ingest each CSV row into workspace-based Application Insights as AppEvents.\n" +
        "Table: AppEvents\nEvent name: SaaSUserAction\n" +
        "Properties: JobId, SessionId, User, Role, Task, Action, RowTimestamp, BlobName, OriginalFileName\nMeasurements: DurationMs\n\n" +
        "Tasks:\n1) Infer which field index (0..fieldCount-1) best represents each canonical field: Timestamp, User, Role, Task, Action, SessionId, DurationMs. If not present, set null.\n" +
        "2) Propose 12 to 25 KQL queries against AppEvents for a JobId placeholder {{JOBID}}. EVERY query MUST filter by JobId == '{{JOBID}}' and use Properties.RowTimestamp (RowTs) for ordering.\n" +
        "3) Return eventName 'SaaSUserAction'.\n4) Input JSON:\n" + JsonSerializer.Serialize(input));

    var response = await chatClient.CompleteChatAsync([system, user], new ChatCompletionOptions { TopP = 1.0f });
    var text = response.Value.Content[0].Text ?? "";

    var plan = JsonSerializer.Deserialize<KqlPlan>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidOperationException("AOAI did not return a valid KqlPlan JSON.");
    if (plan.Queries == null || plan.Queries.Count == 0) throw new InvalidOperationException("AOAI returned zero queries.");
    return plan;
}

static string CsvEscape(string s) { s ??= ""; return '"' + s.Replace("\"", "\"\"") + '"'; }
static string Sanitize(string s) { var bad = Path.GetInvalidFileNameChars(); return new string(s.Select(ch => bad.Contains(ch) ? '_' : ch).ToArray()); }

static void ValidateKqlPlan(KqlPlan plan)
{
    if (plan.Queries == null || plan.Queries.Count == 0) throw new InvalidOperationException("Plan has zero queries.");
    foreach (var q in plan.Queries)
    {
        if (string.IsNullOrWhiteSpace(q.Kql)) throw new InvalidOperationException($"Query {q.Id} has empty KQL.");
        if (!q.Kql.Contains("{{JOBID}}")) throw new InvalidOperationException($"Query {q.Id} must include {{JOBID}} placeholder.");
        var k = q.Kql.ToLowerInvariant();
        if (!k.Contains("appevents")) throw new InvalidOperationException($"Query {q.Id} must target AppEvents.");
        if (!(k.Contains("rowtimestamp") || k.Contains("rowts"))) throw new InvalidOperationException($"Query {q.Id} must use RowTimestamp/RowTs.");
        if (k.Contains(".set") || k.Contains(".append") || k.Contains(".delete") || k.Contains(".drop"))
            throw new InvalidOperationException($"Query {q.Id} contains control commands.");
    }
}

static List<KqlQuery> MergeQueries(List<KqlQuery> aoai, List<KqlQuery> library, int minCount)
{
    var merged = new List<KqlQuery>(aoai ?? []);
    var seen = new HashSet<string>(merged.Select(q => q.Id), StringComparer.OrdinalIgnoreCase);
    foreach (var q in library) { if (merged.Count >= minCount) break; if (seen.Add(q.Id)) merged.Add(q); }
    return merged.Count == 0 ? library : merged;
}

static bool FirstRowLooksHeader(List<string> firstRow)
{
    int alpha = firstRow.Count(v => !string.IsNullOrEmpty(v) && v.Any(char.IsLetter));
    int numeric = firstRow.Count(v => double.TryParse(v ?? "", NumberStyles.Any, CultureInfo.InvariantCulture, out _));
    int dates = firstRow.Count(v => DateTimeOffset.TryParse(v ?? "", out _));
    return alpha >= Math.Max(3, firstRow.Count / 3) && (numeric + dates) <= firstRow.Count / 4;
}

static string NormalizeCategorical(string raw, string prefix)
{
    raw = (raw ?? "").Trim();
    if (string.IsNullOrEmpty(raw)) return "unknown";
    return (raw.All(char.IsDigit) || Guid.TryParse(raw, out _)) ? $"{prefix}_{raw}" : raw;
}

static string DeriveUser(string raw, IEnumerable<string> otherFields)
{
    raw = (raw ?? "").Trim();
    if (!string.IsNullOrEmpty(raw)) return raw;
    foreach (var f in otherFields) { var v = (f ?? "").Trim(); if (v.Contains('@') && v.Contains('.')) return v.ToLowerInvariant(); }
    var concat = string.Join("|", otherFields).Trim();
    if (string.IsNullOrEmpty(concat)) return "unknown";
    return $"user_{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(concat))).TrimEnd('=').Replace('+', '-').Replace('/', '_')[..12]}";
}

static string DeriveSessionId(string existing, string user, DateTimeOffset ts)
{
    existing = (existing ?? "").Trim();
    if (!string.IsNullOrEmpty(existing)) return existing;
    if (!DateTimeOffset.TryParse(ts.ToString("o"), out var dto)) dto = DateTimeOffset.UtcNow;
    var bucket = new DateTimeOffset(dto.Year, dto.Month, dto.Day, dto.Hour, (dto.Minute / 15) * 15, 0, TimeSpan.Zero);
    return $"{user}_{bucket:yyyyMMddHHmm}";
}

static string TryDecodeToJson(string text)
{
    if (LooksLikeJson(text)) return text;
    if (TryBase64(text, out var d1)) { if (LooksLikeJson(d1)) return d1; if (TryBase64(d1, out var d2) && LooksLikeJson(d2)) return d2; }
    return text;
}
static bool LooksLikeJson(string s) => !string.IsNullOrWhiteSpace(s) && (s.TrimStart().StartsWith('{') || s.TrimStart().StartsWith('['));
static bool TryBase64(string input, out string decoded) { decoded = ""; try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(input)); return true; } catch { return false; } }
static string Must(string? v, string name) { v = v?.Trim(); if (string.IsNullOrWhiteSpace(v)) throw new InvalidOperationException($"Missing config: {name}"); return v; }
static Uri MustUri(string v, string name) { v = (v ?? "").Trim().Trim('"'); if (!Uri.TryCreate(v, UriKind.Absolute, out var uri)) throw new InvalidOperationException($"Invalid URI for {name}: '{v}'"); return uri; }

public record IngestionJobMessage(string JobId, string BlobName, string OriginalFileName, DateTimeOffset UploadedAt);
public record MappingPlan(int? TimestampIndex, int? UserIndex, int? RoleIndex, int? TaskIndex, int? ActionIndex, int? SessionIdIndex, int? DurationMsIndex);
public record KqlQuery(string Id, string Name, string Purpose, string AppendTo, string[] OutputColumns, string Kql);
public record KqlPlan(string EventName, MappingPlan Mapping, List<KqlQuery> Queries);

public class ColumnProfile
{
    public int Index { get; set; }
    public long SampleCount { get; set; }
    public long ParsedDateCount { get; set; }
    public long ParsedDoubleCount { get; set; }
    public long EmptyCount { get; set; }
    public long DistinctCount { get; set; }
    public bool LooksEmailLike { get; set; }
    public bool LooksAlphaCategorical { get; set; }
    public HashSet<string> Distinct { get; set; } = [];
    public ColumnProfile(int index) { Index = index; }
}

public class KqlLibrary
{
    public KqlLibraryPreamble Preamble { get; set; } = new();
    public KqlLibraryMapping DefaultMapping { get; set; } = new();
    public List<KqlLibraryQuery> Queries { get; set; } = [];
    public string DataQualityQuery { get; set; } = "";

    public static KqlLibrary Load(string path)
    {
        var json = File.ReadAllText(path);
        var doc = JsonSerializer.Deserialize<JsonElement>(json);

        var lib = new KqlLibrary();
        lib.Preamble = new KqlLibraryPreamble { Normalized = doc.GetProperty("preamble").GetProperty("normalized").GetString() ?? "" };

        var dm = doc.GetProperty("defaultMapping");
        lib.DefaultMapping = new KqlLibraryMapping
        {
            TimestampIndex = dm.TryGetProperty("timestampIndex", out var ti) && ti.ValueKind == JsonValueKind.Number ? ti.GetInt32() : null,
            UserIndex = dm.TryGetProperty("userIndex", out var ui) && ui.ValueKind == JsonValueKind.Number ? ui.GetInt32() : null,
            RoleIndex = dm.TryGetProperty("roleIndex", out var ri) && ri.ValueKind == JsonValueKind.Number ? ri.GetInt32() : null,
            TaskIndex = dm.TryGetProperty("taskIndex", out var tki) && tki.ValueKind == JsonValueKind.Number ? tki.GetInt32() : null,
            ActionIndex = dm.TryGetProperty("actionIndex", out var ai) && ai.ValueKind == JsonValueKind.Number ? ai.GetInt32() : null,
            SessionIdIndex = dm.TryGetProperty("sessionIdIndex", out var si) && si.ValueKind == JsonValueKind.Number ? si.GetInt32() : null,
            DurationMsIndex = dm.TryGetProperty("durationMsIndex", out var di) && di.ValueKind == JsonValueKind.Number ? di.GetInt32() : null,
        };

        foreach (var q in doc.GetProperty("queries").EnumerateArray())
        {
            lib.Queries.Add(new KqlLibraryQuery
            {
                Id = q.GetProperty("id").GetString() ?? "",
                Name = q.GetProperty("name").GetString() ?? "",
                Purpose = q.GetProperty("purpose").GetString() ?? "",
                AppendTo = q.GetProperty("appendTo").GetString() ?? "",
                OutputColumns = q.GetProperty("outputColumns").EnumerateArray().Select(c => c.GetString() ?? "").ToArray(),
                Suffix = q.GetProperty("suffix").GetString() ?? "",
                UsesLetOnly = q.TryGetProperty("usesLetOnly", out var ulo) && ulo.GetBoolean(),
            });
        }

        lib.DataQualityQuery = doc.GetProperty("dataQualityQuery").GetString() ?? "";
        return lib;
    }

    public MappingPlan BuildDefaultMapping() => new(
        DefaultMapping.TimestampIndex, DefaultMapping.UserIndex, DefaultMapping.RoleIndex,
        DefaultMapping.TaskIndex, DefaultMapping.ActionIndex, DefaultMapping.SessionIdIndex, DefaultMapping.DurationMsIndex);

    public List<KqlQuery> BuildQueries()
    {
        var preamble = Preamble.Normalized;
        return Queries.Select(q =>
        {
            string kql;
            if (q.UsesLetOnly)
                kql = preamble + q.Suffix;
            else
                kql = preamble + "\n_raw" + q.Suffix;

            return new KqlQuery(q.Id, q.Name, q.Purpose, q.AppendTo, q.OutputColumns, kql);
        }).ToList();
    }
}

public class KqlLibraryPreamble { public string Normalized { get; set; } = ""; }
public class KqlLibraryMapping
{
    public int? TimestampIndex { get; set; }
    public int? UserIndex { get; set; }
    public int? RoleIndex { get; set; }
    public int? TaskIndex { get; set; }
    public int? ActionIndex { get; set; }
    public int? SessionIdIndex { get; set; }
    public int? DurationMsIndex { get; set; }
}
public class KqlLibraryQuery
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string AppendTo { get; set; } = "";
    public string[] OutputColumns { get; set; } = [];
    public string Suffix { get; set; } = "";
    public bool UsesLetOnly { get; set; }
}
