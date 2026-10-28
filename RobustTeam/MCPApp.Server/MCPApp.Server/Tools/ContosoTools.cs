using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ContosoMcpApp;

public record KpiResult(
    [property: JsonPropertyName("revenue")] decimal Revenue,
    [property: JsonPropertyName("churnRate")] decimal ChurnRate,
    [property: JsonPropertyName("activeUsers")] int ActiveUsers);

[McpServerToolType]
public class ContosoTools
{
    [McpServerTool(
        Name = "contoso_get_kpis",
        Title = "Get Contoso KPIs",
        IconSource = "https://contoso.example.com/icons/kpi.png",
        UseStructuredContent = true,
        TaskSupport = ToolTaskSupport.Optional)]
    [McpAppUi("ui://contoso/dashboard")]  // ✅ ADD THIS LINE
    public KpiResult GetKpis()
    {
        return new KpiResult(
            Revenue: 1250000.50m,
            ChurnRate: 3.2m,
            ActiveUsers: 48213);
    }
        
    [McpServerTool(
        Name = "contoso_refresh_dashboard",
        Title = "Refresh Dashboard Data",
        IconSource = "https://contoso.example.com/icons/refresh.png",
        UseStructuredContent = false,
        TaskSupport = ToolTaskSupport.Required)]
    public async Task<string> RefreshDashboardAsync(CancellationToken ct)
    {
        // Simulate a longer-running operation (task support demo)
        await Task.Delay(TimeSpan.FromSeconds(3), ct);
        return "Dashboard data refreshed at " + DateTimeOffset.UtcNow.ToString("u");
    }

    [McpServerTool(
        Name = "contoso_get_kpi_history",
        Title = "Get KPI History",
        IconSource = "https://contoso.example.com/icons/history.png",
        UseStructuredContent = true,
        TaskSupport = ToolTaskSupport.Optional)]
    public object GetKpiHistory()
    {
        // Simple time series for charting
        return new
        {
            points = new[]
            {
                new { label = "Jan", revenue = 900000, churnRate = 4.1 },
                new { label = "Feb", revenue = 1000000, churnRate = 3.8 },
                new { label = "Mar", revenue = 1150000, churnRate = 3.5 },
                new { label = "Apr", revenue = 1250000, churnRate = 3.2 },
            }
        };
    }
}
