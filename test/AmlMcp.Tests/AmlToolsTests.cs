using ModelContextProtocol;
using Xunit;

namespace AmlMcp.Tests;

public sealed class Caex215Tests : IDisposable
{
    private readonly Workspace _ws = new();
    private readonly DocumentStore _store = new();
    private readonly string _path;

    public Caex215Tests()
    {
        _path = _ws.Write("plant215.aml", Fixtures.Caex215);
    }

    public void Dispose() => _ws.Dispose();

    [Fact]
    public void Opens_a_CAEX_2_15_document()
    {
        var overview = AmlTools.OpenDocumentText(_store, _path);

        Assert.Contains("CAEX 2.15", overview);
        Assert.Contains("InternalLinks: 2 total, 0 across hierarchies, 0 unresolved", overview);
    }

    [Fact]
    public void Resolves_the_legacy_link_form_id_colon_interface()
    {
        AmlTools.OpenDocumentText(_store, _path);

        var pump = AmlTools.ElementCard(_store, "Plant/Pump");

        Assert.Contains("--link Pipe", pump);
        Assert.Contains("Plant/Tank", pump);
    }

    [Fact]
    public void Resolves_the_legacy_link_form_path_colon_interface()
    {
        AmlTools.OpenDocumentText(_store, _path);

        var valve = AmlTools.ElementCard(_store, "Plant/Valve");

        Assert.Contains("--link DrainPipe", valve);
        Assert.Contains("Plant/Tank", valve);
    }

    // "No problems" is only worth something when the check really looked at the
    // document: the counted line says what it went through.
    [Fact]
    public void Reports_no_problems_for_a_consistent_2_15_document()
    {
        AmlTools.OpenDocumentText(_store, _path);

        var check = AmlTools.CheckText(_store);
        var data = Answer.Data(AmlTools.CheckReferences(_store));

        Assert.Contains("no problems found", check);
        Assert.Contains("checked: 1 library classes, 2 links, 0 references, 7 IDs", check);

        Assert.True(data.GetProperty("ok").GetBoolean());
        Assert.Empty(data.GetProperty("problems").EnumerateArray());
        Assert.Empty(data.GetProperty("notes").EnumerateArray());
        Assert.Equal(2, data.GetProperty("links").GetInt32());
        Assert.Equal(7, data.GetProperty("ids").GetInt32());
        Assert.Equal(1, data.GetProperty("classes").GetInt32());

        // Both links really did find their partners; an empty model would also have no problems.
        Assert.All(_store.Get(null).Links, l => Assert.True(l.Resolved));
        Assert.Equal(2, _store.Get(null).Links.Count);
    }
}

public sealed class MirrorTests : IDisposable
{
    private readonly Workspace _ws = new();
    private readonly DocumentStore _store = new();

    public MirrorTests()
    {
        AmlTools.OpenDocumentText(_store, _ws.Write("tricky.aml", Fixtures.Tricky30));
    }

    public void Dispose() => _ws.Dispose();

    [Fact]
    public void A_mirror_object_is_not_an_unresolved_class_path()
    {
        var check = AmlTools.CheckText(_store);

        Assert.DoesNotContain("unresolved class path", check);
    }

    [Fact]
    public void The_mirror_card_names_the_master_and_its_class()
    {
        var mirror = AmlTools.ElementCard(_store, "Groups/Motor1");

        Assert.Contains("mirror of: Plant/Motor1", mirror);
        Assert.Contains("class: Lib/Motor", mirror);
    }

    [Fact]
    public void The_master_knows_it_is_mirrored()
    {
        var master = AmlTools.ElementCard(_store, "Plant/Motor1");

        Assert.Contains("<--mirrored by--  Groups/Motor1", master);
    }

    [Fact]
    public void A_mirrored_interface_shows_its_master_interface()
    {
        var mirror = AmlTools.ElementCard(_store, "Groups/Motor1");

        Assert.Contains("Power  [mirror of Plant/Motor1/Power]", mirror);
    }

    [Fact]
    public void A_mirror_without_master_is_reported()
    {
        var check = AmlTools.CheckText(_store);

        Assert.Contains("mirror without master: Groups/Ghost", check);
    }

