using Xunit;

namespace AmlMcp.Tests;

/// <summary>get_tree, in both halves: shape, depth, class, mirrors and cross-hierarchy markers.</summary>
public sealed class TreeTests : IDisposable
{
    private readonly Workspace _ws = new();
    private readonly DocumentStore _rich = new();
    private readonly DocumentStore _tricky = new();

    public TreeTests()
    {
        AmlTools.OpenDocumentText(_rich, _ws.Write("rich.aml", Fixtures.Rich30));
        AmlTools.OpenDocumentText(_tricky, _ws.Write("tricky.aml", Fixtures.Tricky30));
    }

    public void Dispose() => _ws.Dispose();

    [Fact]
    public void Without_an_element_every_hierarchy_is_a_root_of_the_tree()
    {
        var result = AmlTools.GetTree(_tricky);
        var text = Answer.Text(result);
        var data = Answer.Data(result);

        Assert.Contains("Plant  [InstanceHierarchy]", text);
        Assert.Contains("Groups  [InstanceHierarchy]", text);
        Assert.Equal(new[] { "Plant", "Groups" }, Answer.Strings(data.GetProperty("nodes"), "name").ToArray());
        Assert.False(data.TryGetProperty("root", out _), "no element was asked for, so the tree has no single root");
        Assert.False(data.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void The_tree_names_class_mirror_and_cross_hierarchy_connections()
    {
        var result = AmlTools.GetTree(_tricky, "Groups");
        var text = Answer.Text(result);
        var node = Answer.Data(result).GetProperty("nodes")[0].GetProperty("children")[0];

        Assert.Contains("Motor1  [Motor]  (mirror of Plant/Motor1)  <2 cross-hierarchy connection(s)>", text);
        Assert.Equal("Motor1", node.GetProperty("name").GetString());
        Assert.Equal("Lib/Motor", node.GetProperty("class").GetString());
        Assert.Equal("Plant/Motor1", node.GetProperty("mirrorOf").GetString());
        Assert.Equal(2, node.GetProperty("crossHierarchyConnections").GetInt32());
    }

    [Fact]
    public void Nested_children_are_walked_to_the_requested_depth()
    {
        var deep = AmlTools.GetTree(_rich, "Shopfloor/Station1", depth: 3);
        var text = Answer.Text(deep);
        var station = Answer.Data(deep).GetProperty("nodes")[0];

        Assert.Contains("Station1  [Drill]", text);
        Assert.Contains("  Spindle", text);
        Assert.Contains("    Tool", text);
        Assert.Equal("Shopfloor/Station1/Spindle", station.GetProperty("children")[0].GetProperty("path").GetString());
        Assert.Equal("Shopfloor/Station1/Spindle/Tool",
            station.GetProperty("children")[0].GetProperty("children")[0].GetProperty("path").GetString());
        Assert.Equal(0, station.GetProperty("hiddenChildren").GetInt32());
    }

    [Fact]
    public void A_smaller_depth_hides_the_children_and_says_how_many()
    {
        var result = AmlTools.GetTree(_rich, "Shopfloor", depth: 1);
        var text = Answer.Text(result);
        var data = Answer.Data(result);

        Assert.Contains("... 8 child element(s)", text);
        Assert.DoesNotContain("Station1  [Drill]", text);
        Assert.Equal(1, data.GetProperty("depth").GetInt32());
        Assert.Equal("Shopfloor", data.GetProperty("root").GetString());
        Assert.Equal(8, data.GetProperty("nodes")[0].GetProperty("hiddenChildren").GetInt32());
        Assert.Empty(data.GetProperty("nodes")[0].GetProperty("children").EnumerateArray());
    }

    [Fact]
    public void The_depth_is_clamped_in_both_halves()
    {
        Assert.Equal(6, Answer.Data(AmlTools.GetTree(_rich, "Shopfloor", depth: 99)).GetProperty("depth").GetInt32());
        Assert.Equal(1, Answer.Data(AmlTools.GetTree(_rich, "Shopfloor", depth: -3)).GetProperty("depth").GetInt32());
        // A depth of -3 must behave like 1, not like "everything".
        Assert.Contains("... 8 child element(s)", Answer.Text(AmlTools.GetTree(_rich, "Shopfloor", depth: -3)));
    }

    [Fact]
    public void The_tree_of_an_unknown_element_says_so()
    {
        var ex = Assert.Throws<ModelContextProtocol.McpException>(() => AmlTools.GetTree(_rich, "NoSuchThing"));

        Assert.Contains("No element 'NoSuchThing'", ex.Message);
    }
}

/// <summary>get_neighbors, in both halves: hops, kinds, containment and the cross-hierarchy filter.</summary>
public sealed class NeighborTests : IDisposable
{
    private readonly Workspace _ws = new();
    private readonly DocumentStore _rich = new();
    private readonly DocumentStore _tricky = new();

    public NeighborTests()
    {
        AmlTools.OpenDocumentText(_rich, _ws.Write("rich.aml", Fixtures.Rich30));
        AmlTools.OpenDocumentText(_tricky, _ws.Write("tricky.aml", Fixtures.Tricky30));
    }

    public void Dispose() => _ws.Dispose();

    [Fact]
    public void The_neighbours_of_an_element_carry_kind_hop_and_partner()
    {
        var result = AmlTools.GetNeighbors(_tricky, "Plant/Motor1");
        var text = Answer.Text(result);
        var data = Answer.Data(result);

        Assert.Contains("Neighbours of Plant/Motor1", text);
        Assert.Contains("hop 1 from Motor1: <--refObj--  Groups/Note", text);
        Assert.Contains("hop 1 from Motor1: <--mirrored by--  Groups/Motor1", text);

        Assert.Equal("Plant/Motor1", data.GetProperty("from").GetProperty("path").GetString());
        Assert.Equal(2, data.GetProperty("total").GetInt32());
        Assert.Equal(new[] { "referenced-by", "mirrored-by" }, Answer.Strings(data.GetProperty("neighbors"), "kind").ToArray());
        Assert.All(data.GetProperty("neighbors").EnumerateArray(), n =>
        {
            Assert.Equal(1, n.GetProperty("hop").GetInt32());
            Assert.True(n.GetProperty("crossHierarchy").GetBoolean());
            Assert.Equal("Plant/Motor1", n.GetProperty("from").GetProperty("path").GetString());
        });
    }

    [Fact]
    public void Containment_is_followed_only_when_asked_for()
    {
        var without = AmlTools.GetNeighbors(_rich, "Shopfloor/Station1");
        var with = AmlTools.GetNeighbors(_rich, "Shopfloor/Station1", includeContainment: true);

        Assert.DoesNotContain("--contains-->", Answer.Text(without));
        Assert.DoesNotContain(Answer.Strings(Answer.Data(without).GetProperty("neighbors"), "kind"), k => k is "child" or "parent");

        Assert.Contains("--contains-->  Shopfloor/Station1/Spindle", Answer.Text(with));
        Assert.Contains("--contained in-->  Shopfloor", Answer.Text(with));
        var kinds = Answer.Strings(Answer.Data(with).GetProperty("neighbors"), "kind");
        Assert.Contains(kinds, k => k == "child");
        Assert.Contains(kinds, k => k == "parent");
    }

    [Fact]
    public void More_hops_report_the_hop_number_they_were_found_at()
    {
        var data = Answer.Data(AmlTools.GetNeighbors(_rich, "Shopfloor/Station1", depth: 2, includeContainment: true));
        var text = Answer.Text(AmlTools.GetNeighbors(_rich, "Shopfloor/Station1", depth: 2, includeContainment: true));

        Assert.Contains("hop 2 from Spindle: --contains-->  Shopfloor/Station1/Spindle/Tool", text);
        Assert.Contains(data.GetProperty("neighbors").EnumerateArray(),
            n => n.GetProperty("hop").GetInt32() == 2 && n.GetProperty("to").GetProperty("name").GetString() == "Tool");
        Assert.Equal(2, data.GetProperty("depth").GetInt32());
    }

    [Fact]
    public void Only_cross_hierarchy_connections_are_reported_when_asked_for()
    {
        var all = AmlTools.GetNeighbors(_rich, "Shopfloor/Station1", depth: 1);
        var cross = AmlTools.GetNeighbors(_rich, "Shopfloor/Station1", depth: 1, crossHierarchyOnly: true);

        Assert.Contains("--link OneToTwo", Answer.Text(all));
        Assert.Contains("--link OneToThree", Answer.Text(all));
        // Two partners, although three edges lead to them: a neighbour already
        // reached is not reported twice (the element card lists every edge).
        Assert.Equal(2, Answer.Data(all).GetProperty("total").GetInt32());

        Assert.Contains("none", Answer.Text(cross));
        Assert.Equal(0, Answer.Data(cross).GetProperty("total").GetInt32());

        // In a document that does connect two hierarchies the filter keeps them.
        var tricky = AmlTools.GetNeighbors(_tricky, "Plant/Motor1", crossHierarchyOnly: true);
        Assert.Equal(2, Answer.Data(tricky).GetProperty("total").GetInt32());
        Assert.Contains("other hierarchy: Groups", Answer.Text(tricky));
    }

    [Fact]
    public void The_hop_count_is_clamped_in_both_halves()
    {
        Assert.Equal(4, Answer.Data(AmlTools.GetNeighbors(_rich, "Shopfloor/Station1", depth: 40)).GetProperty("depth").GetInt32());
        Assert.Contains("depth 4", Answer.Text(AmlTools.GetNeighbors(_rich, "Shopfloor/Station1", depth: 40)));
        Assert.Contains("depth 1", Answer.Text(AmlTools.GetNeighbors(_rich, "Shopfloor/Station1", depth: 0)));
    }
}

/// <summary>list_classes, in both halves: content, filters and the empty answer.</summary>
public sealed class ClassListTests : IDisposable
{
    private readonly Workspace _ws = new();
    private readonly DocumentStore _store = new();

    public ClassListTests()
    {
        AmlTools.OpenDocumentText(_store, _ws.Write("rich.aml", Fixtures.Rich30));
    }

    public void Dispose() => _ws.Dispose();

    [Fact]
    public void Every_library_class_is_listed_with_kind_and_description()
    {
        var result = AmlTools.ListClasses(_store);
        var text = Answer.Text(result);
        var data = Answer.Data(result);

        Assert.Contains("4 class(es):", text);
        Assert.Contains("RichUnitLib/Drill  (SystemUnitClass)  A drilling unit.", text);
        Assert.Contains("RichInterfaceLib/Coupling  (InterfaceClass)", text);
        Assert.Contains("OMG_DD_AttributeTypeLib/DD_Bounds  (AttributeType)", text);

        Assert.Equal(4, data.GetProperty("total").GetInt32());
        Assert.Equal(4, data.GetProperty("shown").GetInt32());
        Assert.Contains(data.GetProperty("classes").EnumerateArray(), c =>
            c.GetProperty("key").GetString() == "RichUnitLib/Drill"
            && c.GetProperty("kind").GetString() == "SystemUnitClass"
            && c.GetProperty("library").GetString() == "RichUnitLib"
            && !c.GetProperty("external").GetBoolean()
            && c.GetProperty("description").GetString() == "A drilling unit.");
    }

    [Fact]
    public void The_kind_filter_reaches_both_halves()
    {
        var result = AmlTools.ListClasses(_store, kind: "RoleClass");

        Assert.Contains("1 class(es):", Answer.Text(result));
        Assert.Contains("RichRoleLib/Station  (RoleClass)", Answer.Text(result));
        Assert.DoesNotContain("Drill", Answer.Text(result));
        Assert.Equal(1, Answer.Data(result).GetProperty("total").GetInt32());
        Assert.Equal(new[] { "RichRoleLib/Station" },
            Answer.Strings(Answer.Data(result).GetProperty("classes"), "key").ToArray());
    }

    [Fact]
    public void The_library_filter_reaches_both_halves()
    {
        var result = AmlTools.ListClasses(_store, library: "Interface");

        Assert.Contains("RichInterfaceLib/Coupling", Answer.Text(result));
        Assert.DoesNotContain("RichUnitLib", Answer.Text(result));
        Assert.Equal(new[] { "RichInterfaceLib/Coupling" },
            Answer.Strings(Answer.Data(result).GetProperty("classes"), "key").ToArray());
    }

    [Fact]
    public void The_text_filter_also_searches_the_description_in_both_halves()
    {
        var result = AmlTools.ListClasses(_store, query: "drilling unit");

        Assert.Contains("RichUnitLib/Drill", Answer.Text(result));
        Assert.DoesNotContain("Coupling", Answer.Text(result));
        Assert.Equal(1, Answer.Data(result).GetProperty("total").GetInt32());
    }

    [Fact]
    public void A_filter_that_matches_nothing_says_so_in_both_halves()
    {
        var result = AmlTools.ListClasses(_store, query: "PetriNet");

        Assert.Equal("No matching classes.", Answer.Text(result));
        Assert.Equal(0, Answer.Data(result).GetProperty("total").GetInt32());
        Assert.Empty(Answer.Data(result).GetProperty("classes").EnumerateArray());
    }
}
