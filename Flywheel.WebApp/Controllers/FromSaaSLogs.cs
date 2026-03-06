using System.Globalization;
using System.Text;
using CsvHelper;
using Microsoft.AspNetCore.Mvc;
using UglyToad.PdfPig;
using Flywheel.Models;
using Flywheel.Services;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace Flywheel.Controllers;

[Route("[controller]")]
public sealed class FromSaaSLogsController : Controller
{
    private readonly IWebHostEnvironment _env;
    private readonly IStorageService _storage;
    private readonly IChatService _chat;
    private readonly ISearchService _search;

    private const string SessionChatKey = "ChatHistoryDtos";
    private const string SessionFilesKey = "UploadedFiles";

    private string UploadPath =>
        Path.Combine(_env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"), "Uploads");

    public FromSaaSLogsController(
        IWebHostEnvironment env,
        IStorageService storage,
        IChatService chat,
        ISearchService search)
    {
        _env = env;
        _storage = storage;
        _chat = chat;
        _search = search;
    }

    [HttpPost("CreateUploadSas")]
    public async Task<IActionResult> CreateUploadSasAsync([FromForm] string fileName)
    {
        var result = await _storage.CreateUploadSasAsync(fileName);
        return Json(new { result.JobId, result.BlobName, uploadUrl = result.UploadUrl });
    }

    [HttpPost("EnqueueJob")]
    public async Task<IActionResult> EnqueueJob([FromBody] IngestionJobMessage msg)
    {
        await _storage.EnqueueJobAsync(JsonSerializer.Serialize(msg));
        return Json(new { ok = true });
    }

    [HttpPost("UploadLogs")]
    public async Task<IActionResult> UploadLogs(List<IFormFile> files)
    {
        if (files is not { Count: > 0 })
            return Content("Please select at least one CSV or PDF file.");

        Directory.CreateDirectory(UploadPath);

        var allowedExtensions = new HashSet<string> { ".csv", ".pdf" };
        var uploadedPaths = new List<string>();
        var logBuilder = new StringBuilder();
        var pdfText = new StringBuilder();

        foreach (var file in files)
        {
            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (!allowedExtensions.Contains(ext))
                return Content("Only CSV and PDF files are allowed.");

            var safeName = Path.GetFileName(file.FileName);
            var filePath = Path.Combine(UploadPath, safeName);

            await using (var ws = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None))
                await file.CopyToAsync(ws);

            uploadedPaths.Add(filePath);

            if (ext == ".csv") AppendCsvContent(filePath, safeName, logBuilder);
            else if (ext == ".pdf") AppendPdfContent(filePath, pdfText);
        }

        var initialPrompt = $"{Prompts.OpportunityDiscovery}\n\n### SaaS Logs:\n{logBuilder}\n\n### PDF content:\n{pdfText}";

        var history = new List<ChatMessageDto>
        {
            new("system", "You are an AI architect with deep expertise in agent design."),
            new("user", initialPrompt),
        };

        HttpContext.Session.SetObject(SessionChatKey, history);
        HttpContext.Session.SetObject(SessionFilesKey, uploadedPaths);

        return Content($"{files.Count} file(s) uploaded and context initialized.");
    }

    [HttpPost("ExtractAiFeatures")]
    public async Task<IActionResult> ExtractAiFeatures() =>
        HtmlContent(await ContinueConversationAsync("Give the output as HTML."));

    [HttpPost("ExtractDiscoveryCards")]
    public async Task<IActionResult> ExtractDiscoveryCards() =>
        HtmlContent(await ContinueConversationAsync("Extract Relevant AI Discovery Cards as HTML."));

    [HttpPost("ExtractAgentArchitecture")]
    public async Task<IActionResult> ExtractAgentArchitecture() =>
        HtmlContent(await ContinueConversationAsync("Extract Suggested Agent Architecture for Multi-Agent System as HTML."));

    [HttpPost("ExtractUiDesign")]
    public async Task<IActionResult> ExtractUiDesign() =>
        HtmlContent(await ContinueConversationAsync("Extract UI Design Recommendations as HTML."));

    [HttpPost("ExtractAgenticFlow")]
    public async Task<IActionResult> ExtractAgenticFlow() =>
        HtmlContent(await ContinueConversationAsync("Extract Agentic Flow Details as HTML with Mermaid.js visualisation."));

