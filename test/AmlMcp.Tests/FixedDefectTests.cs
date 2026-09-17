using ModelContextProtocol;
using Xunit;

namespace AmlMcp.Tests;

/// <summary>Change 1: --root also fences in the libraries an ExternalReference points at.</summary>
public sealed class ExternalReferenceRootTests : IDisposable
{
    private readonly Workspace _inside = new();
    private readonly Workspace _outside = new();

    public void Dispose()
    {
        _inside.Dispose();
        _outside.Dispose();
    }

    private string WriteDocumentPointingOutside()
    {
        _outside.Write("MotorLib.aml", Fixtures.ExternalLibrary);
        var target = Path.Combine(_outside.Dir, "MotorLib.aml").Replace('\\', '/');
        return _inside.Write("user.aml", Fixtures.UsingLibrary(target));
    }

    [Fact]
    public void A_library_outside_the_roots_is_not_loaded()
    {
        var store = new DocumentStore(ServerOptions.Parse(new[] { "--root", _inside.Dir }));
        var overview = AmlTools.OpenDocumentText(store, WriteDocumentPointingOutside());

        Assert.Contains("NOT loaded: outside the directories this server may read", overview);
        Assert.Contains(_inside.Dir, overview);

        var model = store.Get(null);
        var reference = Assert.Single(model.ExternalRefs);
        Assert.False(reference.Loaded);
        Assert.Null(reference.ResolvedFile);
        Assert.Contains("outside the directories this server may read", reference.Problem);
        Assert.DoesNotContain(model.Classes.Keys, k => k.Contains("MotorLib/Motor", StringComparison.Ordinal));
    }

    [Fact]
    public void The_check_says_the_library_was_not_loaded_and_why()
    {
        var store = new DocumentStore(ServerOptions.Parse(new[] { "--root", _inside.Dir }));
        AmlTools.OpenDocumentText(store, WriteDocumentPointingOutside());

        var check = AmlTools.CheckText(store);
        var data = Answer.Data(AmlTools.CheckReferences(store));

        Assert.Contains("external library not loaded: Motors ->", check);
        Assert.Contains("outside the directories this server may read", check);
        Assert.Contains("consequence: 1 class path(s)", check);
        Assert.Contains(data.GetProperty("problems").EnumerateArray(),
            p => p.GetProperty("kind").GetString() == "externalLibraryNotLoaded"
                 && p.GetProperty("message").GetString()!.Contains("outside the directories this server may read"));

        // The class the document builds on is gone with the library, and the card says so.
        var fan = AmlTools.ElementCard(store, "Plant/Fan");
        Assert.Contains("class: Motors@MotorLib/Motor  (NOT resolved)", fan);
        Assert.DoesNotContain("A motor from an external library", fan);
    }

    [Fact]
    public void Without_a_root_restriction_the_same_document_loads_the_library()
    {
        var store = new DocumentStore();
        var overview = AmlTools.OpenDocumentText(store, WriteDocumentPointingOutside());

        Assert.Contains("[loaded]", overview);
        Assert.Contains("no problems found", AmlTools.CheckText(store));
        Assert.Contains("class meaning: A motor from an external library.", AmlTools.ElementCard(store, "Plant/Fan"));
    }
}

/// <summary>Change 2: the command line is checked, a typo does not silently widen the fence.</summary>
public sealed class CommandLineTests
{
    [Fact]
    public void An_unknown_argument_is_refused_by_name()
    {
        var ex = Assert.Throws<ArgumentException>(() => ServerOptions.Parse(new[] { "--roots", "C:\\models" }));

        Assert.Contains("Unknown argument '--roots'", ex.Message);
        Assert.Contains("--help", ex.Message);
    }

    [Fact]
    public void A_stray_argument_is_refused_as_well()
    {
        Assert.Throws<ArgumentException>(() => ServerOptions.Parse(new[] { "--strict-conventions", "surprise" }));
        Assert.Throws<ArgumentException>(() => ServerOptions.Parse(new[] { "-r", "." }));
    }

    [Fact]
    public void Root_without_a_directory_is_refused()
    {
        var ex = Assert.Throws<ArgumentException>(() => ServerOptions.Parse(new[] { "--root" }));

        Assert.Equal("--root needs a directory.", ex.Message);
    }

