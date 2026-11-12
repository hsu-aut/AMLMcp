using System.Text;

namespace AmlMcp;

/// <summary>
/// What the server tells a client about itself when it connects. It depends on how the
/// server was started: a client whose user has an editor open is told to read that
/// document, and writing is only mentioned where writing is allowed.
/// </summary>
public static class ServerInstructions
{
    public static string For(ServerOptions options)
    {
        var text = new StringBuilder();
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
                        "list_classes, check_references" +
                        (options.SelectFile is not null ? ", show_in_editor" : "") +
                        (options.WriteRoots.Count > 0 ? ", write_aml_document" : "") +
                        ". Ask for them by name if your client loads tools on demand.");
        text.AppendLine("Several documents can be open at once: every tool takes a document path, and leaving it out means the one named or opened last.");
        if (options.SelectFile is not null)
            text.AppendLine("You can point the user's editor at an element with show_in_editor. Use it when you name an element they would otherwise have to look up by hand.");
        if (options.WriteRoots.Count > 0)
            text.AppendLine("You can create a document with write_aml_document: describe it and the server renders the CAEX, " +
                            "generates the IDs, validates the result against the CAEX 3.0 schema and refuses anything that " +
                            "would not resolve. Call it with preview first and show " +
                            "the user the overview and the check before you write. " +
                            $"Writable directories: {options.WriteRootsDescription}.");
        if (options.Roots.Count > 0)
            text.AppendLine($"Readable directories: {options.RootsDescription}.");
        return text.ToString();
    }
}
