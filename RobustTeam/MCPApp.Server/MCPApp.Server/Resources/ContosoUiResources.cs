using ModelContextProtocol.Server;
using System.Text;

[McpServerResourceType]
public class ContosoUiResources
{
    [McpServerResource(
        UriTemplate = "ui://contoso/dashboard",
        Name = "Contoso Dashboard",
        Title = "Contoso KPI Dashboard",
        MimeType = "text/html+mcp")]
    public async Task<string> Dashboard(CancellationToken ct)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Ui", "dashboard.html");
        return await File.ReadAllTextAsync(path, Encoding.UTF8, ct);
    }

    [McpServerResource(
        UriTemplate = "ui://contoso/kpi-history",
        Name = "KPI History",
        Title = "Contoso KPI History",
        MimeType = "text/html+mcp")]
    public async Task<string> KpiHistory2(CancellationToken ct)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Ui", "kpi-history.html");
        return await File.ReadAllTextAsync(path, Encoding.UTF8, ct);
    }
}