    [Fact]
    public void Help_and_version_are_accepted()
    {
        foreach (var argument in new[] { "--help", "-h", "--version" })
        {
            var options = ServerOptions.Parse(new[] { argument });

            Assert.Empty(options.Roots);
            Assert.False(options.StrictConventions);
        }

        var withRoot = ServerOptions.Parse(new[] { "--help", "--root", ".", "--strict-conventions" });
        Assert.Single(withRoot.Roots);
        Assert.True(withRoot.StrictConventions);
    }
}

/// <summary>Changes 3 and 4: every refusal names the file or the element and says what to do.</summary>
public sealed class ClearRefusalTests : IDisposable
{
    private readonly Workspace _ws = new();
    private readonly DocumentStore _store = new();

    public void Dispose() => _ws.Dispose();

    [Fact]
    public void An_empty_path_asks_for_a_path()
    {
        foreach (var path in new[] { "", "   ", "\t" })
        {
            var ex = Assert.Throws<McpException>(() => AmlTools.OpenDocumentText(_store, path));
            Assert.Contains("A file path is required", ex.Message);
        }

        // A path a client wrapped in quotes is still a path.
        var real = _ws.Write("quoted.aml", Fixtures.Caex215);
        Assert.Contains("CAEX 2.15", AmlTools.OpenDocumentText(_store, $"\"{real}\""));
    }

    [Fact]
    public void A_directory_is_refused_with_advice()
    {
        var ex = Assert.Throws<McpException>(() => AmlTools.OpenDocumentText(_store, _ws.Dir));

        Assert.Contains("is a directory. Name an .aml or .amlx file inside it.", ex.Message);
        Assert.Contains(_ws.Dir, ex.Message);
    }

    [Fact]
    public void A_file_that_vanished_after_being_opened_is_not_answered_from_memory()
    {
        var path = _ws.Write("gone.aml", Fixtures.Caex215);
        AmlTools.OpenDocumentText(_store, path);
        File.Delete(path);

        var ex = Assert.Throws<McpException>(() => AmlTools.CheckText(_store));

        Assert.Contains("was open, but the file is gone now.", ex.Message);
        Assert.Contains(path, ex.Message);
    }

    [Fact]
    public void Broken_XML_is_blamed_on_the_document_not_on_the_server()
    {
        var path = _ws.Write("broken.aml", "<CAEXFile SchemaVersion=\"3.0\" xmlns=\"http://www.dke.de/CAEX\"><InstanceHierarchy Name=\"x\">");

        var ex = Assert.Throws<McpException>(() => AmlTools.OpenDocumentText(_store, path));

        Assert.Contains($"Could not read '{path}' as AutomationML:", ex.Message);
        Assert.DoesNotContain("Internal error", ex.Message);
    }

    [Fact]
    public void An_empty_element_reference_explains_how_to_name_one()
    {
        AmlTools.OpenDocumentText(_store, _ws.Write("rich.aml", Fixtures.Rich30));
        var model = _store.Get(null);

        foreach (var query in new string?[] { null, "", "   " })
        {
            var ex = Assert.Throws<McpException>(() => model.ResolveElement(query));
            Assert.Contains("Name an element", ex.Message);
            Assert.Contains("find_elements", ex.Message);
        }

        Assert.Contains("Name an element", Assert.Throws<McpException>(() => AmlTools.ElementCard(_store, "")).Message);
    }
}

/// <summary>Change 5: an alias whose library is missing does not borrow a foreign class.</summary>
public sealed class AliasFallbackTests : IDisposable
{
    private readonly Workspace _ws = new();
    private readonly DocumentStore _store = new();

