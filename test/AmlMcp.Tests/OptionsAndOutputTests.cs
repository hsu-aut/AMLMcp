using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Xunit;

namespace AmlMcp.Tests;

public sealed class RootRestrictionTests : IDisposable
{
    private readonly Workspace _inside = new();
    private readonly Workspace _outside = new();

    public void Dispose()
    {
        _inside.Dispose();
        _outside.Dispose();
    }

    [Fact]
    public void Without_roots_any_readable_file_is_allowed()
    {
        var store = new DocumentStore();
        var path = _outside.Write("plant.aml", Fixtures.Caex215);

        Assert.Contains("CAEX 2.15", AmlTools.OpenDocumentText(store, path));
    }

    [Fact]
    public void A_file_outside_the_roots_is_refused()
    {
        var store = new DocumentStore(ServerOptions.Parse(new[] { "--root", _inside.Dir }));
        var path = _outside.Write("plant.aml", Fixtures.Caex215);

        var ex = Assert.Throws<McpException>(() => AmlTools.OpenDocumentText(store, path));

        Assert.Contains("outside the directories this server may read", ex.Message);
        Assert.Contains(_inside.Dir, ex.Message);
    }

    [Fact]
    public void A_file_inside_the_roots_is_allowed()
    {
        var store = new DocumentStore(ServerOptions.Parse(new[] { "--root", _inside.Dir }));
        var path = _inside.Write("plant.aml", Fixtures.Caex215);

        Assert.Contains("CAEX 2.15", AmlTools.OpenDocumentText(store, path));
    }

    [Fact]
    public void A_sibling_directory_with_the_same_prefix_is_not_inside()
    {
        var options = ServerOptions.Parse(new[] { "--root", Path.Combine(_inside.Dir, "models") });

        Assert.False(options.Allows(Path.Combine(_inside.Dir, "models-secret", "plant.aml")));
        Assert.True(options.Allows(Path.Combine(_inside.Dir, "models", "sub", "plant.aml")));
    }
}

public sealed class ConventionTests : IDisposable
{
    private readonly Workspace _ws = new();

    // No Diagram Definition types here: geometry and reference are recognisable
    // by name only, which is exactly what strict conventions switch off.
    private const string NameConventions = """
        <?xml version="1.0" encoding="utf-8"?>
        <CAEXFile SchemaVersion="3.0" FileName="names.aml" xmlns="http://www.dke.de/CAEX">
          <InstanceHierarchy Name="Plant" ID="{44444444-0000-0000-0000-0000000000f1}">
            <InternalElement Name="Pump" ID="{44444444-0000-0000-0000-000000000001}">
              <Attribute Name="ViewInformation" AttributeDataType="xs:string">
                <Attribute Name="x" AttributeDataType="xs:double"><Value>10</Value></Attribute>
              </Attribute>
            </InternalElement>
            <InternalElement Name="Tank" ID="{44444444-0000-0000-0000-000000000002}">
              <Attribute Name="refNeighbour" AttributeDataType="xs:string"><Value>44444444-0000-0000-0000-000000000001</Value></Attribute>
            </InternalElement>
          </InstanceHierarchy>
        </CAEXFile>
        """;

    public void Dispose() => _ws.Dispose();

    [Fact]
    public void By_default_the_name_conventions_of_older_libraries_are_applied()
    {
        var store = new DocumentStore();
        AmlTools.OpenDocumentText(store, _ws.Write("names.aml", NameConventions));

        Assert.Contains("layout attribute group(s) hidden", AmlTools.ElementCard(store, "Plant/Pump"));
        Assert.Contains("--refNeighbour-->  Plant/Pump", AmlTools.ElementCard(store, "Plant/Tank"));
    }

    [Fact]
    public void With_strict_conventions_only_typed_information_counts()
    {
        var store = new DocumentStore(ServerOptions.Parse(new[] { "--strict-conventions" }));
        AmlTools.OpenDocumentText(store, _ws.Write("names.aml", NameConventions));

        var pump = AmlTools.ElementCard(store, "Plant/Pump");
        var tank = AmlTools.ElementCard(store, "Plant/Tank");

        Assert.Contains("ViewInformation/x = 10", pump);
        Assert.DoesNotContain("layout attribute group(s) hidden", pump);
        Assert.Contains("refNeighbour = 44444444", tank);
        Assert.DoesNotContain("--refNeighbour-->", tank);
    }

