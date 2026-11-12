using System.Xml.Linq;
using Xunit;

namespace AmlMcp.Tests;

/// <summary>
/// The written document against the real CAEX 3.0 schema, at the moment it is written.
/// CaexSchemaTests encodes the rules a reader can check by looking; this one hands the
/// document to the schema itself, so an input nobody thought of cannot leave the server
/// with a file that only looks like AutomationML.
/// </summary>
public sealed class SchemaValidationTests : IDisposable
{
    private readonly Workspace _ws = new();

    public void Dispose() => _ws.Dispose();

    private static readonly XNamespace C = AmlModel.C;

    private DocumentStore Store() =>
        new(ServerOptions.Parse(new[] { "--root", _ws.Dir, "--write", _ws.Dir }, _ => null));

    private static XElement Render(DocumentSpec spec) =>
        CaexWriter.Document(SpecCompletion.Complete(spec), "written.aml");

    private static ElementSpec Element(string name, InterfaceSpec[]? interfaces = null,
                                       AttributeSpec[]? attributes = null) =>
        new(name, null, null, null, null, attributes, interfaces, null, null);

    [Fact]
    public void A_rendered_document_satisfies_the_schema()
    {
        var spec = new DocumentSpec(null, null, "A plant",
            new[] { new ExternalLibrarySpec("Demo", "DemoLib.aml") },
            new[] { new LibrarySpec("Units", "SystemUnitClass", new[] { new ClassSpec("Drill", null, "A drill", null, null) }) },
            new[]
            {
                new HierarchySpec("Plant", "The plant",
                    new[]
                    {
                        new ElementSpec("Pump", "Units/Drill", new[] { "Roles/Station" }, new[] { "Roles/Station" },
                            "A pump", new[] { new AttributeSpec("Power", "7", "1", "kW", null, null) },
                            new[] { new InterfaceSpec("Out", "Ifs/Coupling") }, null, null),
                        Element("Tank", new[] { new InterfaceSpec("In", null) }),
                    },
                    new[] { new LinkSpec("Pipe", "Plant/Pump/Out", "Plant/Tank/In") }),
            });

        Assert.Empty(CaexSchema.Validate(Render(spec)));
    }

    /// <summary>The validator has to find a fault that is there, and say where it sits.</summary>
    [Fact]
    public void A_tree_the_schema_forbids_is_reported_with_the_element_that_broke_it()
    {
        var document = Render(new DocumentSpec(null, null, null, null, null,
            new[] { new HierarchySpec("Plant", null, new[] { Element("A") }, null) }));
        document.Element(C + "InstanceHierarchy")!.Add(
            new XElement(C + "InternalLink", new XAttribute("Name", "Loose"), new XAttribute("ID", "{00000000-0000-0000-0000-000000000001}"),
                new XAttribute("RefPartnerSideA", "a"), new XAttribute("RefPartnerSideB", "b")));

        var faults = CaexSchema.Validate(document);

        Assert.NotEmpty(faults);
        Assert.Contains(faults, f => f.Contains("InternalLink", StringComparison.Ordinal));
    }

    /// <summary>
    /// An attribute type that derives from another: the schema gives AttributeType a
    /// RefAttributeType and no RefBaseClassPath, so the base class cannot be written the
    /// way a system unit class writes it.
    /// </summary>
    [Fact]
    public void An_attribute_type_derives_through_RefAttributeType()
    {
        var spec = new DocumentSpec(null, null, null, null,
            new[]
            {
                new LibrarySpec("Types", "AttributeType", new[]
                {
                    new ClassSpec("Length", "Types/Measure", null, null, null),
                    new ClassSpec("Measure", null, null, null, null),
                }),
            },
            null);

        var rendered = Render(spec);
        var length = rendered.Descendants(C + "AttributeType")
            .First(e => (string?)e.Attribute("Name") == "Length");

        Assert.Equal("Types/Measure", (string?)length.Attribute("RefAttributeType"));
        Assert.Null(length.Attribute("RefBaseClassPath"));
        Assert.Empty(CaexSchema.Validate(rendered));
    }

    /// <summary>
    /// The whole way through the tool: what lands on disk is read back and handed to the
    /// schema. A green assertion here is the promise the server makes to its caller.
    /// </summary>
    [Fact]
    public void The_file_the_tool_writes_satisfies_the_schema()
    {
        var spec = new DocumentSpec(null, null, null, null,
            new[] { new LibrarySpec("Types", "AttributeType", new[] { new ClassSpec("Length", "Types/Measure", null, null, null), new ClassSpec("Measure", null, null, null, null) }) },
            new[] { new HierarchySpec("Plant", null, new[] { Element("A") }, null) });
        var target = Path.Combine(_ws.Dir, "written.aml");

        var result = AmlTools.WriteAmlDocument(Store(), target, spec);

        Assert.NotEqual(true, result.IsError);
        Assert.Empty(CaexSchema.Validate(XElement.Load(target)));
    }

    /// <summary>
    /// And the other way round: a description whose document the schema would reject is
    /// refused, and nothing is left behind. An attribute type carries no interfaces.
    /// </summary>
    [Fact]
    public void A_description_the_schema_rejects_is_refused_and_nothing_is_written()
    {
        var spec = new DocumentSpec(null, null, null, null,
            new[]
            {
                new LibrarySpec("Types", "AttributeType", new[]
                {
                    new ClassSpec("Length", null, null, null, new[] { new InterfaceSpec("Port", null) }),
                }),
            },
            null);
        var target = Path.Combine(_ws.Dir, "written.aml");

        var message = Answer.Error(AmlTools.WriteAmlDocument(Store(), target, spec));

        Assert.Contains("schema", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ExternalInterface", message, StringComparison.Ordinal);
        Assert.False(File.Exists(target));
    }

    /// <summary>A preview is held to the same schema, and still touches no disk.</summary>
    [Fact]
    public void A_preview_is_held_to_the_same_schema()
    {
        var spec = new DocumentSpec(null, null, null, null,
            new[]
            {
                new LibrarySpec("Types", "AttributeType", new[]
                {
                    new ClassSpec("Length", null, null, null, new[] { new InterfaceSpec("Port", null) }),
                }),
            },
            null);
        var target = Path.Combine(_ws.Dir, "written.aml");

        var message = Answer.Error(AmlTools.WriteAmlDocument(Store(), target, spec, preview: true));

        Assert.Contains("schema", message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(target));
    }
}
