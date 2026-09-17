using AmlMcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("""
        aml-mcp: a read-only Model Context Protocol server for AutomationML (CAEX 3.0, CAEX 2.15, AMLX).

        Usage: aml-mcp [options]
          --root <dir>            Restrict reading to this directory (repeatable).
                                  Environment: AML_MCP_ROOTS, separated by the platform path separator.
          --strict-conventions    Interpret only what CAEX and the shared AutomationML libraries define,
                                  without the attribute name conventions of older libraries.
                                  Environment: AML_MCP_STRICT=1
          --help                  Show this text.
          --version               Show the version.

        The server speaks MCP over stdio and is meant to be started by an MCP client.
        """);
    return 0;
}

if (args.Contains("--version"))
{
    Console.WriteLine(typeof(AmlModel).Assembly.GetName().Version?.ToString(3) ?? "unknown");
    return 0;
}

ServerOptions options;
try
{
    options = ServerOptions.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"aml-mcp: {ex.Message}");
    return 2;
}

Console.Error.WriteLine($"aml-mcp: readable directories: {options.RootsDescription}");

var builder = Host.CreateApplicationBuilder(args);

// stdout belongs to the MCP protocol; all logging goes to stderr.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<DocumentStore>();
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly()
    .WithResourcesFromAssembly()
    .WithPromptsFromAssembly();

await builder.Build().RunAsync();
return 0;
