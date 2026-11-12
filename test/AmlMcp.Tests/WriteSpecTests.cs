using Xunit;

namespace AmlMcp.Tests;

/// <summary>
/// What a description refuses on its own, before anything is rendered. Only what the
/// description alone determines: whether a class path resolves is the write path's business.
/// </summary>
public sealed class WriteSpecTests
{
    private static ElementSpec Element(string name, params ElementSpec[] children) =>
        new(name, null, null, null, null, null, null, children.Length == 0 ? null : children, null);

    private static ElementSpec WithInterfaces(string name, params string[] interfaces) =>
        new(name, null, null, null, null, null, interfaces.Select(i => new InterfaceSpec(i, null)).ToList(), null, null);

    private static DocumentSpec Document(params HierarchySpec[] hierarchies) =>
        new(null, null, null, null, null, hierarchies);

    [Fact]
    public void An_empty_description_is_refused()
    {
        var faults = WriteSpec.Validate(new DocumentSpec(null, null, null, null, null, null));

        Assert.Contains(faults, f => f.Contains("nothing to write", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_description_with_only_libraries_is_accepted()
    {
        var spec = new DocumentSpec(null, null, null, null,
            new[] { new LibrarySpec("Lib", "SystemUnitClass", new[] { new ClassSpec("Drill", null, null, null, null) }) },
            null);

        Assert.Empty(WriteSpec.Validate(spec));
    }

    [Fact]
    public void Two_children_of_the_same_name_under_one_parent_are_refused()
    {
        var spec = Document(new HierarchySpec("Plant", null,
            new[] { Element("Line", Element("Drill"), Element("Drill")) }, null));

        var faults = WriteSpec.Validate(spec);

        Assert.Contains(faults, f => f.Contains("Plant/Line", StringComparison.Ordinal) && f.Contains("Drill", StringComparison.Ordinal));
    }

    [Fact]
    public void Two_hierarchies_of_the_same_name_are_refused()
    {
        var spec = Document(
            new HierarchySpec("Plant", null, new[] { Element("A") }, null),
            new HierarchySpec("Plant", null, new[] { Element("B") }, null));

        Assert.Contains(WriteSpec.Validate(spec), f => f.Contains("Plant", StringComparison.Ordinal));
    }

    [Fact]
    public void An_element_without_a_name_is_refused()
    {
        var spec = Document(new HierarchySpec("Plant", null, new[] { Element("  ") }, null));

        Assert.Contains(WriteSpec.Validate(spec), f => f.Contains("without a name", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_link_endpoint_that_names_no_interface_is_refused()
    {
        var spec = Document(new HierarchySpec("Plant", null,
            new[] { WithInterfaces("Pump", "Out"), WithInterfaces("Tank", "In") },
            new[] { new LinkSpec("Pipe", "Plant/Pump/Out", "Plant/Tank/Nowhere") }));

        Assert.Contains(WriteSpec.Validate(spec), f => f.Contains("Plant/Tank/Nowhere", StringComparison.Ordinal));
    }

    [Fact]
    public void A_link_endpoint_that_is_ambiguous_is_refused()
    {
        var spec = Document(new HierarchySpec("Plant", null,
            new[] { WithInterfaces("Pump", "Port", "Port"), WithInterfaces("Tank", "In") },
            new[] { new LinkSpec("Pipe", "Plant/Pump/Port", "Plant/Tank/In") }));

        Assert.Contains(WriteSpec.Validate(spec),
            f => f.Contains("Plant/Pump/Port", StringComparison.Ordinal) && f.Contains("twice", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_mirror_of_an_element_that_is_not_in_the_document_is_refused()
    {
        var mirror = new ElementSpec("Copy", null, null, null, null, null, null, null, "Plant/NoSuchThing");
        var spec = Document(new HierarchySpec("Plant", null, new[] { Element("Real"), mirror }, null));

        Assert.Contains(WriteSpec.Validate(spec), f => f.Contains("Plant/NoSuchThing", StringComparison.Ordinal));
    }

    [Fact]
    public void A_mirror_of_an_element_that_is_in_the_document_is_accepted()
    {
        var mirror = new ElementSpec("Copy", null, null, null, null, null, null, null, "Plant/Real");
        var spec = Document(new HierarchySpec("Plant", null, new[] { Element("Real"), mirror }, null));

        Assert.Empty(WriteSpec.Validate(spec));
    }

    [Fact]
    public void An_unknown_library_kind_is_refused_with_the_four_that_exist()
    {
        var spec = new DocumentSpec(null, null, null, null,
            new[] { new LibrarySpec("Lib", "widget", new[] { new ClassSpec("Drill", null, null, null, null) }) },
            null);

        var fault = Assert.Single(WriteSpec.Validate(spec));

        Assert.Contains("widget", fault, StringComparison.Ordinal);
        Assert.Contains("RoleClass", fault, StringComparison.Ordinal);
        Assert.Contains("AttributeType", fault, StringComparison.Ordinal);
    }

    [Fact]
    public void A_library_kind_is_accepted_in_its_short_form()
    {
        var spec = new DocumentSpec(null, null, null, null,
            new[] { new LibrarySpec("Lib", "role", new[] { new ClassSpec("Station", null, null, null, null) }) },
            null);

        Assert.Empty(WriteSpec.Validate(spec));
    }

    [Fact]
    public void A_sound_description_has_nothing_to_report()
    {
        var spec = new DocumentSpec("plant.aml", "3.0", "A line with two stations", null,
            new[] { new LibrarySpec("Lib", "SystemUnitClass", new[] { new ClassSpec("Drill", null, "A drill", null, null) }) },
            new[]
            {
                new HierarchySpec("Plant", "What is there",
                    new[]
                    {
                        new ElementSpec("Line", null, null, null, null, null, null,
                            new[]
                            {
                                new ElementSpec("Station1", "Lib/Drill", new[] { "Lib/Station" }, null, null,
                                    new[] { new AttributeSpec("Feed", "4.5", null, "mm/s", null, null) },
                                    new[] { new InterfaceSpec("Port", "Lib/Coupling") }, null, null),
                            }, null),
                    },
                    null),
            });

        Assert.Empty(WriteSpec.Validate(spec));
    }
}
