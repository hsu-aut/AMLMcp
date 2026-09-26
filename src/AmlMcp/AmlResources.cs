using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using static AmlMcp.AmlModel;

namespace AmlMcp;

/// <summary>
/// Resources let a client attach context without calling a tool: which documents are
/// open, and which class libraries the current document can draw on.
/// </summary>
[McpServerResourceType]
public static class AmlResources
{
    [McpServerResource(UriTemplate = "aml://documents", Name = "open_documents", MimeType = "text/plain")]
    [Description("The AutomationML documents this server currently has open.")]
    public static string OpenDocuments(DocumentStore store)
    {
        var open = store.OpenDocuments;
        if (open.Count == 0) return "No document is open. Use the open_aml_document tool.";
        var sb = new StringBuilder($"{open.Count} open document(s):\n");
        foreach (var path in open) sb.AppendLine("  " + path);
        sb.AppendLine($"\nReadable directories: {store.Options.RootsDescription}");
        return sb.ToString();
    }

    [McpServerResource(UriTemplate = "aml://libraries", Name = "class_libraries", MimeType = "text/plain")]
    [Description("Class libraries available to the document that was opened last, with their classes.")]
    public static string ClassLibraries(DocumentStore store)
    {
        var m = store.Get(null);
        var sb = new StringBuilder($"Class libraries of {Path.GetFileName(m.FilePath)}:\n");
        // A referenced library can hold thousands of classes and this resource is read whole,
        // so it is capped like every tool answer, and it says what it left out.
        const int perLibrary = 60;
        const int total = 400;
        var listed = 0;
        var omitted = 0;

        foreach (var library in m.Classes.Values.GroupBy(c => c.Library).OrderBy(g => g.Key))
        {
            var external = library.First().External;
            sb.AppendLine($"\n{library.Key}{(external ? "  (external file)" : "  (embedded)")}, {library.Count()} classes");
            var shown = 0;
            foreach (var cls in library.OrderBy(c => c.PlainPath))
            {
                if (shown >= perLibrary || listed >= total) { omitted++; continue; }
                sb.AppendLine($"  {cls.PlainPath}  ({cls.Kind})");
                shown++;
                listed++;
            }
        }

        if (omitted > 0)
            sb.AppendLine($"\n{omitted} class(es) not listed here. Use list_classes to filter them, or get_class for one.");
        return sb.ToString();
    }
}