    // The document has a library of its own whose plain path is the one the missing
    // external library would answer with. Answering with it would be a wrong answer.
    private const string SameNameLocally = """
        <?xml version="1.0" encoding="utf-8"?>
        <CAEXFile SchemaVersion="3.0" FileName="user.aml" xmlns="http://www.dke.de/CAEX">
          <ExternalReference Path="DoesNotExist.aml" Alias="Motors" />
          <SystemUnitClassLib Name="MotorLib">
            <SystemUnitClass Name="Motor">
              <Description>A local motor that has nothing to do with the external one.</Description>
              <Attribute Name="RatedPower" AttributeDataType="xs:double" Unit="kW"><DefaultValue>99</DefaultValue></Attribute>
            </SystemUnitClass>
          </SystemUnitClassLib>
          <InstanceHierarchy Name="Plant" ID="{66666666-0000-0000-0000-0000000000f1}">
            <InternalElement Name="Fan" ID="{66666666-0000-0000-0000-000000000001}" RefBaseSystemUnitPath="Motors@MotorLib/Motor" />
            <InternalElement Name="Local" ID="{66666666-0000-0000-0000-000000000002}" RefBaseSystemUnitPath="MotorLib/Motor" />
          </InstanceHierarchy>
        </CAEXFile>
        """;

    public AliasFallbackTests()
    {
        AmlTools.OpenDocumentText(_store, _ws.Write("user.aml", SameNameLocally));
    }

    public void Dispose() => _ws.Dispose();

    [Fact]
    public void A_class_behind_a_missing_alias_does_not_fall_back_to_a_local_class_of_the_same_path()
    {
        var fan = AmlTools.ElementCard(_store, "Plant/Fan");

        Assert.Contains("class: Motors@MotorLib/Motor  (NOT resolved)", fan);
        Assert.DoesNotContain("A local motor", fan);
        Assert.DoesNotContain("RatedPower = 99", fan);
        Assert.Null(_store.Get(null).ResolveClass("Motors@MotorLib/Motor"));

        var data = Answer.Data(AmlTools.GetElement(_store, "Plant/Fan"));
        Assert.False(data.GetProperty("classResolved").GetBoolean());
        Assert.False(data.TryGetProperty("classDescription", out _));
    }

    [Fact]
    public void The_local_class_of_the_same_path_still_answers_for_itself()
    {
        var local = AmlTools.ElementCard(_store, "Plant/Local");

        Assert.Contains("class: MotorLib/Motor", local);
        Assert.DoesNotContain("(NOT resolved)", local);
        Assert.Contains("A local motor", local);
        Assert.Contains("RatedPower = 99 kW  (from MotorLib/Motor)", local);
    }

    [Fact]
    public void The_check_blames_the_missing_library_once()
    {
        var check = AmlTools.CheckText(_store);

        Assert.Contains("external library not loaded: Motors -> DoesNotExist.aml", check);
        Assert.Contains("consequence: 1 class path(s) used 1x cannot be resolved, e.g. Motors@MotorLib/Motor", check);
        Assert.Contains("1 problem(s)", check);
    }
}

/// <summary>Change 6, second half: the interface name of a 2.15 link is matched without regard to case.</summary>
public sealed class LegacyLinkCaseTests : IDisposable
{
    private readonly Workspace _ws = new();
    private readonly DocumentStore _store = new();

    public void Dispose() => _ws.Dispose();

    [Fact]
    public void A_legacy_link_resolves_although_owner_and_interface_are_spelled_differently()
    {
        var mixedCase = Fixtures.Caex215
            .Replace("RefPartnerSideA=\"Plant/Tank:Drain\"", "RefPartnerSideA=\"plant/tank:drain\"")
            .Replace("RefPartnerSideB=\"Plant/Valve:In\"", "RefPartnerSideB=\"PLANT/VALVE:IN\"");
        AmlTools.OpenDocumentText(_store, _ws.Write("plant215.aml", mixedCase));

        var check = AmlTools.CheckText(_store);

        Assert.Contains("no problems found", check);
        Assert.Contains("--link DrainPipe", AmlTools.ElementCard(_store, "Plant/Valve"));
        Assert.Contains("Plant/Tank", AmlTools.ElementCard(_store, "Plant/Valve"));
    }
}

/// <summary>Change 12: a document that references itself is read once.</summary>
public sealed class SelfReferenceTests : IDisposable
{
    private readonly Workspace _ws = new();
    private readonly DocumentStore _store = new();

