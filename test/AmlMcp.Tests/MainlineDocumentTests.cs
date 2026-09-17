using Xunit;

namespace AmlMcp.Tests;

/// <summary>
/// The CAEX 3.0 mainline document: links between plain interface IDs, a shared
/// interface, a link into nothing, a real duplicate ID next to an ID that only
/// differs in letter case, a typed reference, typed layout data, roles,
/// descriptions and nested children.
/// </summary>
public sealed class MainlineDocumentTests : IDisposable
{
    private readonly Workspace _ws = new();
    private readonly DocumentStore _store = new();

    public MainlineDocumentTests()
    {
        AmlTools.OpenDocumentText(_store, _ws.Write("rich.aml", Fixtures.Rich30));
    }

    public void Dispose() => _ws.Dispose();

    [Fact]
    public void Links_between_plain_interface_ids_are_resolved_in_both_directions()
    {
        var one = AmlTools.ElementCard(_store, "Shopfloor/Station1");
        var two = AmlTools.ElementCard(_store, "Shopfloor/Station2");

        Assert.Contains("--link OneToTwo (Port -> Port)-->  Shopfloor/Station2", one);
        Assert.Contains("--link OneToThree (Port -> Port)-->  Shopfloor/Station3", one);
        Assert.Contains("--link OneToTwo (Port -> Port)-->  Shopfloor/Station1", two);

        var data = Answer.Data(AmlTools.GetElement(_store, "Shopfloor/Station1"));
        var links = data.GetProperty("connections").EnumerateArray()
            .Where(c => c.GetProperty("kind").GetString() == "link").ToList();
        Assert.Equal(2, links.Count);
        Assert.All(links, l => Assert.False(l.GetProperty("crossHierarchy").GetBoolean()));
    }

    [Fact]
    public void An_interface_used_by_two_links_is_a_note_not_a_problem()
    {
        var check = AmlTools.CheckText(_store);
        var data = Answer.Data(AmlTools.CheckReferences(_store));

        Assert.Contains("note: interface Shopfloor/Station1/Port is used by 2 links", check);
        Assert.Contains(Answer.Strings(data.GetProperty("notes"), "kind"), k => k == "sharedInterface");
        Assert.DoesNotContain(Answer.Strings(data.GetProperty("problems"), "kind"), k => k == "sharedInterface");
    }

    [Fact]
    public void A_link_whose_partner_does_not_exist_is_reported_with_the_missing_id()
    {
        var check = AmlTools.CheckText(_store);
        var data = Answer.Data(AmlTools.CheckReferences(_store));

        Assert.Contains($"link without partner: Nowhere A=ok B=missing {{{Fixtures.MissingPartnerId}}}", check);
        Assert.Contains(data.GetProperty("problems").EnumerateArray(),
            p => p.GetProperty("kind").GetString() == "linkWithoutPartner" && p.GetProperty("message").GetString() == "Nowhere");
        Assert.Contains("InternalLinks: 3 total, 0 across hierarchies, 1 unresolved", AmlTools.OpenDocumentText(_store, Path.Combine(_ws.Dir, "rich.aml")));
    }

    // Change 7: the same ID twice is a problem, the same ID in different letter
    // case is one object and only worth a note.
    [Fact]
    public void A_repeated_id_is_a_problem_while_a_letter_case_variant_is_only_a_note()
    {
        var check = AmlTools.CheckText(_store);
        var data = Answer.Data(AmlTools.CheckReferences(_store));

        Assert.Contains($"duplicate ID: {{{Fixtures.DuplicatedId}}}", check);
        Assert.Contains($"note: {{{Fixtures.CaseVariantIdUpper}}} appears with different letter case", check);
        Assert.DoesNotContain($"duplicate ID: {{{Fixtures.CaseVariantIdUpper}}}", check);
        Assert.DoesNotContain($"duplicate ID: {{{Fixtures.CaseVariantIdLower}}}", check);

        var problems = data.GetProperty("problems").EnumerateArray().ToList();
        Assert.Single(problems, p => p.GetProperty("kind").GetString() == "duplicateId");
        Assert.Contains(Answer.Strings(data.GetProperty("notes"), "kind"), k => k == "idCaseVariant");
        Assert.DoesNotContain(Answer.Strings(data.GetProperty("problems"), "kind"), k => k == "idCaseVariant");

        var model = _store.Get(null);
        Assert.Equal(new[] { $"{{{Fixtures.DuplicatedId}}}" }, model.DuplicateIds);
        Assert.Equal(new[] { $"{{{Fixtures.CaseVariantIdUpper}}}" }, model.CaseVariantIds);
        // Both spellings address the one object the model keeps for them.
        Assert.Equal("path: Shopfloor/Cased", Line(AmlTools.ElementCard(_store, Fixtures.CaseVariantIdUpper), "path:"));
        Assert.Equal("path: Shopfloor/Cased", Line(AmlTools.ElementCard(_store, Fixtures.CaseVariantIdLower), "path:"));
    }

