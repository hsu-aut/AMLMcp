using System.Xml.Linq;
using Xunit;

namespace AmlMcp.Tests;

/// <summary>
/// What a described document looks like once it is CAEX. The renderer owns the form every
/// reference is written in, so these tests are about the XML, not about the description.
/// </summary>
public sealed class CaexWriterTests
{
    private static readonly XNamespace C = AmlModel.C;

    private static ElementSpec Plain(string name, string? @class = null, string[]? roles = null,
                                     string[]? supportedRoles = null, InterfaceSpec[]? interfaces = null,
                                     AttributeSpec[]? attributes = null, ElementSpec[]? children = null,
                                     string? mirrorOf = null) =>
        new(name, @class, roles, supportedRoles, null, attributes, interfaces, children, mirrorOf);

    private static XElement Render(DocumentSpec spec, string fileName = "written.aml") =>
        CaexWriter.Document(SpecCompletion.Complete(spec), fileName);

    private static XElement Render(params HierarchySpec[] hierarchies) =>
        Render(new DocumentSpec(null, null, null, null, null, hierarchies));

    private static XElement Hierarchy(XElement document, string name) =>
        document.Elements(C + "InstanceHierarchy").Single(h => (string?)h.Attribute("Name") == name);

    private static XElement Element(XElement parent, string name) =>
        parent.Elements(C + "InternalElement").Single(e => (string?)e.Attribute("Name") == name);

    [Fact]
    public void The_document_carries_the_caex_namespace_and_the_schema_version()
    {
        var document = Render(new HierarchySpec("Plant", null, new[] { Plain("A") }, null));

        Assert.Equal(C + "CAEXFile", document.Name);
        Assert.Equal("3.0", (string?)document.Attribute("SchemaVersion"));
        Assert.Equal("written.aml", (string?)document.Attribute("FileName"));
        Assert.Equal("AutomationML 2.10", (string?)document.Element(C + "SuperiorStandardVersion"));
    }

    [Fact]
    public void The_document_says_which_writer_made_it()
    {
        var document = Render(new HierarchySpec("Plant", null, new[] { Plain("A") }, null));
        var source = document.Element(C + "SourceDocumentInformation");

        Assert.NotNull(source);
        Assert.Equal("aml-mcp", (string?)source!.Attribute("WriterName"));
        Assert.False(string.IsNullOrWhiteSpace((string?)source.Attribute("WriterVersion")));
        Assert.False(string.IsNullOrWhiteSpace((string?)source.Attribute("LastWritingDateTime")));
    }

    [Fact]
    public void A_hierarchy_renders_its_elements_with_derived_ids()
    {
        var document = Render(new HierarchySpec("Plant", "What is there", new[] { Plain("Station1") }, null));
        var hierarchy = Hierarchy(document, "Plant");

        Assert.Equal(SpecIds.IdFor(SpecIds.KeyOf("Plant", "hierarchy")), (string?)hierarchy.Attribute("ID"));
        Assert.Equal("What is there", (string?)hierarchy.Element(C + "Description"));
        Assert.Equal(SpecIds.IdFor(SpecIds.KeyOf("Plant/Station1", "element")),
            (string?)Element(hierarchy, "Station1").Attribute("ID"));
    }

    [Fact]
    public void A_child_renders_below_its_parent()
    {
        var document = Render(new HierarchySpec("Plant", null,
            new[] { Plain("Line", children: new[] { Plain("Station1") }) }, null));

        var child = Element(Element(Hierarchy(document, "Plant"), "Line"), "Station1");

        Assert.Equal(SpecIds.IdFor(SpecIds.KeyOf("Plant/Line/Station1", "element")), (string?)child.Attribute("ID"));
    }

    [Fact]
    public void An_attribute_renders_value_unit_and_sub_attributes()
    {
        var width = new AttributeSpec("width", "120", null, null, null, null);
        var bounds = new AttributeSpec("Feed", "4.5", "3.0", "mm/s", null, new[] { width });
        var document = Render(new HierarchySpec("Plant", null, new[] { Plain("A", attributes: new[] { bounds }) }, null));

        var attribute = Element(Hierarchy(document, "Plant"), "A").Elements(C + "Attribute").Single();

        Assert.Equal("Feed", (string?)attribute.Attribute("Name"));
        Assert.Equal("mm/s", (string?)attribute.Attribute("Unit"));
        Assert.Equal("4.5", (string?)attribute.Element(C + "Value"));
        Assert.Equal("3.0", (string?)attribute.Element(C + "DefaultValue"));
        Assert.Equal("120", (string?)attribute.Elements(C + "Attribute").Single().Element(C + "Value"));
    }

