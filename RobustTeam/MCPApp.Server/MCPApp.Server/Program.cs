using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

Console.SetOut(TextWriter.Null);

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(o =>
{
    o.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport() 
    .WithToolsFromAssembly()
    .WithResourcesFromAssembly(typeof(ContosoUiResources).Assembly);

await builder.Build().RunAsync();
