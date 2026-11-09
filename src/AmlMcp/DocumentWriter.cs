using System.Text;
using System.Xml.Linq;
using ModelContextProtocol;

namespace AmlMcp;

/// <summary>
/// What a write produced: the document as it now is, its check, where it went, and anything
/// the caller has to know besides.
/// </summary>
public sealed record WriteOutcome(AmlModel Model, CheckInfo Check, string? WrittenTo, string? Note = null);

/// <summary>
/// Writes a described document, and proves it first. The order matters: the fence and the
/// target are settled before anything is rendered, so a caller who may not write learns
/// that at once. A preview renders and checks in memory and touches no disk at all. A real
/// write goes to a temporary file beside the target, is read back with the ordinary reader
/// and run through the ordinary check, and only then replaces anything. What the check
/// calls a problem is never written.
/// </summary>
public static class DocumentWriter
{
    public static WriteOutcome Write(DocumentStore store, string path, DocumentSpec spec, bool preview, bool overwrite)
    {
        var options = store.Options;
        var target = Path.GetFullPath(path);

        if (!preview)
        {
            if (options.WriteRoots.Count == 0)
                throw new McpException(
                    "This server may not write. It was started without --write <dir>; ask the user to add one, " +
                    "or call again with preview to see what would be written.");

            if (!options.AllowsWrite(target))
                throw new McpException(
                    $"'{target}' is outside the directories this server may write ({options.WriteRootsDescription}).");

            if (Directory.Exists(target))
                throw new McpException($"'{target}' is a directory. Name the file to write.");

            if (!target.EndsWith(".aml", StringComparison.OrdinalIgnoreCase))
                throw new McpException($"'{target}' is no .aml file. Stage one writes a single .aml document.");

            if (store.FollowedDocument() is { } open && string.Equals(open, target, StringComparison.OrdinalIgnoreCase))
                throw new McpException(
                    $"'{target}' is the document the editor has open. The editor would not know about the change, " +
                    "so it is not written. Write to another file, or ask the user to close it first.");

            if (File.Exists(target) && !overwrite)
                throw new McpException($"'{target}' already exists. Ask again with overwrite to replace it.");
        }

        var faults = WriteSpec.Validate(spec);
        if (faults.Count > 0)
            throw new McpException("This description cannot be written:" + Environment.NewLine +
                                   string.Join(Environment.NewLine, faults.Select(f => "  " + f)));

        var document = CaexWriter.Document(SpecCompletion.Complete(spec), Path.GetFileName(target));

        if (preview)
        {
            // Nothing is written: the tree is checked where the file would lie, so a relative
            // ExternalReference resolves from the same directory it will resolve from later.
            var previewed = AmlModel.FromTree(document, target, options);
            Refuse(Structured.Check(previewed));
            return new WriteOutcome(previewed, Structured.Check(previewed), null);
        }

        // Beside the target, so the move afterwards stays on one volume and a relative
        // reference resolves from the right directory. The name carries a GUID: a file that
        // happens to be called written.aml.tmp is none of our business.
        var temporary = Path.Combine(Path.GetDirectoryName(target)!, $".aml-mcp-{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
            using (var writer = new StreamWriter(temporary, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                new XDocument(new XDeclaration("1.0", "utf-8", null), document).Save(writer);

            // Proving the bytes, not only the tree: what lands is what was read back.
            Refuse(Structured.Check(AmlModel.Load(temporary, options)));
            MoveIntoPlace(temporary, target);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        // The document the caller just created becomes the open one, so the read tools work
        // on it without a path. A write directory outside every read directory makes that
        // impossible, and then the answer says so instead of leaving the caller to find out.
        try
        {
            var written = store.Open(target);
            return new WriteOutcome(written, Structured.Check(written), target);
        }
        catch (McpException refused)
        {
            var model = AmlModel.Load(target, options);
            return new WriteOutcome(model, Structured.Check(model), target,
                $"The file was written, but this server may not read it back: {refused.Message} " +
                "The read tools will refuse it until the directory is readable as well (--root).");
        }
    }

    private static void Refuse(CheckInfo check)
    {
        if (check.Problems.Count == 0) return;
        throw new McpException(
            "The described document would not resolve, so nothing was written:" + Environment.NewLine +
            string.Join(Environment.NewLine, check.Problems.Select(p => $"  {p.Kind}: {p.Message}")));
    }

    /// <summary>
    /// The same atomic move the editor handshake uses: a reader never sees half a document,
    /// and a reader holding the old file does not make the write fail.
    /// </summary>
    private static void MoveIntoPlace(string temporary, string target)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temporary, target, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(30);
            }
        }
    }
}