    [Fact]
    public void An_interface_renders_with_its_class()
    {
        var document = Render(new HierarchySpec("Plant", null,
            new[] { Plain("A", interfaces: new[] { new InterfaceSpec("Port", "Coupling") }) }, null));

        var @interface = Element(Hierarchy(document, "Plant"), "A").Elements(C + "ExternalInterface").Single();

        Assert.Equal("Port", (string?)@interface.Attribute("Name"));
        Assert.Equal("AmlMcpInterfaceLib/Coupling", (string?)@interface.Attribute("RefBaseClassPath"));
        Assert.Equal(SpecIds.IdFor(SpecIds.KeyOf("Plant/A/Port", "interface")), (string?)@interface.Attribute("ID"));
    }

    [Fact]
    public void Roles_render_as_role_requirements_and_supported_roles_apart()
    {
        var document = Render(new HierarchySpec("Plant", null,
            new[] { Plain("A", roles: new[] { "Station" }, supportedRoles: new[] { "Resource" }) }, null));
        var element = Element(Hierarchy(document, "Plant"), "A");

        Assert.Equal("AmlMcpRoleLib/Station",
            (string?)element.Element(C + "RoleRequirements")!.Attribute("RefBaseRoleClassPath"));
        Assert.Equal("AmlMcpRoleLib/Resource",
            (string?)element.Element(C + "SupportedRoleClass")!.Attribute("RefRoleClassPath"));
    }

    [Fact]
    public void A_link_renders_the_interface_ids_of_both_sides()
    {
        var document = Render(new HierarchySpec("Plant", null,
            new[]
            {
                Plain("Pump", interfaces: new[] { new InterfaceSpec("Out", null) }),
                Plain("Tank", interfaces: new[] { new InterfaceSpec("In", null) }),
            },
            new[] { new LinkSpec("Pipe", "Plant/Pump/Out", "Plant/Tank/In") }));

        var link = Hierarchy(document, "Plant").Elements(C + "InternalLink").Single();

        Assert.Equal("Pipe", (string?)link.Attribute("Name"));
        Assert.Equal(SpecIds.IdFor(SpecIds.KeyOf("Plant/Pump/Out", "interface")), (string?)link.Attribute("RefPartnerSideA"));
        Assert.Equal(SpecIds.IdFor(SpecIds.KeyOf("Plant/Tank/In", "interface")), (string?)link.Attribute("RefPartnerSideB"));
    }

    [Fact]
    public void A_link_between_two_hierarchies_renders_the_same_way()
    {
        var document = Render(
            new HierarchySpec("Plant", null, new[] { Plain("Pump", interfaces: new[] { new InterfaceSpec("Out", null) }) },
                new[] { new LinkSpec("Across", "Plant/Pump/Out", "Process/Step/In") }),
            new HierarchySpec("Process", null, new[] { Plain("Step", interfaces: new[] { new InterfaceSpec("In", null) }) }, null));

        var link = Hierarchy(document, "Plant").Elements(C + "InternalLink").Single();

        Assert.Equal(SpecIds.IdFor(SpecIds.KeyOf("Process/Step/In", "interface")), (string?)link.Attribute("RefPartnerSideB"));
    }

    [Fact]
    public void A_mirror_renders_the_master_id_in_RefBaseSystemUnitPath()
    {
        var document = Render(new HierarchySpec("Plant", null,
            new[] { Plain("Real"), Plain("Copy", mirrorOf: "Plant/Real") }, null));

        var copy = Element(Hierarchy(document, "Plant"), "Copy");

        Assert.Equal(SpecIds.IdFor(SpecIds.KeyOf("Plant/Real", "element")),
            (string?)copy.Attribute("RefBaseSystemUnitPath"));
    }

    [Fact]
    public void A_reference_attribute_renders_as_an_object_reference()
    {
        var pointing = new AttributeSpec("refObj", null, null, null, "Plant/Target", null);
        var document = Render(new HierarchySpec("Plant", null,
            new[] { Plain("Target"), Plain("Note", attributes: new[] { pointing }) }, null));

        var attribute = Element(Hierarchy(document, "Plant"), "Note").Elements(C + "Attribute").Single();

        Assert.EndsWith("/refObj", (string?)attribute.Attribute("RefAttributeType"), StringComparison.Ordinal);
        Assert.Equal(SpecIds.IdFor(SpecIds.KeyOf("Plant/Target", "element")), (string?)attribute.Element(C + "Value"));
    }

