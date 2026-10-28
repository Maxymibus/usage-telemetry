using ModelContextProtocol.Server;

public sealed class ContosoKpiTool
{
    public object Invoke(string region)
    {
        return new
        {
            Region = region,
            Revenue = 125_000_000,
            Growth = "12%",
            TopAccounts = new[]
            {
                "Fabrikam",
                "Northwind",
                "Adventure Works"
            }
        };
    }
}