using System.Xml.Linq;
using Xunit;

namespace AmlMcp.Tests;

/// <summary>
/// The order and the attributes CAEX 3.0 prescribes. A document the server calls checked has
/// to open in a tool that validates against the schema, or the server has written something
/// that looks like AutomationML and is not.
///
/// <para>
/// These are the rules of `CAEX_ClassModel_V.3.0.xsd`, encoded here because the schema itself
/// is published by AutomationML e.V. and is not ours to put in this repository. The full
/// validation is a manual check; see docs/superpowers/plans for the command.
/// </para>
/// </summary>
public sealed class CaexSchemaTests
{
    private static readonly XNamespace C = AmlModel.C;

    private static XElement Render(DocumentSpec spec) =>
        CaexWriter.Document(SpecCompletion.Complete(spec), "written.aml");

    private static ElementSpec Element(string name, string? @class = null, string[]? roles = null,
                                       string[]? supportedRoles = null, InterfaceSpec[]? interfaces = null,
                                       AttributeSpec[]? attributes = null, ElementSpec[]? children = null) =>
        new(name, @class, roles, supportedRoles, null, attributes, interfaces, children, null);

    private static IEnumerable<string> ChildNames(XElement parent) =>
        parent.Elements().Select(e => e.Name.LocalName);

    /// <summary>
    /// CAEXFile: the Header group of CAEXBasicObject first, then SuperiorStandardVersion,
    /// SourceDocumentInformation, ExternalReference, InstanceHierarchy, and the libraries in
    /// the order InterfaceClassLib, RoleClassLib, SystemUnitClassLib, AttributeTypeLib.
    /// </summary>
    [Fact]
    public void The_document_follows_the_order_the_schema_prescribes()
    {
        var spec = new DocumentSpec(null, null, "What this document is",
            new[] { new ExternalLibrarySpec("Demo", "DemoLib.aml") },
            new[]
            {
                new LibrarySpec("Types", "AttributeType", new[] { new ClassSpec("Length", null, null, null, null) }),
                new LibrarySpec("Units", "SystemUnitClass", new[] { new ClassSpec("Drill", null, null, null, null) }),
                new LibrarySpec("Ifs", "InterfaceClass", new[] { new ClassSpec("Coupling", null, null, null, null) }),
                new LibrarySpec("Roles", "RoleClass", new[] { new ClassSpec("Station", null, null, null, null) }),
            },
            new[] { new HierarchySpec("Plant", null, new[] { Element("A") }, null) });

        Assert.Equal(
            new[]
            {
                "Description", "SuperiorStandardVersion", "SourceDocumentInformation", "ExternalReference",
                "InstanceHierarchy", "InterfaceClassLib", "RoleClassLib", "SystemUnitClassLib", "AttributeTypeLib",
            },
            ChildNames(Render(spec)));
    }

    /// <summary>
    /// SourceDocumentInformationType has no Writer attributes. It requires OriginName,
    /// OriginID, OriginVersion and LastWritingDateTime, and allows nothing but the Origin
    /// family besides them.
    /// </summary>
    [Fact]
    public void The_writer_information_carries_only_what_the_schema_allows()
    {
        var allowed = new[]
        {
            "OriginName", "OriginID", "OriginVendor", "OriginVendorURL", "OriginVersion",
            "OriginRelease", "LastWritingDateTime", "OriginProjectTitle", "OriginProjectID",
        };
        var source = Render(new DocumentSpec(null, null, null, null, null,
            new[] { new HierarchySpec("Plant", null, new[] { Element("A") }, null) }))
            .Element(C + "SourceDocumentInformation")!;

        var names = source.Attributes().Select(a => a.Name.LocalName).ToList();

        Assert.All(names, name => Assert.Contains(name, allowed));
        Assert.Contains("OriginName", names);
        Assert.Contains("OriginID", names);
        Assert.Contains("OriginVersion", names);
        Assert.Contains("LastWritingDateTime", names);
        // The timestamp has to be an xs:dateTime, which round-trips.
        Assert.True(DateTime.TryParse((string?)source.Attribute("LastWritingDateTime"), out _));
    }

    /// <summary>AttributeType: Description, then DefaultValue, then Value, then Attribute.</summary>
    [Fact]
    public void An_attribute_puts_the_default_value_before_the_value()
    {
        var attribute = new AttributeSpec("Feed", "4.5", "3.0", "mm/s", null,
            new[] { new AttributeSpec("width", "120", null, null, null, null) });
        var document = Render(new DocumentSpec(null, null, null, null, null,
            new[] { new HierarchySpec("Plant", null, new[] { Element("A", attributes: new[] { attribute }) }, null) }));

        var rendered = document.Descendants(C + "Attribute").First(a => (string?)a.Attribute("Name") == "Feed");

        Assert.Equal(new[] { "DefaultValue", "Value", "Attribute" }, ChildNames(rendered));
    }

