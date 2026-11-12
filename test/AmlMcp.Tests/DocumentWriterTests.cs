using ModelContextProtocol;
using Xunit;

namespace AmlMcp.Tests;

/// <summary>
/// The write path. A document is proved before it replaces anything: rendered, written to a
/// temporary file, read back with the ordinary reader and checked with the ordinary check.
/// </summary>
public sealed class DocumentWriterTests : IDisposable
{
    private readonly Workspace _ws = new();

    public void Dispose() => _ws.Dispose();

    private DocumentStore Store(bool mayWrite = true, string? follow = null)
    {
        var args = new List<string> { "--root", _ws.Dir };
        if (mayWrite) { args.Add("--write"); args.Add(_ws.Dir); }
        if (follow is not null) { args.Add("--follow"); args.Add(follow); }
        return new DocumentStore(ServerOptions.Parse(args, _ => null));
    }

    private static DocumentSpec Sound(string elementName = "Station1") =>
        new(null, null, "A line with one station", null, null,
            new[]
            {
                new HierarchySpec("Plant", "What is there",
                    new[] { new ElementSpec(elementName, "Drill", null, null, null, null, null, null, null) }, null),
            });

    private string Target(string name = "written.aml") => Path.Combine(_ws.Dir, name);

    private IEnumerable<string> FilesInWorkspace() =>
        Directory.EnumerateFiles(_ws.Dir, "*", SearchOption.AllDirectories).Select(Path.GetFileName)!;

    [Fact]
    public void A_preview_writes_nothing_and_needs_no_write_directory()
    {
        var outcome = DocumentWriter.Write(Store(mayWrite: false), Target(), Sound(), preview: true, overwrite: false);

        Assert.Null(outcome.WrittenTo);
        Assert.False(File.Exists(Target()));
        Assert.True(outcome.Check.Ok);
        Assert.Equal("Plant", outcome.Model.Hierarchies.Single().Attribute("Name")!.Value);
    }

    [Fact]
    public void A_preview_reports_the_same_check_the_written_file_would_get()
    {
        var store = Store();
        var preview = DocumentWriter.Write(store, Target(), Sound(), preview: true, overwrite: false);
        var written = DocumentWriter.Write(store, Target(), Sound(), preview: false, overwrite: false);

        Assert.Equal(preview.Check.Ok, written.Check.Ok);
        Assert.Equal(preview.Check.Problems.Count, written.Check.Problems.Count);
        Assert.Equal(preview.Check.Ids, written.Check.Ids);
    }

    [Fact]
    public void A_write_outside_the_write_directory_is_refused_before_anything_is_rendered()
    {
        using var elsewhere = new Workspace();
        // The description is faulty as well: the answer has to be about the directory.
        var empty = new DocumentSpec(null, null, null, null, null, null);

        var ex = Assert.Throws<McpException>(() =>
            DocumentWriter.Write(Store(), Path.Combine(elsewhere.Dir, "x.aml"), empty, preview: false, overwrite: false));

        Assert.Contains("may write", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFiles(elsewhere.Dir));
    }

