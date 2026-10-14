using ModelContextProtocol;
using Xunit;

namespace AmlMcp.Tests;

/// <summary>
/// Behaviour the suite relied on without checking it. A mutation probe over the model and
/// the tools showed each of these could be broken with every test still green.
/// </summary>
public sealed class ClassUsageDetailTests : IDisposable
{
    private readonly Workspace _ws = new();
    private readonly DocumentStore _store = new();

    private const string Document = """
        <?xml version="1.0" encoding="utf-8"?>
        <CAEXFile SchemaVersion="3.0" FileName="usage.aml" xmlns="http://www.dke.de/CAEX">
          <RoleClassLib Name="Roles">
            <RoleClass Name="Machine" />
          </RoleClassLib>
          <SystemUnitClassLib Name="Lib">
            <SystemUnitClass Name="Pump" />
            <SystemUnitClass Name="BigPump" RefBaseClassPath="Lib/Pump" />
            <SystemUnitClass Name="SmallPump" RefBaseClassPath="Lib/Pump" />
            <SystemUnitClass Name="Special" RefBaseClassPath="Lib/BigPump" />
          </SystemUnitClassLib>
          <InstanceHierarchy Name="Plant" ID="{cccc0000-0000-0000-0000-0000000000f1}">
            <InternalElement Name="P1" ID="{cccc0000-0000-0000-0000-000000000001}" RefBaseSystemUnitPath="Lib/Pump">
              <RoleRequirements RefBaseRoleClassPath="Roles/Machine" />
              <SupportedRoleClass RefRoleClassPath="Roles/Machine" />
            </InternalElement>
          </InstanceHierarchy>
        </CAEXFile>
        """;

    public ClassUsageDetailTests() => AmlTools.OpenDocumentText(_store, _ws.Write("usage.aml", Document));

    public void Dispose() => _ws.Dispose();

    /// <summary>One element, two mentions of the same role: one user, not two.</summary>
    [Fact]
    public void An_element_that_names_a_class_twice_counts_once()
    {
        Assert.Contains("used by 1 element(s) in this document", AmlTools.ClassCard(_store, "Roles/Machine"));
    }

    /// <summary>
    /// The role is named by a RoleRequirements child, not by the element itself. The count
    /// is about the element that holds it.
    /// </summary>
    [Fact]
    public void A_class_named_by_a_child_counts_for_the_element_that_holds_it()
    {
        var data = Answer.Data(AmlTools.GetClass(_store, "Roles/Machine"));
        var one = data.GetProperty("classes").EnumerateArray().First();

        Assert.Equal(1, one.GetProperty("usedByElements").GetInt32());
        Assert.Equal(0, one.GetProperty("usedByClasses").GetInt32());
    }

    /// <summary>The two numbers are different here, so a swap would show.</summary>
    [Fact]
    public void The_structured_card_keeps_elements_and_library_classes_apart()
    {
        var data = Answer.Data(AmlTools.GetClass(_store, "Lib/Pump"));
        var one = data.GetProperty("classes").EnumerateArray().First();

        Assert.Equal(1, one.GetProperty("usedByElements").GetInt32());
        Assert.Equal(2, one.GetProperty("usedByClasses").GetInt32());
        Assert.Contains("used by 1 element(s) in this document, and by 2 class(es) in the libraries",
            AmlTools.ClassCard(_store, "Lib/Pump"));
    }

    /// <summary>A single library class is worth saying too.</summary>
    [Fact]
    public void One_class_in_the_libraries_is_still_reported()
    {
        Assert.Contains("used by 0 element(s) in this document, and by 1 class(es) in the libraries",
            AmlTools.ClassCard(_store, "Lib/BigPump"));
    }
}

/// <summary>What counts as a reference, and what only looks like one.</summary>
public sealed class ReferenceConventionDetailTests : IDisposable
{
    private readonly Workspace _ws = new();
    private readonly DocumentStore _store = new();