    /// <summary>
    /// InternalElementType extends SystemUnitClassType, so everything of the base type comes
    /// first: Attribute, ExternalInterface, InternalElement, SupportedRoleClass, InternalLink,
    /// and only then the RoleRequirements of the extension.
    /// </summary>
    [Fact]
    public void An_element_puts_the_parts_of_its_base_type_before_its_role_requirements()
    {
        var spec = new DocumentSpec(null, null, null, null, null,
            new[]
            {
                new HierarchySpec("Plant", null,
                    new[]
                    {
                        Element("Line",
                            roles: new[] { "Station" },
                            supportedRoles: new[] { "Resource" },
                            interfaces: new[] { new InterfaceSpec("Port", null) },
                            attributes: new[] { new AttributeSpec("Feed", "1", null, null, null, null) },
                            children: new[] { Element("Station1", interfaces: new[] { new InterfaceSpec("In", null) }) }),
                    },
                    new[] { new LinkSpec("Inside", "Plant/Line/Port", "Plant/Line/Station1/In") }),
            });

        var line = Render(spec).Descendants(C + "InternalElement").First(e => (string?)e.Attribute("Name") == "Line");

        Assert.Equal(
            new[] { "Attribute", "ExternalInterface", "InternalElement", "SupportedRoleClass", "InternalLink", "RoleRequirements" },
            ChildNames(line));
    }

    /// <summary>
    /// An InstanceHierarchy may contain InternalElement and nothing else. An InternalLink is
    /// only allowed inside a SystemUnitClassType, which an InternalElement is.
    /// </summary>
    [Fact]
    public void A_link_is_never_a_child_of_an_instance_hierarchy()
    {
        var spec = new DocumentSpec(null, null, null, null, null,
            new[]
            {
                new HierarchySpec("Plant", null,
                    new[]
                    {
                        Element("Pump", interfaces: new[] { new InterfaceSpec("Out", null) }),
                        Element("Tank", interfaces: new[] { new InterfaceSpec("In", null) }),
                    },
                    new[] { new LinkSpec("Pipe", "Plant/Pump/Out", "Plant/Tank/In") }),
            });

        var document = Render(spec);

        Assert.Empty(document.Element(C + "InstanceHierarchy")!.Elements(C + "InternalLink"));
        Assert.Single(document.Descendants(C + "InternalLink"));
    }

    /// <summary>
    /// Where a link goes: the deepest element that contains both of its sides. That is where
    /// the standard puts it and where a reader looks for it.
    /// </summary>
    [Fact]
    public void A_link_sits_in_the_deepest_element_that_contains_both_sides()
    {
        var spec = new DocumentSpec(null, null, null, null, null,
            new[]
            {
                new HierarchySpec("Plant", null,
                    new[]
                    {
                        Element("Line", children: new[]
                        {
                            Element("Pump", interfaces: new[] { new InterfaceSpec("Out", null) }),
                            Element("Tank", interfaces: new[] { new InterfaceSpec("In", null) }),
                        }),
                    },
                    new[] { new LinkSpec("Pipe", "Plant/Line/Pump/Out", "Plant/Line/Tank/In") }),
            });

        var link = Render(spec).Descendants(C + "InternalLink").Single();

        Assert.Equal("Line", (string?)link.Parent!.Attribute("Name"));
    }

    /// <summary>
    /// Two elements directly below the hierarchy have no common element. The link then goes
    /// into the element it starts from, which is what the editor does when a user draws one.
    /// </summary>
    [Fact]
    public void A_link_between_two_top_level_elements_sits_in_the_element_it_starts_from()
    {
        var spec = new DocumentSpec(null, null, null, null, null,
            new[]
            {
                new HierarchySpec("Plant", null,
                    new[]
                    {
                        Element("Pump", interfaces: new[] { new InterfaceSpec("Out", null) }),
                        Element("Tank", interfaces: new[] { new InterfaceSpec("In", null) }),
                    },
                    new[] { new LinkSpec("Pipe", "Plant/Pump/Out", "Plant/Tank/In") }),
            });

        var link = Render(spec).Descendants(C + "InternalLink").Single();

        Assert.Equal("Pump", (string?)link.Parent!.Attribute("Name"));
    }

    /// <summary>A link across hierarchies has no common element either, and the same rule holds.</summary>
    [Fact]
    public void A_link_across_hierarchies_sits_in_the_element_it_starts_from()
    {
        var spec = new DocumentSpec(null, null, null, null, null,
            new[]
            {
                new HierarchySpec("Plant", null,
                    new[] { Element("Pump", interfaces: new[] { new InterfaceSpec("Out", null) }) },
                    new[] { new LinkSpec("Across", "Plant/Pump/Out", "Process/Step/In") }),
                new HierarchySpec("Process", null,
                    new[] { Element("Step", interfaces: new[] { new InterfaceSpec("In", null) }) }, null),
            });

        var link = Render(spec).Descendants(C + "InternalLink").Single();

        Assert.Equal("Pump", (string?)link.Parent!.Attribute("Name"));
        Assert.Equal("Plant", (string?)link.Parent!.Parent!.Attribute("Name"));
    }

    /// <summary>The reader has to find a link wherever the writer put it.</summary>
    [Fact]
    public void A_written_link_is_read_back_as_a_connection()
    {
        using var ws = new Workspace();
        var store = new DocumentStore(ServerOptions.Parse(new[] { "--root", ws.Dir, "--write", ws.Dir }, _ => null));
        var spec = new DocumentSpec(null, null, null, null, null,
            new[]
            {
                new HierarchySpec("Plant", null,
                    new[]
                    {
                        Element("Pump", interfaces: new[] { new InterfaceSpec("Out", null) }),
                        Element("Tank", interfaces: new[] { new InterfaceSpec("In", null) }),
                    },
                    new[] { new LinkSpec("Pipe", "Plant/Pump/Out", "Plant/Tank/In") }),
            });

        AmlTools.WriteAmlDocument(store, Path.Combine(ws.Dir, "links.aml"), spec);

        Assert.Contains("--link Pipe", Answer.Text(AmlTools.GetNeighbors(store, "Plant/Pump")));
        Assert.Contains("no problems found", AmlTools.CheckText(store));
    }
}