    // Geometry that says what it is, by the Diagram Definition types of the Object
    // Management Group, is layout in both modes: the strict mode drops the name
    // conventions, not the typed information.
    [Fact]
    public void Diagram_definition_types_are_recognised_in_both_modes()
    {
        var path = _ws.Write("rich.aml", Fixtures.Rich30);
        foreach (var options in new[] { ServerOptions.Default, ServerOptions.Parse(new[] { "--strict-conventions" }) })
        {
            var store = new DocumentStore(options);
            AmlTools.OpenDocumentText(store, path);

            var card = AmlTools.ElementCard(store, "Shopfloor/Station1");
            Assert.DoesNotContain("Bounds/width", card);
            Assert.Contains("(1 layout attribute group(s) hidden, use includeLayout=true)", card);
            // Engineering data next to it is not swept away with the geometry.
            Assert.Contains("Feed = 4.5 mm/s (default)", card);
            Assert.Contains("Bounds/width = 120", AmlTools.ElementCard(store, "Shopfloor/Station1", includeLayout: true));

            var model = store.Get(null);
            var bounds = model.ResolveElement("Shopfloor/Station1").Elements(AmlModel.C + "Attribute")
                .Single(a => AmlModel.NameOf(a) == "Bounds");
            Assert.True(model.IsPresentationAttribute(bounds));
            Assert.True(model.IsInsidePresentation(bounds.Elements(AmlModel.C + "Attribute").Single()));
        }
    }
}

public sealed class StructuredOutputTests : IDisposable
{
    private readonly Workspace _ws = new();
    private readonly DocumentStore _store = new();

    public StructuredOutputTests()
    {
        AmlTools.OpenDocumentText(_store, _ws.Write("tricky.aml", Fixtures.Tricky30));
    }

    public void Dispose() => _ws.Dispose();

    private static JsonElement Structured(ModelContextProtocol.Protocol.CallToolResult result)
    {
        Assert.NotNull(result.StructuredContent);
        return result.StructuredContent!.Value;
    }

    [Fact]
    public void An_element_card_carries_machine_readable_data_next_to_the_text()
    {
        var result = AmlTools.GetElement(_store, "Plant/Motor1");

        var text = Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(result.Content[0]).Text;
        var data = Structured(result);

        Assert.Contains("path: Plant/Motor1", text);
        Assert.Equal("Plant/Motor1", data.GetProperty("path").GetString());
        Assert.Equal("Lib/Motor", data.GetProperty("class").GetString());
        Assert.True(data.GetProperty("classResolved").GetBoolean());
        Assert.Contains(data.GetProperty("attributes").EnumerateArray(),
            a => a.GetProperty("path").GetString() == "RatedPower" && a.GetProperty("inheritedFrom").GetString() == "Lib/Motor");
    }

    [Fact]
    public void A_path_answer_carries_its_steps()
    {
        var data = Structured(AmlTools.FindPath(_store, "Groups/Motor1", "Plant/Motor1", includeContainment: false));

        Assert.True(data.GetProperty("found").GetBoolean());
        Assert.Equal(1, data.GetProperty("steps").GetInt32());
        Assert.Equal("mirror-of", data.GetProperty("path")[0].GetProperty("kind").GetString());
    }

    [Fact]
    public void A_search_answer_carries_its_hits()
    {
        var data = Structured(AmlTools.FindElements(_store, classContains: "Drive"));

        // Both motors and the mirror of the first one, which stands for its master
        // and therefore carries the same class.
        Assert.Equal(3, data.GetProperty("total").GetInt32());
        Assert.Contains(data.GetProperty("elements").EnumerateArray(),
            e => e.GetProperty("path").GetString() == "Plant/Motor2");
        Assert.Contains(data.GetProperty("elements").EnumerateArray(),
            e => e.GetProperty("path").GetString() == "Groups/Motor1" && e.GetProperty("isMirror").GetBoolean());
    }

