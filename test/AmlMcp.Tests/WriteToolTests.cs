using ModelContextProtocol.Server;
using Xunit;

namespace AmlMcp.Tests;

/// <summary>
/// The tool an assistant calls. It answers what open_aml_document answers for a document
/// that exists, plus the reference check, whether it wrote the file or only looked at what
/// it would write.
/// </summary>
public sealed class WriteToolTests : IDisposable
{
    private readonly Workspace _ws = new();

    public void Dispose() => _ws.Dispose();

    private DocumentStore Store(bool mayWrite = true) =>
        new(ServerOptions.Parse(
            mayWrite ? new[] { "--root", _ws.Dir, "--write", _ws.Dir } : new[] { "--root", _ws.Dir },
            _ => null));

    private string Target(string name = "written.aml") => Path.Combine(_ws.Dir, name);

    private static DocumentSpec Sound() =>
        new(null, null, "A line with one station", null, null,
            new[]
            {
                new HierarchySpec("Plant", "What is there",
                    new[]
                    {
                        new ElementSpec("Station1", "Drill", new[] { "Station" }, null, null,
                            new[] { new AttributeSpec("Feed", "4.5", null, "mm/s", null, null) },
                            new[] { new InterfaceSpec("Port", "Coupling") }, null, null),
                    },
                    null),
            });

    [Fact]
    public void The_tool_is_annotated_as_a_writing_tool()
    {
        var tool = typeof(AmlTools).GetMethod(nameof(AmlTools.WriteAmlDocument))!
            .GetCustomAttributes(typeof(McpServerToolAttribute), false)
            .Cast<McpServerToolAttribute>().Single();

        Assert.Equal("write_aml_document", tool.Name);
        Assert.False(tool.ReadOnly);
        Assert.True(tool.Destructive);
        Assert.True(tool.Idempotent);
        Assert.False(tool.OpenWorld);
    }

    [Fact]
    public void The_answer_is_the_overview_and_the_check()
    {
        var text = Answer.Text(AmlTools.WriteAmlDocument(Store(), Target(), Sound()));

        Assert.Contains("Instance hierarchies (1):", text);
        Assert.Contains("Plant: 1 elements", text);
        Assert.Contains("Reference check for written.aml", text);
        Assert.Contains("no problems found", text);
    }

    [Fact]
    public void A_preview_says_that_nothing_was_written()
    {
        var result = AmlTools.WriteAmlDocument(Store(), Target(), Sound(), preview: true);

        Assert.Contains("nothing was written", Answer.Text(result), StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Target()));
        Assert.True(Answer.Data(result).GetProperty("preview").GetBoolean());
        // This server leaves a null out of the structured half; there is no file to name.
        Assert.False(Answer.Data(result).TryGetProperty("writtenTo", out _));
    }

    [Fact]
    public void A_write_names_the_file_it_wrote()
    {
        var result = AmlTools.WriteAmlDocument(Store(), Target(), Sound());

        Assert.Contains(Target(), Answer.Text(result), StringComparison.Ordinal);
        Assert.Equal(Target(), Answer.Data(result).GetProperty("writtenTo").GetString());
        Assert.False(Answer.Data(result).GetProperty("preview").GetBoolean());
    }

    [Fact]
    public void The_read_tools_answer_about_the_file_that_was_just_written()
    {
        var store = Store();
        AmlTools.WriteAmlDocument(store, Target(), Sound());

        Assert.Contains("written.aml", AmlTools.OpenDocumentText(store, Target()));
        Assert.Contains("class: AmlMcpUnitLib/Drill", AmlTools.ElementCard(store, "Plant/Station1"));
        Assert.Contains("no problems found", AmlTools.CheckText(store));
    }

    [Fact]
    public void A_refusal_comes_back_as_an_error_result_not_an_exception()
    {
        var message = Answer.Error(AmlTools.WriteAmlDocument(Store(mayWrite: false), Target(), Sound()));

        Assert.Contains("--write", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_description_with_nothing_in_it_is_refused()
    {
        var empty = new DocumentSpec(null, null, null, null, null, null);

        var message = Answer.Error(AmlTools.WriteAmlDocument(Store(), Target(), empty));

        Assert.Contains("nothing to write", message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Target()));
    }

    [Fact]
    public void A_description_with_only_libraries_is_written()
    {
        var libraryOnly = new DocumentSpec(null, null, "Classes to build with", null,
            new[]
            {
                new LibrarySpec("Lib", "SystemUnitClass", new[] { new ClassSpec("Drill", null, "A drill", null, null) }),
                new LibrarySpec("Roles", "role", new[] { new ClassSpec("Station", null, null, null, null) }),
            },
            null);

        var text = Answer.Text(AmlTools.WriteAmlDocument(Store(), Target("lib.aml"), libraryOnly));

        Assert.True(File.Exists(Target("lib.aml")));
        Assert.Contains("Lib: 1 classes", text);
        Assert.Contains("Roles: 1 classes", text);
    }

    /// <summary>
    /// The overview of a big document has to stay an overview: the read side caps the class
    /// list of a hierarchy and the library listing, and the write answer uses the same one.
    /// </summary>
    [Fact]
    public void A_large_document_still_answers_within_the_caps()
    {
        var elements = Enumerable.Range(0, 400)
            .Select(i => new ElementSpec($"E{i:000}", $"Class{i % 60:00}", null, null, null, null, null, null, null))
            .ToList();
        var spec = new DocumentSpec(null, null, null, null, null,
            new[] { new HierarchySpec("Plant", null, elements, null) });

        var text = Answer.Text(AmlTools.WriteAmlDocument(Store(), Target("big.aml"), spec));

        Assert.Contains("Plant: 400 elements", text);
        Assert.Contains("more class(es), ask list_classes", text);
        Assert.InRange(text.Split('\n').Length, 10, 120);
    }

    [Fact]
    public void The_instructions_name_the_tool_only_when_writing_is_on()
    {
        var reading = ServerInstructions.For(ServerOptions.Parse(new[] { "--root", _ws.Dir }, _ => null));
        var writing = ServerInstructions.For(ServerOptions.Parse(new[] { "--write", _ws.Dir }, _ => null));

        Assert.DoesNotContain("write_aml_document", reading);
        Assert.Contains("write_aml_document", writing);
        Assert.Contains(_ws.Dir, writing);
    }
}