    [Fact]
    public void A_named_external_library_renders_an_ExternalReference()
    {
        var spec = new DocumentSpec(null, null, null,
            new[] { new ExternalLibrarySpec("Demo", "DemoLib.aml") }, null,
            new[] { new HierarchySpec("Plant", null, new[] { Plain("A", "Demo@DemoUnitLib/Drill") }, null) });

        var document = Render(spec);
        var reference = document.Elements(C + "ExternalReference").Single();

        Assert.Equal("Demo", (string?)reference.Attribute("Alias"));
        Assert.Equal("DemoLib.aml", (string?)reference.Attribute("Path"));
        Assert.Equal("Demo@DemoUnitLib/Drill",
            (string?)Element(Hierarchy(document, "Plant"), "A").Attribute("RefBaseSystemUnitPath"));
    }

    [Fact]
    public void Classes_of_the_document_render_as_its_own_libraries()
    {
        var spec = new DocumentSpec(null, null, null, null,
            new[]
            {
                new LibrarySpec("Lib", "SystemUnitClass",
                    new[] { new ClassSpec("Drill", null, "A drill", new[] { new AttributeSpec("Speed", null, "3000", "1/min", null, null) }, null) }),
                new LibrarySpec("Roles", "RoleClass", new[] { new ClassSpec("Station", null, null, null, null) }),
            },
            null);

        var document = Render(spec);
        var unitLib = document.Elements(C + "SystemUnitClassLib").Single();
        var cls = unitLib.Elements(C + "SystemUnitClass").Single();

        Assert.Equal("Lib", (string?)unitLib.Attribute("Name"));
        Assert.Equal("Drill", (string?)cls.Attribute("Name"));
        Assert.Equal("A drill", (string?)cls.Element(C + "Description"));
        Assert.Equal("3000", (string?)cls.Elements(C + "Attribute").Single().Element(C + "DefaultValue"));
        Assert.Equal("Station", (string?)document.Elements(C + "RoleClassLib").Single()
            .Elements(C + "RoleClass").Single().Attribute("Name"));
    }

    [Fact]
    public void A_base_class_renders_as_a_class_path()
    {
        var spec = new DocumentSpec(null, null, null, null,
            new[]
            {
                new LibrarySpec("Lib", "SystemUnitClass", new[]
                {
                    new ClassSpec("Drive", null, null, null, null),
                    new ClassSpec("Motor", "Lib/Drive", null, null, null),
                }),
            },
            null);

        var classes = Render(spec).Elements(C + "SystemUnitClassLib").Single().Elements(C + "SystemUnitClass").ToList();

        Assert.Equal("Lib/Drive", (string?)classes.Single(c => (string?)c.Attribute("Name") == "Motor").Attribute("RefBaseClassPath"));
    }

    /// <summary>The tree is built, never concatenated, so escaping is the framework's job.</summary>
    [Fact]
    public void A_name_with_xml_characters_and_umlauts_survives()
    {
        var document = Render(new HierarchySpec("Anlage & Zubehör", null,
            new[] { Plain("Ventil <2>", attributes: new[] { new AttributeSpec("Hinweis", "a < b & \"c\"", null, null, null, null) }) },
            null));

        var hierarchy = Hierarchy(document, "Anlage & Zubehör");
        var element = Element(hierarchy, "Ventil <2>");

        Assert.Equal("a < b & \"c\"", (string?)element.Elements(C + "Attribute").Single().Element(C + "Value"));
        Assert.Contains("&amp;", document.ToString(SaveOptions.DisableFormatting), StringComparison.Ordinal);
    }

    /// <summary>Stage 2 renders parts of a document, so one element has to render alone.</summary>
    [Fact]
    public void One_element_can_be_rendered_on_its_own()
    {
        var element = CaexWriter.Element(Plain("Station1", interfaces: new[] { new InterfaceSpec("Port", null) }), "Plant");

        Assert.Equal(C + "InternalElement", element.Name);
        Assert.Equal("Station1", (string?)element.Attribute("Name"));
        Assert.Equal(SpecIds.IdFor(SpecIds.KeyOf("Plant/Station1", "element")), (string?)element.Attribute("ID"));
        Assert.Single(element.Elements(C + "ExternalInterface"));
    }

    /// <summary>A library renders alone for the same reason.</summary>
    [Fact]
    public void One_library_can_be_rendered_on_its_own()
    {
        var library = CaexWriter.Library(new LibrarySpec("Roles", "role", new[] { new ClassSpec("Station", null, null, null, null) }));

        Assert.Equal(C + "RoleClassLib", library.Name);
        Assert.Equal("Station", (string?)library.Elements(C + "RoleClass").Single().Attribute("Name"));
    }
}