    private const string ReferencesItself = """
        <?xml version="1.0" encoding="utf-8"?>
        <CAEXFile SchemaVersion="3.0" FileName="self.aml" xmlns="http://www.dke.de/CAEX">
          <ExternalReference Path="self.aml" Alias="Self" />
          <SystemUnitClassLib Name="Lib">
            <SystemUnitClass Name="Motor">
              <Description>The one and only motor class.</Description>
            </SystemUnitClass>
          </SystemUnitClassLib>
          <InstanceHierarchy Name="Plant" ID="{77770000-0000-0000-0000-0000000000f1}">
            <InternalElement Name="Fan" ID="{77770000-0000-0000-0000-000000000001}" RefBaseSystemUnitPath="Self@Lib/Motor" />
          </InstanceHierarchy>
        </CAEXFile>
        """;

    public SelfReferenceTests()
    {
        AmlTools.OpenDocumentText(_store, _ws.Write("self.aml", ReferencesItself));
    }

    public void Dispose() => _ws.Dispose();

    [Fact]
    public void The_document_is_parsed_once_and_both_spellings_mean_the_same_class()
    {
        var model = _store.Get(null);

        Assert.True(model.Classes.ContainsKey("Lib/Motor"));
        Assert.True(model.Classes.ContainsKey("Self@Lib/Motor"));
        // A second parse would produce a second, equal-looking XML tree.
        Assert.Same(model.Classes["Lib/Motor"].Element, model.Classes["Self@Lib/Motor"].Element);
        Assert.Same(model.Root.Document, model.Classes["Self@Lib/Motor"].Element.Document);
    }

    [Fact]
    public void The_self_reference_is_reported_as_loaded_and_costs_no_problem()
    {
        Assert.Contains("Self -> self.aml  [loaded]", AmlTools.OpenDocumentText(_store, Path.Combine(_ws.Dir, "self.aml")));
        Assert.Contains("no problems found", AmlTools.CheckText(_store));
        Assert.Contains("class: Self@Lib/Motor", AmlTools.ElementCard(_store, "Plant/Fan"));
        Assert.DoesNotContain("NOT resolved", AmlTools.ElementCard(_store, "Plant/Fan"));
    }
}

/// <summary>Change 13: a library missing deeper in the chain is reported, not swallowed.</summary>
public sealed class NestedLibraryTests : IDisposable
{
    private readonly Workspace _ws = new();
    private readonly DocumentStore _store = new();

    private const string MiddleLibrary = """
        <?xml version="1.0" encoding="utf-8"?>
        <CAEXFile SchemaVersion="3.0" FileName="MiddleLib.aml" xmlns="http://www.dke.de/CAEX">
          <ExternalReference Path="BottomLib.aml" Alias="Bottom" />
          <SystemUnitClassLib Name="MiddleLib">
            <SystemUnitClass Name="Motor" RefBaseClassPath="Bottom@BottomLib/Machine">
              <Description>A motor from the middle library.</Description>
            </SystemUnitClass>
          </SystemUnitClassLib>
        </CAEXFile>
        """;

    private const string TopDocument = """
        <?xml version="1.0" encoding="utf-8"?>
        <CAEXFile SchemaVersion="3.0" FileName="top.aml" xmlns="http://www.dke.de/CAEX">
          <ExternalReference Path="MiddleLib.aml" Alias="Middle" />
          <InstanceHierarchy Name="Plant" ID="{88880000-0000-0000-0000-0000000000f1}">
            <InternalElement Name="Fan" ID="{88880000-0000-0000-0000-000000000001}" RefBaseSystemUnitPath="Middle@MiddleLib/Motor" />
          </InstanceHierarchy>
        </CAEXFile>
        """;

    public NestedLibraryTests()
    {
        _ws.Write("MiddleLib.aml", MiddleLibrary);
        AmlTools.OpenDocumentText(_store, _ws.Write("top.aml", TopDocument));
    }

    public void Dispose() => _ws.Dispose();

