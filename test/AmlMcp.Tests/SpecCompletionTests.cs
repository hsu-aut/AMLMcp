using Xunit;

namespace AmlMcp.Tests;

/// <summary>
/// A description that names no library is completed with the classes it uses, so that the
/// written document stands on its own. A description that names one is left to it.
/// </summary>
public sealed class SpecCompletionTests
{
    private static DocumentSpec With(ElementSpec element, params ExternalLibrarySpec[] external) =>
        new(null, null, null, external.Length == 0 ? null : external, null,
            new[] { new HierarchySpec("Plant", null, new[] { element }, null) });

    private static ElementSpec Plain(string name, string? @class = null, string[]? roles = null,
                                     string[]? supportedRoles = null, InterfaceSpec[]? interfaces = null) =>
        new(name, @class, roles, supportedRoles, null, null, interfaces, null, null);

    private static IReadOnlyList<ClassSpec> ClassesOf(DocumentSpec spec, string library) =>
        (spec.Libraries ?? Array.Empty<LibrarySpec>())
        .Where(l => l.Name == library).SelectMany(l => l.Classes).ToList();

    private static ElementSpec FirstElement(DocumentSpec spec) => spec.Hierarchies![0].Elements![0];

    [Fact]
    public void A_class_no_library_defines_is_added_to_a_library_of_the_document()
    {
        var completed = SpecCompletion.Complete(With(Plain("Station1", "Drill")));

        Assert.Contains(ClassesOf(completed, "AmlMcpUnitLib"), c => c.Name == "Drill");
        Assert.Contains(completed.Libraries!, l => l.Name == "AmlMcpUnitLib" && l.Kind == "SystemUnitClass");
    }

    /// <summary>
    /// A bare class name does not resolve on its own, and the reference check calls that a
    /// problem. The completion therefore points the element at the library it just made.
    /// </summary>
    [Fact]
    public void A_bare_class_name_is_pointed_at_the_library_that_now_carries_it()
    {
        var completed = SpecCompletion.Complete(With(Plain("Station1", "Drill")));

        Assert.Equal("AmlMcpUnitLib/Drill", FirstElement(completed).Class);
    }

    [Fact]
    public void A_role_no_library_defines_is_added_as_a_role_class()
    {
        var completed = SpecCompletion.Complete(With(Plain("Station1", roles: new[] { "Station" })));

        Assert.Contains(completed.Libraries!, l => l.Name == "AmlMcpRoleLib" && l.Kind == "RoleClass");
        Assert.Contains(ClassesOf(completed, "AmlMcpRoleLib"), c => c.Name == "Station");
        Assert.Equal(new[] { "AmlMcpRoleLib/Station" }, FirstElement(completed).Roles);
    }

    [Fact]
    public void A_supported_role_is_treated_the_same_way()
    {
        var completed = SpecCompletion.Complete(With(Plain("Station1", supportedRoles: new[] { "Station" })));

        Assert.Contains(ClassesOf(completed, "AmlMcpRoleLib"), c => c.Name == "Station");
        Assert.Equal(new[] { "AmlMcpRoleLib/Station" }, FirstElement(completed).SupportedRoles);
    }

    [Fact]
    public void An_interface_class_no_library_defines_is_added_as_an_interface_class()
    {
        var element = Plain("Station1", interfaces: new[] { new InterfaceSpec("Port", "Coupling") });

        var completed = SpecCompletion.Complete(With(element));

        Assert.Contains(completed.Libraries!, l => l.Name == "AmlMcpInterfaceLib" && l.Kind == "InterfaceClass");
        Assert.Contains(ClassesOf(completed, "AmlMcpInterfaceLib"), c => c.Name == "Coupling");
        Assert.Equal("AmlMcpInterfaceLib/Coupling", FirstElement(completed).Interfaces![0].Class);
    }