    // Change 8: a mirror reference that is really a class name without its library.
    [Fact]
    public void A_class_name_used_as_a_mirror_reference_names_the_class_that_was_meant()
    {
        var check = AmlTools.CheckText(_store);
        var data = Answer.Data(AmlTools.CheckReferences(_store));

        Assert.Contains("class path without library: Shopfloor/Twin refers to Drill", check);
        Assert.Contains("Did you mean RichUnitLib/Drill?", check);
        Assert.DoesNotContain("mirror without master", check);
        Assert.Contains(data.GetProperty("problems").EnumerateArray(),
            p => p.GetProperty("kind").GetString() == "classPathWithoutLibrary"
                 && p.GetProperty("message").GetString()!.Contains("probably RichUnitLib/Drill"));
        Assert.DoesNotContain(Answer.Strings(data.GetProperty("problems"), "kind"), k => k == "mirrorWithoutMaster");
    }

    [Fact]
    public void An_attribute_typed_xs_IDREF_is_a_reference_in_both_convention_modes()
    {
        foreach (var options in Modes)
        {
            var store = new DocumentStore(options);
            AmlTools.OpenDocumentText(store, Path.Combine(_ws.Dir, "rich.aml"));

            Assert.Contains("--Origin-->  Shopfloor/Station1", AmlTools.ElementCard(store, "Shopfloor/Station2"));
            Assert.Contains("<--Origin--  Shopfloor/Station2", AmlTools.ElementCard(store, "Shopfloor/Station1"));
            Assert.Contains("1 resolved", AmlTools.OpenDocumentText(store, Path.Combine(_ws.Dir, "rich.aml")));
            Assert.DoesNotContain("dangling reference", AmlTools.CheckText(store));
        }
    }

    [Fact]
    public void An_OMG_Diagram_Definition_type_is_layout_in_both_convention_modes()
    {
        foreach (var options in Modes)
        {
            var store = new DocumentStore(options);
            AmlTools.OpenDocumentText(store, Path.Combine(_ws.Dir, "rich.aml"));

            var card = AmlTools.ElementCard(store, "Shopfloor/Station1");
            Assert.DoesNotContain("Bounds", card);
            Assert.Contains("(1 layout attribute group(s) hidden, use includeLayout=true)", card);
            Assert.Contains("Bounds/width = 120", AmlTools.ElementCard(store, "Shopfloor/Station1", includeLayout: true));

            var hidden = Answer.Data(AmlTools.GetElement(store, "Shopfloor/Station1"));
            Assert.Equal(1, hidden.GetProperty("hiddenPresentationAttributes").GetInt32());
            Assert.DoesNotContain(Answer.Strings(hidden.GetProperty("attributes"), "path"), p => p!.StartsWith("Bounds"));

            var shown = Answer.Data(AmlTools.GetElement(store, "Shopfloor/Station1", includeLayout: true));
            Assert.Equal(0, shown.GetProperty("hiddenPresentationAttributes").GetInt32());
            Assert.Contains(Answer.Strings(shown.GetProperty("attributes"), "path"), p => p == "Bounds/width");
        }
    }

    [Fact]
    public void Role_requirements_description_and_nested_children_reach_both_halves()
    {
        var card = AmlTools.ElementCard(_store, "Shopfloor/Station1");
        var data = Answer.Data(AmlTools.GetElement(_store, "Shopfloor/Station1"));

        Assert.Contains("role: RichRoleLib/Station", card);
        Assert.DoesNotContain("role: RichRoleLib/Station  (NOT resolved)", card);
        Assert.Contains("description: The drilling station of the line.", card);
        Assert.Contains("children (1):", card);
        Assert.Contains("Spindle", card);

        Assert.Equal(new[] { "RichRoleLib/Station" },
            data.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ToArray());
        Assert.Equal("The drilling station of the line.", data.GetProperty("description").GetString());
        Assert.Equal("Shopfloor/Station1/Spindle", data.GetProperty("children")[0].GetProperty("path").GetString());

        // A role requirement is a filter, not only a label.
        var byRole = AmlTools.FindElementsText(_store, roleContains: "Station");
        Assert.Contains("Shopfloor/Station1", byRole);
        Assert.DoesNotContain("Station2", byRole);
        Assert.Equal(new[] { "Shopfloor/Station1" },
            Answer.Strings(Answer.Data(AmlTools.FindElements(_store, roleContains: "Station")).GetProperty("elements"), "path").ToArray());
    }