    [HttpGet("JobStatus")]
    public async Task<IActionResult> JobStatus(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId)) return BadRequest("jobId is required.");
        return Json(await _storage.GetJobStatusAsync(jobId));
    }

    [HttpPost("GenerateUseCases")]
    public async Task<IActionResult> GenerateUseCases([FromBody] GenerateUseCasesRequest? req)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.JobId))
            return HtmlContent("<p>jobId is required.</p>");

        var jobId = req.JobId.Trim();
        var csvTexts = await _storage.LoadEvidenceCsvsAsync(jobId);
        if (csvTexts.Count == 0)
            return HtmlContent("<p>No evidence CSVs found yet. Wait for processing to complete.</p>");

        var evidence = EvidenceBuilder.Build(csvTexts);

        var grounding = "";
        if (_search.IsEnabled)
        {
            grounding = await _search.RetrieveGroundingAsync(
                "Analyze SaaS usage patterns, identify agent automation opportunities, " +
                "friction points, rework loops, and suggest multi-agent architectures " +
                "with discovery cards grounded in behavioral evidence.");
        }

        var discoveryHtml = await _chat.RunDiscoveryPassAsync(evidence, grounding);

        var architectureHtml = await _chat.RunArchitecturePassAsync(evidence, discoveryHtml);

        var combined = $"""
            <div class='generated-html'>
                <style>
                    .pass-header {{
                        display: flex;
                        align-items: center;
                        gap: 10px;
                        padding: 12px 16px;
                        margin: 24px 0 16px 0;
                        border-radius: 8px;
                        font-size: 1.1em;
                        font-weight: 600;
                    }}
                    .pass-discovery {{
                        background: linear-gradient(135deg, #1e3a5f 0%, #2d5a87 100%);
                        color: #7dd3fc;
                        border: 1px solid #38bdf8;
                    }}
                    .pass-architecture {{
                        background: linear-gradient(135deg, #1a2e05 0%, #2d4a0a 100%);
                        color: #86efac;
                        border: 1px solid #4ade80;
                    }}
                    .pass-tag {{
                        font-size: 0.7em;
                        padding: 2px 8px;
                        border-radius: 4px;
                        text-transform: uppercase;
                        letter-spacing: 0.05em;
                    }}
                    .tag-rag {{ background: #0c4a6e; color: #7dd3fc; }}
                    .tag-precise {{ background: #14532d; color: #86efac; }}
                </style>

                <div class="pass-header pass-discovery">
                    <span>Pass 1: Discovery &amp; Opportunity Clustering</span>
                    <span class="pass-tag tag-rag">RAG-Grounded</span>
                </div>
                {discoveryHtml}

                <div class="pass-header pass-architecture">
                    <span>Pass 2: Production Architecture &amp; Hardening</span>
                    <span class="pass-tag tag-precise">Evidence-Precise</span>
                </div>
                {architectureHtml}
            </div>
            """;

        return Content(combined, "text/html");
    }

    private async Task<string> ContinueConversationAsync(string prompt)
    {
        var history = HttpContext.Session.GetObject<List<ChatMessageDto>>(SessionChatKey) ?? [];
        if (history.Count == 0) return "<p>No session context found. Please upload logs first.</p>";

        history.Add(new ChatMessageDto("user", $"{prompt} Do not include ```html at the beginning."));
        var text = await _chat.CompleteChatAsync(history);
        history.Add(new ChatMessageDto("assistant", text));
        HttpContext.Session.SetObject(SessionChatKey, history);

        return System.Net.WebUtility.HtmlDecode(text);
    }

    private ContentResult HtmlContent(string html) =>
        Content($"<div class='generated-html'>{html}</div>", "text/html");

    private static void AppendCsvContent(string filePath, string safeName, StringBuilder sb)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(stream);
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);

        sb.AppendLine($"--- Log File: {safeName} ---");
        foreach (var record in csv.GetRecords<dynamic>().Take(200))
        {
            foreach (var kvp in (IDictionary<string, object>)record)
                sb.Append($"{kvp.Key}: {kvp.Value} | ");
            sb.AppendLine();
        }
    }

    private static void AppendPdfContent(string filePath, StringBuilder sb)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var pdf = PdfDocument.Open(stream);
        foreach (var page in pdf.GetPages()) sb.AppendLine(page.Text);
    }
}