    private const string Document = """
        <?xml version="1.0" encoding="utf-8"?>
        <CAEXFile SchemaVersion="3.0" FileName="conventions.aml" xmlns="http://www.dke.de/CAEX">
          <AttributeTypeLib Name="HouseLib">
            <AttributeType Name="refObj" AttributeDataType="xs:string" />
          </AttributeTypeLib>
          <InstanceHierarchy Name="Plant" ID="{dddd0000-0000-0000-0000-0000000000f1}">
            <InternalElement Name="Note" ID="{dddd0000-0000-0000-0000-000000000001}">
              <Attribute Name="pointsAt" AttributeDataType="xs:string" RefAttributeType="House@HouseLib/refObj"><Value>{dddd0000-0000-0000-0000-000000000002}</Value></Attribute>
            </InternalElement>
            <InternalElement Name="Target" ID="{dddd0000-0000-0000-0000-000000000002}">
              <Attribute Name="refKey" AttributeDataType="xs:ID"><Value>{dddd0000-0000-0000-0000-000000000001}</Value></Attribute>
            </InternalElement>
          </InstanceHierarchy>
        </CAEXFile>
        """;

    public ReferenceConventionDetailTests() =>
        AmlTools.OpenDocumentText(_store, _ws.Write("conventions.aml", Document));

    public void Dispose() => _ws.Dispose();

    /// <summary>
    /// An attribute type ending in /refObj is an object reference whatever library it
    /// comes from, so a house library works like the shared one.
    /// </summary>
    [Fact]
    public void An_attribute_type_ending_in_refObj_is_a_reference()
    {
        Assert.Contains("--pointsAt-->  Plant/Target", Answer.Text(AmlTools.GetNeighbors(_store, "Plant/Note")));
    }

    /// <summary>An attribute that declares an ID is not a reference to one.</summary>
    [Fact]
    public void An_attribute_typed_xs_ID_is_no_reference_however_it_is_named()
    {
        var neighbours = Answer.Text(AmlTools.GetNeighbors(_store, "Plant/Target"));

        Assert.DoesNotContain("refKey", neighbours);
        Assert.DoesNotContain("refKey", AmlTools.CheckText(_store));
    }
}

/// <summary>Paths, aliases and the bracketed spellings CAEX allows.</summary>
public sealed class PathAndAliasDetailTests : IDisposable
{
    private readonly Workspace _ws = new();
    private readonly DocumentStore _store = new();

    private const string Document = """
        <?xml version="1.0" encoding="utf-8"?>
        <CAEXFile FileName="paths.aml" SchemaVersion="2.15">
          <InstanceHierarchy Name="Shopfloor">
            <InternalElement Name="Station1" ID="{eeee0000-0000-0000-0000-000000000001}">
              <InternalElement Name="Spindle" ID="{eeee0000-0000-0000-0000-000000000002}">
                <ExternalInterface Name="Power:AC" ID="{eeee0000-0000-0000-0000-0000000000a1}" />
              </InternalElement>
            </InternalElement>
            <InternalElement Name="Supply" ID="{eeee0000-0000-0000-0000-000000000003}">
              <ExternalInterface Name="Out" ID="{eeee0000-0000-0000-0000-0000000000b1}" />
            </InternalElement>
            <InternalLink Name="Feeds"
                          RefPartnerSideA="[{eeee0000-0000-0000-0000-000000000002}]:[Power:AC]"
                          RefPartnerSideB="[{eeee0000-0000-0000-0000-000000000003}]:[Out]" />
          </InstanceHierarchy>
        </CAEXFile>
        """;

    public PathAndAliasDetailTests() => AmlTools.OpenDocumentText(_store, _ws.Write("paths.aml", Document));

    public void Dispose() => _ws.Dispose();

    /// <summary>A path may start at an element instead of at the hierarchy.</summary>
    [Fact]
    public void A_path_without_the_hierarchy_in_front_still_finds_the_element()
    {
        Assert.Contains("path: Shopfloor/Station1/Spindle", AmlTools.ElementCard(_store, "Station1/Spindle"));
    }