    [Fact]
    public void A_check_answer_carries_typed_problems()
    {
        var data = Structured(AmlTools.CheckReferences(_store));

        Assert.False(data.GetProperty("ok").GetBoolean());
        var kinds = data.GetProperty("problems").EnumerateArray()
            .Select(p => p.GetProperty("kind").GetString()).ToList();
        Assert.Contains("mirrorWithoutMaster", kinds);
        Assert.Contains("danglingReference", kinds);
    }

    [Fact]
    public void A_document_overview_carries_the_hierarchy_connections()
    {
        var data = Structured(AmlTools.OpenAmlDocument(_store, Path.Combine(_ws.Dir, "tricky.aml")));

        Assert.Equal("3.0", data.GetProperty("schemaVersion").GetString());
        Assert.Equal(2, data.GetProperty("hierarchies").GetArrayLength());
        Assert.Contains(data.GetProperty("hierarchyConnections").EnumerateArray(),
            c => c.GetProperty("kind").GetString() == "mirror");
    }
}

// Each tool answers twice, as text for the model and as data for programs. Both halves
// must be built from the same arguments; a wrapper that quietly passes its own defaults
// to one half would make a filtered search read like an unfiltered one.
public sealed class TextAndDataAgreeTests : IDisposable
{
    private readonly Workspace _ws = new();
    private readonly DocumentStore _store = new();

    public TextAndDataAgreeTests()
    {
        AmlTools.OpenDocumentText(_store, _ws.Write("tricky.aml", Fixtures.Tricky30));
    }

    public void Dispose() => _ws.Dispose();

    private static string TextOf(ModelContextProtocol.Protocol.CallToolResult result) => Answer.Text(result);

    [Fact]
    public void A_filtered_search_filters_both_halves()
    {
        var result = AmlTools.FindElements(_store, hierarchy: "Groups");
        var text = TextOf(result);
        var data = Answer.Data(result);

        Assert.Contains("Groups/Note", text);
        Assert.DoesNotContain("Plant/Motor2", text);
        Assert.Contains("3 match(es):", text);

        Assert.Equal(3, data.GetProperty("total").GetInt32());
        Assert.Equal(new[] { "Groups/Motor1", "Groups/Ghost", "Groups/Note" },
            Answer.Strings(data.GetProperty("elements"), "path").ToArray());
        Assert.All(data.GetProperty("elements").EnumerateArray(),
            e => Assert.Equal("Groups", e.GetProperty("hierarchy").GetString()));
    }

    [Fact]
    public void A_search_limit_reaches_both_halves()
    {
        var result = AmlTools.FindElements(_store, limit: 1);
        var data = Answer.Data(result);

        Assert.Contains("5 match(es), showing 1:", TextOf(result));
        Assert.Equal(5, data.GetProperty("total").GetInt32());
        Assert.Equal(1, data.GetProperty("shown").GetInt32());
        Assert.Single(data.GetProperty("elements").EnumerateArray());

        // Out-of-range limits are clamped the same way on both sides.
        var clamped = AmlTools.FindElements(_store, limit: 0);
        Assert.Contains("5 match(es), showing 1:", TextOf(clamped));
        Assert.Equal(1, Answer.Data(clamped).GetProperty("shown").GetInt32());
    }

    [Fact]
    public void Requested_layout_attributes_reach_both_halves()
    {
        var hidden = AmlTools.GetElement(_store, "Plant/Motor1");
        var shown = AmlTools.GetElement(_store, "Plant/Motor1", includeLayout: true);

        Assert.DoesNotContain("ViewInformation/x", TextOf(hidden));
        Assert.Contains("layout attribute group(s) hidden", TextOf(hidden));
        Assert.Equal(1, Answer.Data(hidden).GetProperty("hiddenPresentationAttributes").GetInt32());
        Assert.DoesNotContain(Answer.Strings(Answer.Data(hidden).GetProperty("attributes"), "path"),
            p => p!.StartsWith("ViewInformation", StringComparison.Ordinal));

        Assert.Contains("ViewInformation/x = 10", TextOf(shown));
        Assert.DoesNotContain("layout attribute group(s) hidden", TextOf(shown));
        Assert.Equal(0, Answer.Data(shown).GetProperty("hiddenPresentationAttributes").GetInt32());
        Assert.Contains(Answer.Strings(Answer.Data(shown).GetProperty("attributes"), "path"), p => p == "ViewInformation/x");
    }

