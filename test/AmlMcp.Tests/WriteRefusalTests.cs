using System.Xml.Linq;
using ModelContextProtocol;
using Xunit;

namespace AmlMcp.Tests;

/// <summary>
/// What a faulty description gets back. Every fault has to come out as a refusal that names
/// where it sits, not as an unhandled exception and not as a riddle about a GUID: a model
/// can only fix what the answer names.
/// </summary>
public sealed class WriteRefusalTests : IDisposable
{
    private readonly Workspace _ws = new();

    public void Dispose() => _ws.Dispose();

    private DocumentStore Store() =>
        new(ServerOptions.Parse(new[] { "--root", _ws.Dir, "--write", _ws.Dir }, _ => null));

    private string Write(DocumentSpec spec) =>
        Answer.Error(AmlTools.WriteAmlDocument(Store(), Path.Combine(_ws.Dir, "written.aml"), spec));

    private static DocumentSpec Of(params HierarchySpec[] hierarchies) =>
        new(null, null, null, null, null, hierarchies);

    private static ElementSpec Element(string name, InterfaceSpec[]? interfaces = null,
                                       AttributeSpec[]? attributes = null, string? mirrorOf = null) =>
        new(name, null, null, null, null, attributes, interfaces, null, mirrorOf);

    /// <summary>A library without a classes field: System.Text.Json leaves the list null.</summary>
    [Fact]
    public void A_library_without_any_classes_is_refused_and_does_not_crash()
    {
        var spec = new DocumentSpec(null, null, null, null,
            new[] { new LibrarySpec("Lib", "SystemUnitClass", null!) },
            new[] { new HierarchySpec("Plant", null, new[] { new ElementSpec("A", "Lib/Drill", null, null, null, null, null, null, null) }, null) });

        var message = Write(spec);

        Assert.Contains("Lib", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Object reference", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_interface_without_a_name_is_refused()
    {
        var message = Write(Of(new HierarchySpec("Plant", null,
            new[] { Element("A", interfaces: new[] { new InterfaceSpec(null!, "Coupling") }) }, null)));

        Assert.Contains("Plant/A", message, StringComparison.Ordinal);
        Assert.Contains("without a name", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_attribute_without_a_name_is_refused()
    {
        var message = Write(Of(new HierarchySpec("Plant", null,
            new[] { Element("A", attributes: new[] { new AttributeSpec("  ", "7", null, null, null, null) }) }, null)));

        Assert.Contains("Plant/A", message, StringComparison.Ordinal);
        Assert.Contains("without a name", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_link_without_a_name_is_refused()
    {
        var message = Write(Of(new HierarchySpec("Plant", null,
            new[]
            {
                Element("Pump", interfaces: new[] { new InterfaceSpec("Out", null) }),
                Element("Tank", interfaces: new[] { new InterfaceSpec("In", null) }),
            },
            new[] { new LinkSpec(null!, "Plant/Pump/Out", "Plant/Tank/In") })));

        Assert.Contains("without a name", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_external_library_without_an_alias_or_a_path_is_refused()
    {
        var spec = new DocumentSpec(null, null, null,
            new[] { new ExternalLibrarySpec("", "DemoLib.aml") }, null,
            new[] { new HierarchySpec("Plant", null, new[] { Element("A") }, null) });

        Assert.Contains("alias", Write(spec), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Only CAEX 3.0 is written, whatever the description asks for. A document labelled 2.15
    /// with a 3.0 body would be a lie about itself.
    /// </summary>
    [Fact]
    public void A_schema_version_the_server_does_not_write_is_refused()
    {
        var spec = new DocumentSpec(null, "2.15", null, null, null,
            new[] { new HierarchySpec("Plant", null, new[] { Element("A") }, null) });

        var message = Write(spec);

        Assert.Contains("2.15", message, StringComparison.Ordinal);
        Assert.Contains("3.0", message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_schema_version_may_be_left_out_or_given_as_three_point_zero()
    {
        var store = Store();
        var hierarchies = new[] { new HierarchySpec("Plant", null, new[] { Element("A") }, null) };

        foreach (var version in new string?[] { null, "3.0" })
        {
            var spec = new DocumentSpec(null, version, null, null, null, hierarchies);
            var result = AmlTools.WriteAmlDocument(store, Path.Combine(_ws.Dir, $"v{version ?? "none"}.aml"), spec);

            Assert.NotEqual(true, result.IsError);
        }
    }

    /// <summary>
    /// An endpoint that differs only in letter case is accepted, and the link then has to
    /// point at the interface that is there, not at a hash of the spelling it was given.
    /// </summary>
    [Fact]
    public void An_endpoint_in_another_letter_case_still_finds_its_interface()
    {
        var spec = Of(new HierarchySpec("Plant", null,
            new[]
            {
                Element("Pump", interfaces: new[] { new InterfaceSpec("Out", null) }),
                Element("Tank", interfaces: new[] { new InterfaceSpec("In", null) }),
            },
            new[] { new LinkSpec("Pipe", "plant/pump/out", "Plant/Tank/In") }));

        var result = AmlTools.WriteAmlDocument(Store(), Path.Combine(_ws.Dir, "written.aml"), spec);

        Assert.NotEqual(true, result.IsError);
        Assert.Contains("no problems found", Answer.Text(result), StringComparison.Ordinal);
    }

    /// <summary>The same for a mirror, which carries the master's ID.</summary>
    [Fact]
    public void A_mirror_in_another_letter_case_still_finds_its_master()
    {
        var spec = Of(new HierarchySpec("Plant", null,
            new[] { Element("Real"), Element("Copy", mirrorOf: "plant/real") }, null));

        var result = AmlTools.WriteAmlDocument(Store(), Path.Combine(_ws.Dir, "written.aml"), spec);

        Assert.NotEqual(true, result.IsError);
        Assert.Contains("no problems found", Answer.Text(result), StringComparison.Ordinal);
    }

    /// <summary>
    /// A reference points at an element by path. A path that names nothing has to be refused
    /// by name, not reported later as a dangling reference to a GUID nobody wrote.
    /// </summary>
    [Fact]
    public void A_reference_to_an_element_that_is_not_there_is_refused_by_its_path()
    {
        var spec = Of(new HierarchySpec("Plant", null,
            new[] { Element("Note", attributes: new[] { new AttributeSpec("refObj", null, null, null, "Plant/Nowhere", null) }) },
            null));

        var message = Write(spec);

        Assert.Contains("Plant/Nowhere", message, StringComparison.Ordinal);
        Assert.DoesNotContain("danglingReference", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reference_in_another_letter_case_still_finds_its_target()
    {
        var spec = Of(new HierarchySpec("Plant", null,
            new[]
            {
                Element("Target"),
                Element("Note", attributes: new[] { new AttributeSpec("refObj", null, null, null, "plant/target", null) }),
            },
            null));

        var result = AmlTools.WriteAmlDocument(Store(), Path.Combine(_ws.Dir, "written.aml"), spec);

        Assert.NotEqual(true, result.IsError);
        Assert.Contains("1 resolved", Answer.Text(result), StringComparison.Ordinal);
    }

    /// <summary>
    /// The classes of a library get the same completion as the elements: a base class or an
    /// interface class written without a library would not resolve either.
    /// </summary>
    [Fact]
    public void A_base_class_of_a_library_class_is_completed_too()
    {
        var spec = new DocumentSpec(null, null, null, null,
            new[]
            {
                new LibrarySpec("Lib", "SystemUnitClass", new[]
                {
                    new ClassSpec("Motor", "Drive", null, null, new[] { new InterfaceSpec("Power", "Coupling") }),
                }),
            },
            null);

        var result = AmlTools.WriteAmlDocument(Store(), Path.Combine(_ws.Dir, "written.aml"), spec);

        Assert.NotEqual(true, result.IsError);
        Assert.Contains("no problems found", Answer.Text(result), StringComparison.Ordinal);
    }

    /// <summary>
    /// A description that brings the standard object reference library keeps it: the server
    /// must not quietly put its own attribute type beside the standard one.
    /// </summary>
    [Fact]
    public void The_standard_object_reference_library_wins_over_the_generated_one()
    {
        var library = new LibrarySpec("AutomationML_ObjectReferences_AttributeTypeLib", "AttributeType",
            new[] { new ClassSpec("refObj", null, null, null, null) });
        var spec = new DocumentSpec(null, null, null, null, new[] { library },
            new[]
            {
                new HierarchySpec("Plant", null,
                    new[]
                    {
                        Element("Target"),
                        Element("Note", attributes: new[]
                        {
                            new AttributeSpec("refObj", null, null, null, "Plant/Target", null),
                        }),
                    },
                    null),
            });

        var result = AmlTools.WriteAmlDocument(Store(), Path.Combine(_ws.Dir, "written.aml"), spec);
        var written = XElement.Load(Path.Combine(_ws.Dir, "written.aml"));

        Assert.NotEqual(true, result.IsError);
        Assert.DoesNotContain("AmlMcpReferenceLib", written.ToString(), StringComparison.Ordinal);
        Assert.Contains("AutomationML_ObjectReferences_AttributeTypeLib/refObj",
            written.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Review Focus 2: the file itself has to be UTF-8 without a byte order mark.</summary>
    [Fact]
    public void The_written_file_is_utf8_without_a_byte_order_mark()
    {
        var spec = Of(new HierarchySpec("Anlage", null, new[] { Element("Prüfstation") }, null));
        var target = Path.Combine(_ws.Dir, "written.aml");

        AmlTools.WriteAmlDocument(Store(), target, spec);
        var bytes = File.ReadAllBytes(target);

        Assert.NotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.Contains("encoding=\"utf-8\"", File.ReadAllText(target), StringComparison.Ordinal);
        Assert.Contains("Prüfstation", File.ReadAllText(target), StringComparison.Ordinal);
    }
}
