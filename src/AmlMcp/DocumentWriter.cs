using System.Text;
using System.Xml.Linq;
using ModelContextProtocol;

namespace AmlMcp;

/// <summary>What a write produced: the document as it now is, its check, and where it went.</summary>
public sealed record WriteOutcome(AmlModel Model, CheckInfo Check, string? WrittenTo);

/// <summary>
/// Writes a described document, and proves it first. The order matters: the fence and the
/// target are settled before anything is rendered, so a caller who may not write learns
/// that at once; and the rendered document is written to a temporary file, read back with
/// the ordinary reader and run through the ordinary check before it replaces anything. What
/// the check calls a problem is never written.
/// </summary>
public static class DocumentWriter
{
    /// <summary>
    /// The problems that stop a write. Exactly the problem kinds of the reference check:
    /// the server refuses to write the document it would afterwards complain about. Notes
    /// do not stop anything and are reported with the answer.
    /// </summary>
    public static WriteOutcome Write(DocumentStore store, string path, DocumentSpec spec, bool preview, bool overwrite)
    {
        var options = store.Options;
        string? target = null;

        if (!preview)
        {
            if (options.WriteRoots.Count == 0)
                throw new McpException(
                    "This server may not write. It was started without --write <dir>; ask the user to add one, " +
                    "or call again with preview to see what would be written.");

            target = Path.GetFullPath(path);
            if (!options.AllowsWrite(target))
                throw new McpException(
                    $"'{target}' is outside the directories this server may write ({options.WriteRootsDescription}).");

            if (!target.EndsWith(".aml", StringComparison.OrdinalIgnoreCase))
                throw new McpException($"'{target}' is no .aml file. Stage one writes a single .aml document.");

            if (Directory.Exists(target))
                throw new McpException($"'{target}' is a directory. Name the file to write.");

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

        var fileName = Path.GetFileName(target ?? path);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "document.aml";
        var document = CaexWriter.Document(SpecCompletion.Complete(spec), fileName);

        // Beside the target, so the move afterwards stays on one volume; in the system
        // temporary folder for a preview, which may not be allowed to write anywhere else.
        var temporary = preview
            ? Path.Combine(Path.GetTempPath(), $"aml-mcp-{Guid.NewGuid():N}.aml")
            : target + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
            using (var writer = new StreamWriter(temporary, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                new XDocument(new XDeclaration("1.0", "utf-8", null), document).Save(writer);

            var model = AmlModel.Load(temporary, options);
            var check = Structured.Check(model);
            if (check.Problems.Count > 0)
                throw new McpException(
                    "The described document would not resolve, so nothing was written:" + Environment.NewLine +
                    string.Join(Environment.NewLine, check.Problems.Select(p => $"  {p.Kind}: {p.Message}")));

            if (preview) return new WriteOutcome(model, check, null);

            MoveIntoPlace(temporary, target!);
            // Read the file where it now lies, so the answer is about what is on disk.
            var written = AmlModel.Load(target!, options);
            return new WriteOutcome(written, Structured.Check(written), target);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
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