    [Fact]
    public void Find_path_follows_the_mirror_relation()
    {
        var path = AmlTools.FindPathText(_store, "Groups/Motor1", "Plant/Motor1", includeContainment: false);

        Assert.Contains("Path with 1 step(s)", path);
        Assert.Contains("--mirror of-->", path);
    }
}

public sealed class ReferenceTests : IDisposable
{
    private readonly Workspace _ws = new();
    private readonly DocumentStore _store = new();

    public ReferenceTests()
    {
        AmlTools.OpenDocumentText(_store, _ws.Write("tricky.aml", Fixtures.Tricky30));
    }

    public void Dispose() => _ws.Dispose();

    [Fact]
    public void An_attribute_named_like_a_reference_with_a_plain_value_is_not_a_reference()
    {
        var model = _store.Get(null);

        Assert.DoesNotContain(model.Refs, r => r.AttributePath == "refTemperature");
        Assert.DoesNotContain("refTemperature", AmlTools.CheckText(_store));
    }

    [Fact]
    public void An_attribute_named_like_a_reference_with_an_unknown_id_is_dangling()
    {
        var check = AmlTools.CheckText(_store);

        Assert.Contains("dangling reference: Plant/Motor1.refCustomer", check);
    }

    [Fact]
    public void A_typed_reference_connects_hierarchies_in_both_directions()
    {
        var note = AmlTools.ElementCard(_store, "Groups/Note");
        var motor = AmlTools.ElementCard(_store, "Plant/Motor1");

        Assert.Contains("--refObj-->  Plant/Motor1", note);
        Assert.Contains("[other hierarchy: Plant]", note);
        Assert.Contains("<--refObj--  Groups/Note", motor);
    }

    [Fact]
    public void The_check_counts_exactly_the_two_real_problems()
    {
        var check = AmlTools.CheckText(_store);

        Assert.Contains("2 problem(s)", check);
    }
}

public sealed class InheritanceTests : IDisposable
{
    private readonly Workspace _ws = new();
    private readonly DocumentStore _store = new();

    public InheritanceTests()
    {
        AmlTools.OpenDocumentText(_store, _ws.Write("tricky.aml", Fixtures.Tricky30));
    }

    public void Dispose() => _ws.Dispose();

    [Fact]
    public void An_instance_shows_attributes_inherited_over_two_class_levels()
    {
        var motor = AmlTools.ElementCard(_store, "Plant/Motor1");

        Assert.Contains("RatedPower = 7.5 kW  (from Lib/Motor)", motor);
        Assert.Contains("Manufacturer = ACME  (from Lib/Drive)", motor);
    }

    [Fact]
    public void An_own_attribute_hides_the_inherited_default()
    {
        var motor = AmlTools.ElementCard(_store, "Plant/Motor2");

        Assert.Contains("RatedPower = 11 kW", motor);
        Assert.DoesNotContain("RatedPower = 7.5", motor);
        Assert.Contains("Manufacturer = ACME  (from Lib/Drive)", motor);
    }

    [Fact]
    public void A_class_filter_finds_instances_of_subclasses()
    {
        var hits = AmlTools.FindElementsText(_store, classContains: "Drive");

        Assert.Contains("Plant/Motor1", hits);
        Assert.Contains("Plant/Motor2", hits);
    }

    [Fact]
    public void The_class_card_shows_inherited_attributes()
    {
        var card = AmlTools.ClassCard(_store, "Lib/Motor");

        Assert.Contains("inherits from: Lib/Drive", card);
        Assert.Contains("Manufacturer = ACME  (from Lib/Drive)", card);
    }
}

public sealed class LayoutTests : IDisposable
{
    private readonly Workspace _ws = new();
    private readonly DocumentStore _store = new();

    public LayoutTests()
    {
        AmlTools.OpenDocumentText(_store, _ws.Write("tricky.aml", Fixtures.Tricky30));
    }

    public void Dispose() => _ws.Dispose();

    [Fact]
    public void Layout_attributes_are_hidden_by_default()
    {
        var motor = AmlTools.ElementCard(_store, "Plant/Motor1");

        Assert.DoesNotContain("ViewInformation/x", motor);
        Assert.Contains("layout attribute group(s) hidden", motor);
    }

