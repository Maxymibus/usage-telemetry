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

    private const string SessionChatKey = "ChatHistoryDtos";
    private const string SessionFilesKey = "UploadedFiles";

    private string UploadPath =>
        Path.Combine(_env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"), "Uploads");

    public FromSaaSLogsController(IWebHostEnvironment env, IStorageService storage, IChatService chat)
    {
        _env = env;
        _storage = storage;
        _chat = chat;
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

        var csvTexts = await _storage.LoadEvidenceCsvsAsync(req.JobId.Trim());
        if (csvTexts.Count == 0)
            return HtmlContent("<p>No evidence CSVs found yet. Wait for processing to complete.</p>");

        var evidence = EvidenceBuilder.Build(csvTexts);
        var html = await _chat.GenerateUseCasesAsync(evidence);
        return Content(html, "text/html");
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