    [Fact]
    public void Containment_steps_are_allowed_or_forbidden_in_both_halves()
    {
        var withContainment = AmlTools.FindPath(_store, "Plant/Motor1", "Plant/Motor2");
        var withoutContainment = AmlTools.FindPath(_store, "Plant/Motor1", "Plant/Motor2", includeContainment: false);

        Assert.Contains("Path with 2 step(s)", TextOf(withContainment));
        Assert.True(Answer.Data(withContainment).GetProperty("found").GetBoolean());
        Assert.Equal(2, Answer.Data(withContainment).GetProperty("steps").GetInt32());
        Assert.Equal(new[] { "parent", "child" },
            Answer.Strings(Answer.Data(withContainment).GetProperty("path"), "kind").ToArray());

        Assert.Contains("No connection within", TextOf(withoutContainment));
        Assert.Contains("Try includeContainment=true", TextOf(withoutContainment));
        Assert.False(Answer.Data(withoutContainment).GetProperty("found").GetBoolean());
        Assert.Empty(Answer.Data(withoutContainment).GetProperty("path").EnumerateArray());
    }

    [Fact]
    public void The_step_budget_reaches_both_halves()
    {
        var tooShort = AmlTools.FindPath(_store, "Plant/Motor1", "Plant/Motor2", maxSteps: 1);
        var longEnough = AmlTools.FindPath(_store, "Plant/Motor1", "Plant/Motor2", maxSteps: 2);

        Assert.Contains("No connection within 1 steps", TextOf(tooShort));
        Assert.False(Answer.Data(tooShort).GetProperty("found").GetBoolean());
        Assert.Equal("no connection within 1 steps", Answer.Data(tooShort).GetProperty("reason").GetString());

        Assert.Contains("Path with 2 step(s)", TextOf(longEnough));
        Assert.Equal(2, Answer.Data(longEnough).GetProperty("steps").GetInt32());

        // An absurd budget is clamped, not obeyed.
        Assert.Contains("No connection within 20 steps",
            TextOf(AmlTools.FindPath(_store, "Plant/Motor1", "Plant/Motor2", includeContainment: false, maxSteps: 900)));
    }

    [Fact]
    public void A_class_card_answers_about_the_class_that_was_asked_for_in_both_halves()
    {
        var result = AmlTools.GetClass(_store, "Lib/Drive");
        var data = Answer.Data(result);

        Assert.Contains("Lib/Drive", TextOf(result));
        Assert.DoesNotContain("RatedPower", TextOf(result));

        Assert.Equal(1, data.GetProperty("total").GetInt32());
        Assert.Equal("Lib/Drive", data.GetProperty("classes")[0].GetProperty("key").GetString());
        Assert.DoesNotContain(Answer.Strings(data.GetProperty("classes")[0].GetProperty("attributes"), "path"),
            p => p == "RatedPower");
    }

    [Fact]
    public void The_document_parameter_reaches_both_halves()
    {
        var tricky = Path.Combine(_ws.Dir, "tricky.aml");
        var legacy = _ws.Write("plant215.aml", Fixtures.Caex215);
        AmlTools.OpenDocumentText(_store, legacy);   // the most recent document is now the 2.15 one

        var element = AmlTools.GetElement(_store, "Plant/Motor1", document: tricky);
        Assert.Contains("path: Plant/Motor1", TextOf(element));
        Assert.Equal("Lib/Motor", Answer.Data(element).GetProperty("class").GetString());

        var legacyCheck = AmlTools.CheckReferences(_store, document: legacy);
        Assert.Contains("Reference check for plant215.aml", TextOf(legacyCheck));
        Assert.Equal("plant215.aml", Answer.Data(legacyCheck).GetProperty("document").GetString());

        var trickyCheck = AmlTools.CheckReferences(_store, document: tricky);
        Assert.Contains("Reference check for tricky.aml", TextOf(trickyCheck));
        Assert.Equal("tricky.aml", Answer.Data(trickyCheck).GetProperty("document").GetString());

        var hits = AmlTools.FindElements(_store, query: "Pump", document: legacy);
        Assert.Contains("Plant/Pump", TextOf(hits));
        Assert.Equal(1, Answer.Data(hits).GetProperty("total").GetInt32());

        var tree = AmlTools.GetTree(_store, document: legacy);
        Assert.Contains("Plant  [InstanceHierarchy]", TextOf(tree));
        Assert.Single(Answer.Data(tree).GetProperty("nodes").EnumerateArray());

        var classes = AmlTools.ListClasses(_store, document: tricky);
        Assert.Contains("Lib/Motor", TextOf(classes));
        Assert.Contains(Answer.Strings(Answer.Data(classes).GetProperty("classes"), "key"), k => k == "Lib/Motor");
    }