    /// <summary>The brackets are there because the name holds the separator.</summary>
    [Fact]
    public void A_colon_inside_the_brackets_does_not_split_the_link_partner()
    {
        Assert.Contains("no problems found", AmlTools.CheckText(_store));
        Assert.Contains("--link Feeds", Answer.Text(AmlTools.GetNeighbors(_store, "Shopfloor/Station1/Spindle")));
    }

    /// <summary>Only the first segment can carry an alias; a later '@' belongs to the name.</summary>
    [Fact]
    public void An_at_sign_in_a_later_segment_is_part_of_the_name()
    {
        var (alias, plain, segments) = AmlModel.SplitClassPath("Lib/Class@variant");

        Assert.Null(alias);
        Assert.Equal("Lib/Class@variant", plain);
        Assert.Equal(2, segments);
    }
}

/// <summary>
/// An alias is matched without regard to letter case, as CAEX names are elsewhere.
/// </summary>
public sealed class AliasCaseTests : IDisposable
{
    private readonly Workspace _ws = new();
    private readonly DocumentStore _store = new();

    private const string Library = """
        <?xml version="1.0" encoding="utf-8"?>
        <CAEXFile SchemaVersion="3.0" FileName="StdLib.aml" xmlns="http://www.dke.de/CAEX">
          <SystemUnitClassLib Name="Shared">
            <SystemUnitClass Name="Bolt" />
          </SystemUnitClassLib>
        </CAEXFile>
        """;

    private const string Document = """
        <?xml version="1.0" encoding="utf-8"?>
        <CAEXFile SchemaVersion="3.0" FileName="user.aml" xmlns="http://www.dke.de/CAEX">
          <ExternalReference Path="StdLib.aml" Alias="Std" />
          <SystemUnitClassLib Name="Shared">
            <SystemUnitClass Name="Nut" />
          </SystemUnitClassLib>
          <InstanceHierarchy Name="Plant" ID="{ffff0000-0000-0000-0000-0000000000f1}">
            <InternalElement Name="Fastener" ID="{ffff0000-0000-0000-0000-000000000001}" RefBaseSystemUnitPath="STD@Shared/Nut" />
          </InstanceHierarchy>
        </CAEXFile>
        """;

    public AliasCaseTests()
    {
        _ws.Write("StdLib.aml", Library);
        AmlTools.OpenDocumentText(_store, _ws.Write("user.aml", Document));
    }

    public void Dispose() => _ws.Dispose();

    [Fact]
    public void An_alias_spelt_in_another_case_is_still_the_declared_one()
    {
        var check = AmlTools.CheckText(_store);

        Assert.Contains("alias does not carry the class: STD@Shared/Nut", check);
        // The finding is the only one, so it has to be counted as a problem.
        Assert.Contains("1 problem(s)", check);
        Assert.DoesNotContain("no problems found", check);
    }
}

/// <summary>Every list the answers cut short shows the number it cut.</summary>
public sealed class TruncationDetailTests : IDisposable
{
    private readonly Workspace _ws = new();
    private readonly DocumentStore _store = new();