    [Fact]
    public void Layout_attributes_are_shown_on_request()
    {
        var motor = AmlTools.ElementCard(_store, "Plant/Motor1", includeLayout: true);

        Assert.Contains("ViewInformation/x = 10", motor);
    }
}

public sealed class ExternalLibraryTests : IDisposable
{
    private readonly Workspace _ws = new();
    private readonly DocumentStore _store = new();

    public void Dispose() => _ws.Dispose();

    [Fact]
    public void A_class_from_an_external_library_resolves_with_its_alias()
    {
        _ws.Write("MotorLib.aml", Fixtures.ExternalLibrary);
        AmlTools.OpenDocumentText(_store, _ws.Write("user.aml", Fixtures.UsingLibrary("MotorLib.aml")));

        var fan = AmlTools.ElementCard(_store, "Plant/Fan");

        Assert.Contains("class: Motors@MotorLib/Motor", fan);
        Assert.DoesNotContain("NOT resolved", fan);
        Assert.Contains("class meaning: A motor from an external library.", fan);
        Assert.Contains("RatedPower = 3 kW  (from Motors@MotorLib/Motor)", fan);
        Assert.Contains("no problems found", AmlTools.CheckText(_store));
    }

    [Fact]
    public void A_missing_library_is_reported_once_with_its_consequence()
    {
        AmlTools.OpenDocumentText(_store, _ws.Write("user.aml", Fixtures.UsingLibrary("DoesNotExist.aml")));

        var check = AmlTools.CheckText(_store);

        Assert.Contains("external library not loaded: Motors -> DoesNotExist.aml", check);
        Assert.Contains("consequence: 1 class path(s)", check);
        Assert.DoesNotContain("unresolved class path:", check);
        Assert.Contains("1 problem(s)", check);
    }
}

public sealed class RobustnessTests : IDisposable
{
    private readonly Workspace _ws = new();
    private readonly DocumentStore _store = new();

    public void Dispose() => _ws.Dispose();

    [Fact]
    public void A_non_CAEX_document_is_rejected_with_a_clear_message()
    {
        var path = _ws.Write("other.xml", "<?xml version=\"1.0\"?><Foo />");

        var ex = Assert.Throws<McpException>(() => AmlTools.OpenDocumentText(_store, path));

        Assert.Contains("root element is <Foo>", ex.Message);
    }

    [Fact]
    public void A_missing_file_is_rejected_with_a_clear_message()
    {
        var ex = Assert.Throws<McpException>(() => AmlTools.OpenDocumentText(_store, Path.Combine(_ws.Dir, "nope.aml")));

        Assert.Contains("File not found", ex.Message);
    }

    [Fact]
    public void An_ambiguous_name_lists_the_candidates()
    {
        AmlTools.OpenDocumentText(_store, _ws.Write("tricky.aml", Fixtures.Tricky30));

        var ex = Assert.Throws<McpException>(() => AmlTools.ElementCard(_store, "Motor1"));

        Assert.Contains("ambiguous", ex.Message);
        Assert.Contains("Plant/Motor1", ex.Message);
        Assert.Contains("Groups/Motor1", ex.Message);
    }

    [Fact]
    public void A_changed_file_is_reloaded_automatically()
    {
        var path = _ws.Write("plant215.aml", Fixtures.Caex215);
        AmlTools.OpenDocumentText(_store, path);
        Assert.Contains("No matching elements", AmlTools.FindElementsText(_store, query: "Compressor"));

        File.WriteAllText(path, Fixtures.Caex215.Replace(
            "<InternalElement Name=\"Valve\"",
            "<InternalElement Name=\"Compressor\" ID=\"{11111111-0000-0000-0000-000000000099}\" /><InternalElement Name=\"Valve\""));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        Assert.Contains("Plant/Compressor", AmlTools.FindElementsText(_store, query: "Compressor"));
    }

    [Fact]
    public void Elements_can_be_addressed_by_id_with_or_without_braces()
    {
        AmlTools.OpenDocumentText(_store, _ws.Write("tricky.aml", Fixtures.Tricky30));

        Assert.Contains("path: Plant/Motor1", AmlTools.ElementCard(_store, Fixtures.MotorId));
        Assert.Contains("path: Plant/Motor1", AmlTools.ElementCard(_store, "{" + Fixtures.MotorId + "}"));
    }
}