    // Change 10: both halves come from one model. Asking the store twice can answer
    // about the document a concurrent call opened in between.
    [Fact]
    public void Both_halves_describe_the_same_document_while_other_documents_are_opened()
    {
        var tricky = Path.Combine(_ws.Dir, "tricky.aml");
        var legacy = _ws.Write("plant215.aml", Fixtures.Caex215);
        var store = new DocumentStore();
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();

        Parallel.For(0, 200, i =>
        {
            var path = i % 2 == 0 ? tricky : legacy;
            var result = AmlTools.OpenAmlDocument(store, path);
            var text = Answer.Text(result);
            var data = Answer.Data(result);
            var named = data.GetProperty("fileName").GetString();
            var hierarchies = data.GetProperty("hierarchies").GetArrayLength();

            if (named != Path.GetFileName(path)) failures.Add($"data names {named}, asked for {path}");
            if (!text.Contains($"Document: {named}", StringComparison.Ordinal))
                failures.Add($"text and data disagree: data says {named}, text says {text.Split('\n')[0]}");
            if (!text.Contains($"Instance hierarchies ({hierarchies})", StringComparison.Ordinal))
                failures.Add($"text and data disagree on the hierarchy count ({hierarchies})");
        });

        Assert.Empty(failures);
    }
}

public sealed class ResourceAndPromptTests : IDisposable
{
    private readonly Workspace _ws = new();
    private readonly DocumentStore _store = new();

    public void Dispose() => _ws.Dispose();

    [Fact]
    public void The_document_resource_reports_what_is_open_and_which_directories_are_readable()
    {
        Assert.Contains("No document is open", AmlResources.OpenDocuments(_store));

        var path = _ws.Write("tricky.aml", Fixtures.Tricky30);
        AmlTools.OpenDocumentText(_store, path);

        var text = AmlResources.OpenDocuments(_store);
        Assert.Contains(path, text);
        Assert.Contains("no restriction", text);
    }

    [Fact]
    public void The_library_resource_lists_the_classes()
    {
        AmlTools.OpenDocumentText(_store, _ws.Write("tricky.aml", Fixtures.Tricky30));

        var text = AmlResources.ClassLibraries(_store);

        Assert.Contains("Lib  (embedded)", text);
        Assert.Contains("Lib/Motor  (SystemUnitClass)", text);
    }

    // A prompt is an instruction to use this server. It must name the subject it was
    // given and only tools this server actually offers: a renamed tool must not leave
    // a prompt pointing at nothing.
    [Fact]
    public void Every_prompt_names_its_subject_and_only_tools_that_exist()
    {
        var toolNames = typeof(AmlTools).GetMethods()
            .Select(m => m.GetCustomAttributes(typeof(McpServerToolAttribute), false).FirstOrDefault())
            .OfType<McpServerToolAttribute>()
            .Select(a => a.Name)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("open_aml_document", toolNames);

        var cases = new[]
        {
            (Subject: "plant.aml", Text: AmlPrompts.ExplainDocument("plant.aml"), Tools: new[] { "get_tree", "get_element" }),
            (Subject: "Check_Quality", Text: AmlPrompts.TraceToPlant("Check_Quality"), Tools: new[] { "get_element", "get_neighbors", "find_path" }),
            (Subject: "plant.amlx", Text: AmlPrompts.ReviewDocument("plant.amlx"), Tools: new[] { "check_references" }),
        };

        foreach (var (subject, text, tools) in cases)
        {
            Assert.Contains(subject, text);
            foreach (var tool in tools)
            {
                Assert.Contains(tool, toolNames);
                Assert.Contains(tool, text);
            }
        }
    }
}