    [Fact]
    public void The_library_one_level_down_is_loaded_and_the_one_below_it_is_reported_missing()
    {
        var overview = AmlTools.OpenDocumentText(_store, Path.Combine(_ws.Dir, "top.aml"));
        var check = AmlTools.CheckText(_store);

        // The overview lists what this document declares; the alias Bottom belongs to
        // MiddleLib, not to top.aml, so it is reported by the check instead.
        Assert.Contains("Middle -> MiddleLib.aml  [loaded]", overview);
        Assert.DoesNotContain("Bottom ->", overview);
        Assert.Contains("library of an external library not loaded: Bottom -> BottomLib.aml", check);

        var m = _store.Get(null);
        Assert.True(Assert.Single(m.ExternalRefs).Loaded);
        Assert.Equal("Middle", m.ExternalRefs[0].Alias);
        Assert.False(Assert.Single(m.NestedLibraryProblems).Loaded);
        Assert.Equal("Bottom", m.NestedLibraryProblems[0].Alias);
    }

    [Fact]
    public void The_class_that_was_found_still_answers_and_its_missing_base_class_is_named()
    {
        var fan = AmlTools.ElementCard(_store, "Plant/Fan");
        var card = AmlTools.ClassCard(_store, "Middle@MiddleLib/Motor");

        Assert.Contains("class: Middle@MiddleLib/Motor", fan);
        Assert.Contains("class meaning: A motor from the middle library.", fan);
        Assert.Contains("Bottom@BottomLib/Machine (NOT resolved)", card);
    }
}

/// <summary>
/// A CAEX alias belongs to the document that declares it. A failed reference inside an
/// external library must not make an unrelated class path of the main document unresolvable.
/// </summary>
public sealed class AliasScopeTests : IDisposable
{
    private readonly Workspace _ws = new();
    private readonly DocumentStore _store = new();

    private const string LibraryWithMissingReference = """
        <?xml version="1.0" encoding="utf-8"?>
        <CAEXFile SchemaVersion="3.0" FileName="LibA.aml" xmlns="http://www.dke.de/CAEX">
          <ExternalReference Path="Missing.aml" Alias="Gone" />
          <SystemUnitClassLib Name="LibA">
            <SystemUnitClass Name="Pump" />
          </SystemUnitClassLib>
        </CAEXFile>
        """;

    // The document uses the alias Gone for a library of its own, and that library loads.
    private const string Document = """
        <?xml version="1.0" encoding="utf-8"?>
        <CAEXFile SchemaVersion="3.0" FileName="top.aml" xmlns="http://www.dke.de/CAEX">
          <ExternalReference Path="LibA.aml" Alias="A" />
          <SystemUnitClassLib Name="Local">
            <SystemUnitClass Name="Thing"><Description>A class of this very document.</Description></SystemUnitClass>
          </SystemUnitClassLib>
          <InstanceHierarchy Name="Plant" ID="{99990000-0000-0000-0000-0000000000f1}">
            <InternalElement Name="E" ID="{99990000-0000-0000-0000-000000000001}" RefBaseSystemUnitPath="Gone@Local/Thing" />
          </InstanceHierarchy>
        </CAEXFile>
        """;

    public AliasScopeTests()
    {
        _ws.Write("LibA.aml", LibraryWithMissingReference);
        AmlTools.OpenDocumentText(_store, _ws.Write("top.aml", Document));
    }

    public void Dispose() => _ws.Dispose();

    [Fact]
    public void A_failed_reference_inside_a_library_does_not_block_a_class_of_the_main_document()
    {
        var card = AmlTools.ElementCard(_store, "Plant/E");

        Assert.Contains("class: Gone@Local/Thing", card);
        Assert.DoesNotContain("NOT resolved", card);
        Assert.Contains("class meaning: A class of this very document.", card);
    }

    [Fact]
    public void The_missing_library_is_still_reported_once_and_attributed_to_the_library()
    {
        var check = AmlTools.CheckText(_store);

        Assert.Contains("library of an external library not loaded: Gone -> Missing.aml", check);
        Assert.Single(check.Split("Gone -> Missing.aml").Skip(1));
    }
}

public sealed class RootArgumentTests
{
    [Fact]
    public void An_option_cannot_be_swallowed_as_the_directory_of_root()
    {
        var ex = Assert.Throws<ArgumentException>(() => ServerOptions.Parse(new[] { "--root", "--strict-conventions" }));

        Assert.Contains("--root needs a directory", ex.Message);
    }
}