    public TruncationDetailTests()
    {
        var attributes = string.Join("\n", Enumerable.Range(0, 60)
            .Select(i => $"      <Attribute Name=\"A{i:00}\" AttributeDataType=\"xs:string\"><DefaultValue>{i}</DefaultValue></Attribute>"));
        var subclasses = string.Join("\n", Enumerable.Range(0, 25)
            .Select(i => $"    <SystemUnitClass Name=\"Small{i:00}\" RefBaseClassPath=\"Lib/Big\" />"));
        var children = string.Join("\n", Enumerable.Range(0, 35)
            .Select(i => $"      <InternalElement Name=\"Child{i:00}\" ID=\"{{10100000-0000-0000-0000-{i:000000000000}}}\" />"));
        var document = $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <CAEXFile SchemaVersion="3.0" FileName="many.aml" xmlns="http://www.dke.de/CAEX">
              <SystemUnitClassLib Name="Lib">
                <SystemUnitClass Name="Big">
            {{attributes}}
                </SystemUnitClass>
            {{subclasses}}
              </SystemUnitClassLib>
              <InstanceHierarchy Name="Plant" ID="{10100000-0000-0000-0000-0000000000f1}">
                <InternalElement Name="Rack" ID="{10100000-0000-0000-0000-0000000000e1}">
            {{children}}
                </InternalElement>
              </InstanceHierarchy>
            </CAEXFile>
            """;
        AmlTools.OpenDocumentText(_store, _ws.Write("many.aml", document));
    }

    public void Dispose() => _ws.Dispose();

    private static int Lines(string text, Func<string, bool> matches) =>
        text.Split('\n').Count(l => matches(l));

    [Fact]
    public void A_class_shows_the_first_fifty_attributes_and_counts_the_rest()
    {
        var card = AmlTools.ClassCard(_store, "Lib/Big");

        Assert.Equal(50, Lines(card, l => l.TrimStart().StartsWith("A", StringComparison.Ordinal) && l.Contains(" = ")));
        Assert.Contains("... 10 more", card);
    }

    [Fact]
    public void A_class_lists_twenty_subclasses_and_counts_the_rest()
    {
        var card = AmlTools.ClassCard(_store, "Lib/Big");
        var line = card.Split('\n').Single(l => l.TrimStart().StartsWith("subclasses:", StringComparison.Ordinal));

        Assert.Equal(20, line.Split(", ").Count(part => part.Contains("Small", StringComparison.Ordinal)));
        Assert.Contains("... 5 more", line);
    }

    [Fact]
    public void An_element_lists_thirty_children_and_counts_the_rest()
    {
        var card = AmlTools.ElementCard(_store, "Plant/Rack");

        Assert.Equal(30, Lines(card, l => l.Contains("  Child", StringComparison.Ordinal)));
        Assert.Contains("... 5 more", card);
    }

    [Fact]
    public void The_search_shows_exactly_as_many_hits_as_the_limit_allows()
    {
        var text = AmlTools.FindElementsText(_store, "Child", limit: 10);

        Assert.Contains("35 match(es), showing 10", text);
        Assert.Equal(10, Lines(text, l => l.Contains("Plant/Rack/Child", StringComparison.Ordinal)));
    }
}

/// <summary>The counts in the reference check, where one finding must stay one.</summary>
public sealed class CheckCountingTests : IDisposable
{
    private readonly Workspace _ws = new();
    private readonly DocumentStore _store = new();

    private const string Document = """
        <?xml version="1.0" encoding="utf-8"?>
        <CAEXFile SchemaVersion="3.0" FileName="thrice.aml" xmlns="http://www.dke.de/CAEX">
          <InstanceHierarchy Name="Plant" ID="{12120000-0000-0000-0000-0000000000f1}">
            <InternalElement Name="One" ID="{12120000-0000-0000-0000-000000000001}" />
            <InternalElement Name="Two" ID="{12120000-0000-0000-0000-000000000001}" />
            <InternalElement Name="Three" ID="{12120000-0000-0000-0000-000000000001}" />
          </InstanceHierarchy>
        </CAEXFile>
        """;

    public CheckCountingTests() => AmlTools.OpenDocumentText(_store, _ws.Write("thrice.aml", Document));

    public void Dispose() => _ws.Dispose();

    [Fact]
    public void An_id_used_three_times_is_one_finding_that_names_all_three()
    {
        var check = AmlTools.CheckText(_store);
        var data = Answer.Data(AmlTools.CheckReferences(_store));

        Assert.Equal(1, check.Split('\n').Count(l => l.Contains("duplicate ID:", StringComparison.Ordinal)));
        Assert.Contains("Plant/One, Plant/Two, Plant/Three", check);
        Assert.Contains("1 problem(s)", check);
        Assert.Single(data.GetProperty("problems").EnumerateArray(),
            p => p.GetProperty("kind").GetString() == "duplicateId");
    }
}

/// <summary>The markers and limits of the remaining answers.</summary>
public sealed class ListMarkerTests : IDisposable
{
    private readonly Workspace _ws = new();

    public void Dispose() => _ws.Dispose();

    private DocumentStore Open(string fileName, string document)
    {
        var store = new DocumentStore();
        AmlTools.OpenDocumentText(store, _ws.Write(fileName, document));
        return store;
    }

    /// <summary>A mirror in a hit list is not the original and says so.</summary>
    [Fact]
    public void A_mirror_is_marked_in_the_search_results()
    {
        var store = Open("mirror-hit.aml", """
            <?xml version="1.0" encoding="utf-8"?>
            <CAEXFile SchemaVersion="3.0" FileName="mirror-hit.aml" xmlns="http://www.dke.de/CAEX">
              <InstanceHierarchy Name="Plant" ID="{13130000-0000-0000-0000-0000000000f1}">
                <InternalElement Name="Motor" ID="{13130000-0000-0000-0000-000000000001}" />
              </InstanceHierarchy>
              <InstanceHierarchy Name="Groups" ID="{13130000-0000-0000-0000-0000000000f2}">
                <InternalElement Name="Motor" ID="{13130000-0000-0000-0000-000000000002}" RefBaseSystemUnitPath="{13130000-0000-0000-0000-000000000001}" />
              </InstanceHierarchy>
            </CAEXFile>
            """);

        var text = AmlTools.FindElementsText(store, "Motor");

        Assert.Contains("Groups/Motor", text);
        Assert.Equal(1, text.Split('\n').Count(l => l.Contains("(mirror)", StringComparison.Ordinal)));
    }

    /// <summary>CAEX 2.15 leaves a hierarchy without an ID, and an empty "id=" says nothing.</summary>
    [Fact]
    public void A_path_step_without_an_id_prints_no_empty_id()
    {
        var store = Open("no-id.aml", """
            <?xml version="1.0" encoding="utf-8"?>
            <CAEXFile FileName="no-id.aml" SchemaVersion="2.15">
              <InstanceHierarchy Name="Plant">
                <InternalElement Name="A" ID="{14140000-0000-0000-0000-000000000001}" />
                <InternalElement Name="B" ID="{14140000-0000-0000-0000-000000000002}" />
              </InstanceHierarchy>
            </CAEXFile>
            """);

        var text = AmlTools.FindPathText(store, "Plant/A", "Plant/B");

        Assert.Contains("Path with 2 step(s)", text);
        // The hierarchy in between has no ID, and "id=" with nothing after it says nothing.
        Assert.DoesNotContain(text.Split('\n').Select(l => l.TrimEnd()), l => l.EndsWith("id=", StringComparison.Ordinal));
        Assert.Contains(text.Split('\n'), l => l.Contains("[Plant]", StringComparison.Ordinal));
    }

    /// <summary>A class card hides layout attributes, and says that it did.</summary>
    [Fact]
    public void A_class_says_when_it_hid_layout_attributes()
    {
        var store = Open("layout.aml", """
            <?xml version="1.0" encoding="utf-8"?>
            <CAEXFile SchemaVersion="3.0" FileName="layout.aml" xmlns="http://www.dke.de/CAEX">
              <AttributeTypeLib Name="OMG_DD_AttributeTypeLib">
                <AttributeType Name="DD_Bounds" AttributeDataType="xs:string" />
              </AttributeTypeLib>
              <SystemUnitClassLib Name="Lib">
                <SystemUnitClass Name="Box">
                  <Attribute Name="Bounds" AttributeDataType="xs:string" RefAttributeType="OMG_DD@OMG_DD_AttributeTypeLib/DD_Bounds">
                    <Attribute Name="width" AttributeDataType="xs:double"><Value>10</Value></Attribute>
                  </Attribute>
                  <Attribute Name="Weight" AttributeDataType="xs:double"><DefaultValue>3</DefaultValue></Attribute>
                </SystemUnitClass>
              </SystemUnitClassLib>
            </CAEXFile>
            """);

        var card = AmlTools.ClassCard(store, "Lib/Box");

        Assert.Contains("layout attribute group(s) not shown", card);
        Assert.DoesNotContain("Bounds/width", card);
    }

    /// <summary>
    /// A document that names itself in an ExternalReference indexes every class twice,
    /// plainly and under the alias. A subclass is still one subclass.
    /// </summary>
    [Fact]
    public void A_subclass_reachable_under_two_names_is_listed_once()
    {
        var store = Open("self.aml", """
            <?xml version="1.0" encoding="utf-8"?>
            <CAEXFile SchemaVersion="3.0" FileName="self.aml" xmlns="http://www.dke.de/CAEX">
              <ExternalReference Path="self.aml" Alias="Self" />
              <SystemUnitClassLib Name="Lib">
                <SystemUnitClass Name="Base" />
                <SystemUnitClass Name="Derived" RefBaseClassPath="Lib/Base" />
              </SystemUnitClassLib>
            </CAEXFile>
            """);

        var line = AmlTools.ClassCard(store, "Lib/Base").Split('\n')
            .Single(l => l.TrimStart().StartsWith("subclasses:", StringComparison.Ordinal));

        Assert.Equal(1, line.Split(", ").Count(part => part.Contains("Derived", StringComparison.Ordinal)));
    }

    /// <summary>Too many classes of one name is a list to choose from, not five cards.</summary>
    [Fact]
    public void A_name_that_matches_five_classes_is_refused_with_the_list()
    {
        var libraries = string.Join("\n", Enumerable.Range(0, 5)
            .Select(i => $"  <RoleClassLib Name=\"Lib{i}\">\n    <RoleClass Name=\"Thing\" />\n  </RoleClassLib>"));
        var store = Open("many-names.aml", $"""
            <?xml version="1.0" encoding="utf-8"?>
            <CAEXFile SchemaVersion="3.0" FileName="many-names.aml" xmlns="http://www.dke.de/CAEX">
            {libraries}
            </CAEXFile>
            """);

        var ex = Assert.Throws<McpException>(() => AmlTools.ClassCard(store, "Thing"));

        Assert.Contains("matches 5 classes", ex.Message);
        Assert.Contains("Lib4/Thing", ex.Message);
    }

    /// <summary>A hub with more neighbours than fit says how many it left out.</summary>
    [Fact]
    public void More_than_a_hundred_and_fifty_neighbours_are_cut_with_a_count()
    {
        static string Element(int i) => "{15150000-0000-0000-0000-" + i.ToString("000000000000") + "}";
        static string Port(int i) => "{15150000-0000-1111-0000-" + i.ToString("000000000000") + "}";
        const string hubPort = "{15150000-0000-2222-0000-000000000000}";

        var spokes = string.Join(Environment.NewLine, Enumerable.Range(0, 160).Select(i =>
            $"""
                <InternalElement Name="S{i:000}" ID="{Element(i)}"><ExternalInterface Name="P" ID="{Port(i)}" /></InternalElement>
                <InternalLink Name="L{i:000}" RefPartnerSideA="{hubPort}" RefPartnerSideB="{Port(i)}" />
                """));
        var store = Open("hub.aml", $"""
            <?xml version="1.0" encoding="utf-8"?>
            <CAEXFile SchemaVersion="3.0" FileName="hub.aml" xmlns="http://www.dke.de/CAEX">
              <InstanceHierarchy Name="Plant">
                <InternalElement Name="Hub" ID="{Element(999)}"><ExternalInterface Name="P" ID="{hubPort}" /></InternalElement>
            {spokes}
              </InstanceHierarchy>
            </CAEXFile>
            """);

        var result = AmlTools.GetNeighbors(store, "Plant/Hub", depth: 1);
        var text = Answer.Text(result);

        Assert.Equal(150, text.Split('\n').Count(l => l.Contains("hop 1 from Hub:", StringComparison.Ordinal)));
        Assert.Contains("... 10 more", text);
        Assert.Equal(160, Answer.Data(result).GetProperty("total").GetInt32());
    }
}