    [Fact]
    public void A_write_without_a_write_directory_at_all_is_refused()
    {
        var ex = Assert.Throws<McpException>(() =>
            DocumentWriter.Write(Store(mayWrite: false), Target(), Sound(), preview: false, overwrite: false));

        Assert.Contains("--write", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_write_lands_where_it_was_asked_to()
    {
        var outcome = DocumentWriter.Write(Store(), Target(), Sound(), preview: false, overwrite: false);

        Assert.Equal(Target(), outcome.WrittenTo);
        Assert.True(File.Exists(Target()));
        Assert.True(outcome.Check.Ok);
        Assert.Contains("written.aml", File.ReadAllText(Target()), StringComparison.Ordinal);
    }

    [Fact]
    public void A_blocking_problem_leaves_no_file_behind()
    {
        var pointingAtNothing = new DocumentSpec(null, null, null,
            new[] { new ExternalLibrarySpec("Vendor", "NoSuchLibrary.aml") }, null,
            new[]
            {
                new HierarchySpec("Plant", null,
                    new[] { new ElementSpec("A", "Vendor@VendorLib/Drill", null, null, null, null, null, null, null) }, null),
            });

        var ex = Assert.Throws<McpException>(() =>
            DocumentWriter.Write(Store(), Target(), pointingAtNothing, preview: false, overwrite: false));

        Assert.Contains("NoSuchLibrary.aml", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Target()));
        Assert.Empty(FilesInWorkspace());
    }

    [Fact]
    public void A_note_does_not_block()
    {
        var spec = new DocumentSpec(null, null, null, null, null,
            new[]
            {
                new HierarchySpec("Plant", null,
                    new[]
                    {
                        new ElementSpec("Pump", null, null, null, null, null,
                            new[] { new InterfaceSpec("Out", null) }, null, null),
                        new ElementSpec("Tank1", null, null, null, null, null,
                            new[] { new InterfaceSpec("In", null) }, null, null),
                        new ElementSpec("Tank2", null, null, null, null, null,
                            new[] { new InterfaceSpec("In", null) }, null, null),
                    },
                    new[]
                    {
                        new LinkSpec("One", "Plant/Pump/Out", "Plant/Tank1/In"),
                        new LinkSpec("Two", "Plant/Pump/Out", "Plant/Tank2/In"),
                    }),
            });

        var outcome = DocumentWriter.Write(Store(), Target(), spec, preview: false, overwrite: false);

        Assert.True(File.Exists(Target()));
        Assert.Contains(outcome.Check.Notes, n => n.Kind == "sharedInterface");
        Assert.Empty(outcome.Check.Problems);
    }

    [Fact]
    public void An_existing_file_is_not_overwritten_by_default()
    {
        File.WriteAllText(Target(), "older content");

        var ex = Assert.Throws<McpException>(() =>
            DocumentWriter.Write(Store(), Target(), Sound(), preview: false, overwrite: false));

        Assert.Contains("already", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("older content", File.ReadAllText(Target()));
    }

    [Fact]
    public void An_existing_file_is_replaced_when_the_caller_says_so()
    {
        File.WriteAllText(Target(), "older content");

        DocumentWriter.Write(Store(), Target(), Sound(), preview: false, overwrite: true);

        Assert.Contains("CAEXFile", File.ReadAllText(Target()), StringComparison.Ordinal);
    }

    [Fact]
    public void The_document_the_editor_has_open_is_refused_even_then()
    {
        var open = Path.Combine(_ws.Dir, "open.aml");
        File.WriteAllText(open, Fixtures.Caex215);
        var pointer = _ws.Write("open-document.txt", open);

        var ex = Assert.Throws<McpException>(() =>
            DocumentWriter.Write(Store(follow: pointer), open, Sound(), preview: false, overwrite: true));

        Assert.Contains("editor", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Fixtures.Caex215, File.ReadAllText(open));
    }

    [Fact]
    public void A_missing_directory_below_the_write_directory_is_created()
    {
        var target = Path.Combine(_ws.Dir, "models", "deeper", "written.aml");

        DocumentWriter.Write(Store(), target, Sound(), preview: false, overwrite: false);

        Assert.True(File.Exists(target));
    }

    [Fact]
    public void An_amlx_target_is_refused()
    {
        var ex = Assert.Throws<McpException>(() =>
            DocumentWriter.Write(Store(), Target("written.amlx"), Sound(), preview: false, overwrite: false));

        Assert.Contains(".aml", ex.Message, StringComparison.Ordinal);
        Assert.Empty(FilesInWorkspace());
    }

    [Fact]
    public void A_target_without_the_aml_extension_is_refused()
    {
        var ex = Assert.Throws<McpException>(() =>
            DocumentWriter.Write(Store(), Target("written.xml"), Sound(), preview: false, overwrite: false));

        Assert.Contains(".aml", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_directory_as_the_target_is_refused()
    {
        var directory = Path.Combine(_ws.Dir, "folder.aml");
        Directory.CreateDirectory(directory);

        var ex = Assert.Throws<McpException>(() =>
            DocumentWriter.Write(Store(), directory, Sound(), preview: false, overwrite: true));

        Assert.Contains("directory", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_faulty_description_is_refused_with_every_fault_at_once()
    {
        var spec = new DocumentSpec(null, null, null, null, null,
            new[]
            {
                new HierarchySpec("Plant", null,
                    new[]
                    {
                        new ElementSpec("A", null, null, null, null, null, null, null, "Plant/Nowhere"),
                        new ElementSpec("A", null, null, null, null, null, null, null, null),
                    },
                    null),
            });

        var ex = Assert.Throws<McpException>(() =>
            DocumentWriter.Write(Store(), Target(), spec, preview: false, overwrite: false));

        Assert.Contains("Plant/Nowhere", ex.Message, StringComparison.Ordinal);
        Assert.Contains("two children", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Umlauts_come_back_unchanged_from_the_written_file()
    {
        var spec = new DocumentSpec(null, null, null, null, null,
            new[]
            {
                new HierarchySpec("Anlage", null,
                    new[] { new ElementSpec("Prüfstation & Zubehör", null, null, null, null, null, null, null, null) }, null),
            });

        var outcome = DocumentWriter.Write(Store(), Target(), spec, preview: false, overwrite: false);
        var reopened = AmlModel.Load(outcome.WrittenTo!);

        Assert.Contains(reopened.ById.Values, e => AmlModel.NameOf(e) == "Prüfstation & Zubehör");
    }

    [Fact]
    public void No_temporary_file_is_left_behind_in_any_case()
    {
        var store = Store();
        DocumentWriter.Write(store, Target(), Sound(), preview: false, overwrite: false);
        Assert.Throws<McpException>(() =>
            DocumentWriter.Write(store, Target("second.amlx"), Sound(), preview: false, overwrite: false));
        DocumentWriter.Write(store, Target("third.aml"), Sound(), preview: true, overwrite: false);

        // One write, one refusal, one preview: only the write leaves a file.
        Assert.Equal(new[] { "written.aml" }, FilesInWorkspace().ToArray());
    }

    /// <summary>
    /// A write may create the directory it writes into. A write that is then refused has to
    /// take that directory with it: the caller fixes the description and tries again, and
    /// finds the file system as it was.
    /// </summary>
    [Fact]
    public void A_refused_write_takes_the_directory_it_created_with_it()
    {
        var directory = Path.Combine(_ws.Dir, "new", "folder");
        // An external library that is not there: the description is sound, the document is
        // rendered and written, and the check of the file refuses it.
        var spec = new DocumentSpec(null, null, null,
            new[] { new ExternalLibrarySpec("Demo", "nowhere.aml") }, null,
            new[] { new HierarchySpec("Plant", null, new[] { new ElementSpec("A", null, null, null, null, null, null, null, null) }, null) });

        Assert.Throws<McpException>(() =>
            DocumentWriter.Write(Store(), Path.Combine(directory, "written.aml"), spec, preview: false, overwrite: false));

        Assert.False(Directory.Exists(directory));
        Assert.False(Directory.Exists(Path.Combine(_ws.Dir, "new")));
    }

    /// <summary>A directory that already holds something is none of ours to remove.</summary>
    [Fact]
    public void A_refused_write_leaves_a_directory_that_was_already_there()
    {
        var directory = Path.Combine(_ws.Dir, "existing");
        Directory.CreateDirectory(directory);
        var spec = new DocumentSpec(null, null, null,
            new[] { new ExternalLibrarySpec("Demo", "nowhere.aml") }, null,
            new[] { new HierarchySpec("Plant", null, new[] { new ElementSpec("A", null, null, null, null, null, null, null, null) }, null) });

        Assert.Throws<McpException>(() =>
            DocumentWriter.Write(Store(), Path.Combine(directory, "written.aml"), spec, preview: false, overwrite: false));

        Assert.True(Directory.Exists(directory));
    }

    /// <summary>
    /// A directory named as the target is refused as a directory, whatever it is called.
    /// The extension is the lesser of the two reasons and must not hide the other.
    /// </summary>
    [Fact]
    public void A_directory_is_refused_as_a_directory_and_not_for_its_extension()
    {
        var directory = Path.Combine(_ws.Dir, "folder");
        Directory.CreateDirectory(directory);

        var ex = Assert.Throws<McpException>(() =>
            DocumentWriter.Write(Store(), directory, Sound(), preview: false, overwrite: false));

        Assert.Contains("is a directory", ex.Message, StringComparison.Ordinal);
    }
}