    [Fact]
    public void The_check_finds_exactly_the_three_planted_problems()
    {
        var check = AmlTools.CheckText(_store);
        var data = Answer.Data(AmlTools.CheckReferences(_store));

        Assert.Contains("3 problem(s)", check);
        Assert.Contains("checked: 4 library classes, 3 links, 1 references, 16 IDs", check);
        Assert.Equal(3, data.GetProperty("problems").GetArrayLength());
        Assert.Equal(2, data.GetProperty("notes").GetArrayLength());
        Assert.False(data.GetProperty("ok").GetBoolean());
        Assert.Equal(3, data.GetProperty("links").GetInt32());
        Assert.Equal(16, data.GetProperty("ids").GetInt32());
    }

    // Change 11: IsDefault says where the value came from, InheritedFrom says who
    // defines it. An inherited Value is not a default.
    [Fact]
    public void A_default_value_is_marked_as_one_and_an_inherited_value_is_not()
    {
        var card = AmlTools.ElementCard(_store, "Shopfloor/Station1");
        var data = Answer.Data(AmlTools.GetElement(_store, "Shopfloor/Station1"));

        Assert.Contains("Feed = 4.5 mm/s (default)", card);
        Assert.Contains("SpindleSpeed = 3000 1/min  (from RichUnitLib/Drill)", card);
        Assert.Contains("Vendor = ACME  (from RichUnitLib/Drill)", card);
        Assert.Contains("Coolant = oil  (from RichUnitLib/Drill)", card);
        Assert.DoesNotContain("water", card);

        var attributes = data.GetProperty("attributes").EnumerateArray().ToDictionary(a => a.GetProperty("path").GetString()!);
        Assert.True(attributes["Feed"].GetProperty("isDefault").GetBoolean());
        Assert.False(attributes["Feed"].TryGetProperty("inheritedFrom", out _));
        Assert.True(attributes["SpindleSpeed"].GetProperty("isDefault").GetBoolean());
        Assert.Equal("RichUnitLib/Drill", attributes["SpindleSpeed"].GetProperty("inheritedFrom").GetString());
        Assert.False(attributes["Vendor"].GetProperty("isDefault").GetBoolean());
        Assert.Equal("RichUnitLib/Drill", attributes["Vendor"].GetProperty("inheritedFrom").GetString());
        // A value that overrides its own default is a value, not a default.
        Assert.Equal("oil", attributes["Coolant"].GetProperty("value").GetString());
        Assert.False(attributes["Coolant"].GetProperty("isDefault").GetBoolean());
    }

    [Fact]
    public void The_class_card_marks_defaults_the_same_way_and_prints_their_unit()
    {
        var card = AmlTools.ClassCard(_store, "RichUnitLib/Drill");
        var data = Answer.Data(AmlTools.GetClass(_store, "RichUnitLib/Drill"));

        Assert.Contains("SpindleSpeed = 3000 1/min (default)", card);
        Assert.Contains("Vendor = ACME", card);
        Assert.DoesNotContain("Vendor = ACME (default)", card);
        Assert.Contains("Coolant = oil", card);
        Assert.DoesNotContain("Coolant = oil (default)", card);

        // An object with a total, not a bare JSON array.
        Assert.Equal(1, data.GetProperty("total").GetInt32());
        var attributes = data.GetProperty("classes")[0].GetProperty("attributes").EnumerateArray()
            .ToDictionary(a => a.GetProperty("path").GetString()!);
        Assert.True(attributes["SpindleSpeed"].GetProperty("isDefault").GetBoolean());
        Assert.Equal("1/min", attributes["SpindleSpeed"].GetProperty("unit").GetString());
        Assert.False(attributes["Vendor"].GetProperty("isDefault").GetBoolean());
        Assert.Equal("oil", attributes["Coolant"].GetProperty("value").GetString());
        Assert.False(attributes["Coolant"].GetProperty("isDefault").GetBoolean());
    }

    // Change 6: paths are matched without regard to letter case, like names and IDs.
    [Fact]
    public void Element_paths_are_matched_without_regard_to_letter_case()
    {
        Assert.Contains("path: Shopfloor/Station1/Spindle", AmlTools.ElementCard(_store, "shopfloor/station1/spindle"));
        Assert.Contains("path: Shopfloor/Station1/Spindle", AmlTools.ElementCard(_store, "SHOPFLOOR/STATION1/SPINDLE"));
        Assert.Contains("path: Shopfloor/Station1", AmlTools.ElementCard(_store, "shopfloor/Station1"));
        Assert.Equal("Shopfloor/Station1/Spindle/Tool",
            Answer.Data(AmlTools.GetElement(_store, "shopfloor/station1/spindle/tool")).GetProperty("path").GetString());
    }

    private static IEnumerable<ServerOptions> Modes =>
        new[] { ServerOptions.Default, ServerOptions.Parse(new[] { "--strict-conventions" }) };

    private static string Line(string text, string startsWith) =>
        text.Split('\n').Select(l => l.Trim()).First(l => l.StartsWith(startsWith, StringComparison.Ordinal));
}
