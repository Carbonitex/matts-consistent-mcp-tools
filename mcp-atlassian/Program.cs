using McpAtlassian.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    try { Console.Error.WriteLine($"[mcp-atlassian] Unhandled: {e.ExceptionObject}"); } catch { /* ignore */ }
};

TaskScheduler.UnobservedTaskException += (_, e) =>
{
    try { Console.Error.WriteLine($"[mcp-atlassian] Unobserved task: {e.Exception}"); } catch { /* ignore */ }
    e.SetObserved();
};

var builder = Host.CreateApplicationBuilder(args);

// stdout is the MCP channel; everything else goes to stderr.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

var options = AtlassianOptions.FromEnvironment();
if (!options.IsConfigured)
    Console.Error.WriteLine("[mcp-atlassian] Warning: ATLASSIAN_SITE / ATLASSIAN_EMAIL / ATLASSIAN_API_TOKEN not set; tools will return a configuration error.");

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<AtlassianClient>();

builder.Services
    .AddMcpServer(o => o.ServerInfo = new() { Name = "atlassian", Version = "1.0.0" })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
