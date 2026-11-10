using AmlMcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("""
        aml-mcp: a Model Context Protocol server for AutomationML (CAEX 3.0, CAEX 2.15, AMLX).
        It reads; it writes only where --write allows it.

        Usage: aml-mcp [options]
          --root <dir>            Restrict reading to this directory (repeatable).
                                  Environment: AML_MCP_ROOTS, separated by the platform path separator.
          --write <dir>           Allow writing below this directory (repeatable). Without it the
                                  server writes nothing. A readable directory is not a writable one.
                                  Environment: AML_MCP_WRITE, separated by the platform path separator.
          --follow <file>         Answer about the document whose path this file contains, as long as
                                  the client names none. An editor plugin keeps the file up to date.
                                  Environment: AML_MCP_FOLLOW
          --select <file>         Let show_in_editor point an editor at an element by writing its ID
                                  into this file. An editor plugin watches it.
                                  Environment: AML_MCP_SELECT
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
Console.Error.WriteLine($"aml-mcp: writable directories: {options.WriteRootsDescription}");

// A document written where the server may not read is a document its own tools refuse
// afterwards. Worth a word at startup, not only in the answer that hits it.
foreach (var writable in options.WriteRoots.Where(d => !options.Allows(d)))
    Console.Error.WriteLine($"aml-mcp: warning: '{writable}' may be written but not read. " +
                            "Add it to --root as well, or the read tools will refuse what was written there.");

var builder = Host.CreateApplicationBuilder(args);

// stdout belongs to the MCP protocol; all logging goes to stderr. Only what the operator
// needs to see: a request-by-request log of a local server is noise in their client.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<DocumentStore>();
builder.Services
    .AddMcpServer(server => server.ServerInstructions = ServerInstructions.For(options))
    .WithStdioServerTransport()
    .WithToolsFromAssembly()
    .WithResourcesFromAssembly()
    .WithPromptsFromAssembly();

await builder.Build().RunAsync();
return 0;

