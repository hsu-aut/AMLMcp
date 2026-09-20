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
          --follow <file>         Answer about the document whose path this file contains, as long as
                                  the client names none. An editor plugin keeps the file up to date.
                                  Environment: AML_MCP_FOLLOW
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
    .AddMcpServer(server => server.ServerInstructions = Instructions(options))
    .WithStdioServerTransport()
    .WithToolsFromAssembly()
    .WithResourcesFromAssembly()
    .WithPromptsFromAssembly();

await builder.Build().RunAsync();
return 0;

static string Instructions(ServerOptions options)
{
    var text = new System.Text.StringBuilder();
    text.AppendLine("Reads AutomationML documents (CAEX 3.0, CAEX 2.15, AMLX). Every answer names element IDs and paths, so state them when you report a fact.");
    if (options.FollowFile is not null)
        text.AppendLine("The user has an AutomationML editor open. Call open_aml_document WITHOUT a path to read the document they are looking at right now; the other tools then work on it as well. Do not ask for a file path or an upload first.");
    else
        text.AppendLine("Call open_aml_document with the path of a file first; the other tools then work on it.");
    // Clients that load tools on demand search by name and description. Naming the tools
    // here keeps a failed search from looking like a server without those tools.
    text.AppendLine("Tools: open_aml_document (overview), get_tree (structure), find_elements (search), " +
                    "get_element (one element with its attributes, interfaces and every link), get_neighbors " +
                    "(follow links and references), find_path (how two elements are connected), get_class, " +
                    "list_classes, check_references. Ask for them by name if your client loads tools on demand.");
    if (options.Roots.Count > 0)
        text.AppendLine($"Readable directories: {options.RootsDescription}.");
    return text.ToString();
}