    [Fact]
    public void A_class_an_external_library_carries_is_left_alone()
    {
        var completed = SpecCompletion.Complete(
            With(Plain("Station1", "Demo@DemoUnitLib/Drill"), new ExternalLibrarySpec("Demo", "DemoLib.aml")));

        Assert.Null(completed.Libraries);
        Assert.Equal("Demo@DemoUnitLib/Drill", FirstElement(completed).Class);
    }

    [Fact]
    public void A_class_a_library_of_the_document_already_defines_is_left_alone()
    {
        var spec = new DocumentSpec(null, null, null, null,
            new[] { new LibrarySpec("Lib", "SystemUnitClass", new[] { new ClassSpec("Drill", null, null, null, null) }) },
            new[] { new HierarchySpec("Plant", null, new[] { Plain("Station1", "Lib/Drill") }, null) });

        var completed = SpecCompletion.Complete(spec);

        Assert.Single(completed.Libraries!);
        Assert.Equal("Lib/Drill", FirstElement(completed).Class);
    }

    /// <summary>
    /// A path whose library is not in the document gets that library, not the generated one:
    /// the document then says what the description said.
    /// </summary>
    [Fact]
    public void A_path_naming_a_library_that_is_missing_gets_that_library()
    {
        var completed = SpecCompletion.Complete(With(Plain("Station1", "PlantLib/Drill")));

        Assert.Contains(completed.Libraries!, l => l.Name == "PlantLib" && l.Kind == "SystemUnitClass");
        Assert.Contains(ClassesOf(completed, "PlantLib"), c => c.Name == "Drill");
        Assert.Equal("PlantLib/Drill", FirstElement(completed).Class);
    }

    [Fact]
    public void A_class_used_twice_is_added_once()
    {
        var spec = new DocumentSpec(null, null, null, null, null,
            new[]
            {
                new HierarchySpec("Plant", null,
                    new[] { Plain("Station1", "Drill"), Plain("Station2", "Drill") }, null),
            });

        var completed = SpecCompletion.Complete(spec);

        Assert.Single(ClassesOf(completed, "AmlMcpUnitLib"));
    }

    [Fact]
    public void A_class_a_child_uses_is_found_too()
    {
        var child = Plain("Spindle", "Drill");
        var parent = new ElementSpec("Station1", null, null, null, null, null, null, new[] { child }, null);

        var completed = SpecCompletion.Complete(With(parent));

        Assert.Contains(ClassesOf(completed, "AmlMcpUnitLib"), c => c.Name == "Drill");
        Assert.Equal("AmlMcpUnitLib/Drill", FirstElement(completed).Children![0].Class);
    }

    /// <summary>
    /// An object reference is written with an attribute type, and an attribute type that no
    /// library carries would not resolve either.
    /// </summary>
    [Fact]
    public void A_reference_brings_the_attribute_type_it_needs()
    {
        var pointing = new ElementSpec("Note", null, null, null, null,
            new[] { new AttributeSpec("refObj", null, null, null, "Plant/Station1", null) }, null, null, null);

        var completed = SpecCompletion.Complete(With(pointing));

        Assert.Contains(completed.Libraries!, l => l.Name == "AmlMcpReferenceLib" && l.Kind == "AttributeType");
        Assert.Contains(ClassesOf(completed, "AmlMcpReferenceLib"), c => c.Name == "refObj");
    }

    [Fact]
    public void A_description_that_names_no_class_at_all_gains_no_library()
    {
        var completed = SpecCompletion.Complete(With(Plain("Station1")));

        Assert.Null(completed.Libraries);
    }

    [Fact]
    public void Completion_changes_nothing_else_about_the_description()
    {
        var spec = new DocumentSpec("plant.aml", "3.0", "A line", null, null,
            new[] { new HierarchySpec("Plant", "What is there", new[] { Plain("Station1") }, null) });

        var completed = SpecCompletion.Complete(spec);

        Assert.Equal(spec, completed);
    }
}
