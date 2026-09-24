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

/// <summary>CAEX 2.15 documents name base classes without their library.</summary>
public sealed class BareClassNameTests : IDisposable
{
    private readonly Workspace _ws = new();
    private readonly DocumentStore _store = new();

    private const string Document = """
        <?xml version="1.0" encoding="utf-8"?>
        <CAEXFile FileName="bare.aml" SchemaVersion="2.15">
          <RoleClassLib Name="BaseRoles">
            <RoleClass Name="Base">
              <RoleClass Name="Structure" RefBaseClassPath="Base" />
            </RoleClass>
          </RoleClassLib>
          <RoleClassLib Name="OtherRoles">
            <RoleClass Name="Twin" />
          </RoleClassLib>
          <RoleClassLib Name="MoreRoles">
            <RoleClass Name="Twin" />
            <RoleClass Name="UsesTwin" RefBaseClassPath="Twin" />
          </RoleClassLib>
          <RoleClassLib Name="Orphans">
            <RoleClass Name="Lost" RefBaseClassPath="Nowhere" />
          </RoleClassLib>
        </CAEXFile>
        """;

    public BareClassNameTests() => AmlTools.OpenDocumentText(_store, _ws.Write("bare.aml", Document));

    public void Dispose() => _ws.Dispose();

    [Fact]
    public void A_unique_bare_name_resolves()
    {
        var chain = _store.Get(null).ClassChain("BaseRoles/Base/Structure").Select(c => c.Key).ToList();

        Assert.Equal(new[] { "BaseRoles/Base/Structure", "BaseRoles/Base" }, chain);
    }

    [Fact]
    public void A_shared_bare_name_resolves_within_the_library_of_the_class_that_uses_it()
    {
        var chain = _store.Get(null).ClassChain("MoreRoles/UsesTwin").Select(c => c.Key).ToList();

        Assert.Equal(new[] { "MoreRoles/UsesTwin", "MoreRoles/Twin" }, chain);
    }

    [Fact]
    public void A_shared_bare_name_without_context_is_not_guessed()
    {
        Assert.Null(_store.Get(null).ResolveClass("Twin"));
    }

    [Fact]
    public void An_unresolved_class_reference_names_the_class_that_makes_it()
    {
        var check = AmlTools.CheckText(_store);

        Assert.Contains("unresolved class path: Nowhere  (used 1x, e.g. by Orphans/Lost)", check);
        Assert.DoesNotContain("unresolved class path: Base ", check);
        Assert.DoesNotContain("unresolved class path: Twin", check);
    }
}

/// <summary>Library names with '/' in them are written in square brackets, as the OPC UA libraries do.</summary>
public sealed class BracketedClassPathTests : IDisposable
{
    private readonly Workspace _ws = new();
    private readonly DocumentStore _store = new();

    private const string Document = """
        <?xml version="1.0" encoding="utf-8"?>
        <CAEXFile SchemaVersion="3.0" FileName="ua.aml" xmlns="http://www.dke.de/CAEX">
          <AttributeTypeLib Name="ATL_http://opcfoundation.org/UA/">
            <AttributeType Name="NodeId" AttributeDataType="xs:string" />
          </AttributeTypeLib>
          <SystemUnitClassLib Name="SUC_http://opcfoundation.org/UA/">
            <SystemUnitClass Name="FolderType"><Description>An OPC UA folder.</Description></SystemUnitClass>
          </SystemUnitClassLib>
          <InstanceHierarchy Name="Plant" ID="{12340000-0000-0000-0000-0000000000f1}">
            <InternalElement Name="Objects" ID="{12340000-0000-0000-0000-000000000001}" RefBaseSystemUnitPath="[SUC_http://opcfoundation.org/UA/]/[FolderType]">
              <Attribute Name="NodeId" RefAttributeType="[ATL_http://opcfoundation.org/UA/]/[NodeId]"><Value>ns=0;i=85</Value></Attribute>
            </InternalElement>
          </InstanceHierarchy>
        </CAEXFile>
        """;

    public BracketedClassPathTests() => AmlTools.OpenDocumentText(_store, _ws.Write("ua.aml", Document));

    public void Dispose() => _ws.Dispose();

    [Fact]
    public void Bracketed_segments_resolve()
    {
        var card = AmlTools.ElementCard(_store, "Plant/Objects");

        Assert.Contains("class meaning: An OPC UA folder.", card);
        Assert.DoesNotContain("NOT resolved", card);
        Assert.Contains("no problems found", AmlTools.CheckText(_store));
    }

    [Fact]
    public void An_alias_is_only_split_off_outside_the_brackets()
    {
        var (alias, plain, segments) = AmlModel.SplitClassPath("Lib@[ATL_http://user@host/UA/]/[NodeId]");

        Assert.Equal("Lib", alias);
        Assert.Equal("ATL_http://user@host/UA//NodeId", plain);
        Assert.Equal(2, segments);
    }
}

/// <summary>A library behind an http address is not downloaded; that is a note, not a defect.</summary>
public sealed class RemoteLibraryTests : IDisposable
{
    private readonly Workspace _ws = new();
    private readonly DocumentStore _store = new();

    private const string Document = """
        <?xml version="1.0" encoding="utf-8"?>
        <CAEXFile SchemaVersion="3.0" FileName="remote.aml" xmlns="http://www.dke.de/CAEX">
          <ExternalReference Path="https://token123@example.org/share/RemoteLib.aml" Alias="Remote" />
          <InstanceHierarchy Name="Plant" ID="{56780000-0000-0000-0000-0000000000f1}">
            <InternalElement Name="Pump" ID="{56780000-0000-0000-0000-000000000001}" RefBaseSystemUnitPath="Remote@RemoteLib/Pump" />
          </InstanceHierarchy>
        </CAEXFile>
        """;

    private const string Library = """
        <?xml version="1.0" encoding="utf-8"?>
        <CAEXFile SchemaVersion="3.0" FileName="RemoteLib.aml" xmlns="http://www.dke.de/CAEX">
          <SystemUnitClassLib Name="RemoteLib">
            <SystemUnitClass Name="Pump"><Description>A pump from the remote library.</Description></SystemUnitClass>
          </SystemUnitClassLib>
        </CAEXFile>
        """;

    public void Dispose() => _ws.Dispose();

    [Fact]
    public void Without_a_local_copy_it_is_a_note_and_the_token_is_not_shown()
    {
        var overview = AmlTools.OpenDocumentText(_store, _ws.Write("remote.aml", Document));
        var check = AmlTools.CheckText(_store);

        Assert.Contains("no problems found", check);
        Assert.Contains("note: remote library not downloaded: Remote -> https://example.org/share/RemoteLib.aml", check);
        Assert.Contains("1 class path(s) used 1x from it are not checked", check);
        Assert.DoesNotContain("token123", overview + check);
    }

    [Fact]
    public void A_local_copy_next_to_the_document_is_used()
    {
        _ws.Write("RemoteLib.aml", Library);
        AmlTools.OpenDocumentText(_store, _ws.Write("remote.aml", Document));

        Assert.Contains("class meaning: A pump from the remote library.", AmlTools.ElementCard(_store, "Plant/Pump"));
        Assert.Contains("was read from the local copy", AmlTools.CheckText(_store));
    }
}

/// <summary>With --follow the server answers about the document an editor currently shows.</summary>
public sealed class FollowTheEditorTests : IDisposable
{
    private readonly Workspace _ws = new();

    public void Dispose() => _ws.Dispose();

    private DocumentStore StoreFollowing(string pointer, bool restricted = true)
    {
        var arguments = restricted
            ? new[] { "--root", _ws.Dir, "--follow", pointer }
            : new[] { "--follow", pointer };
        return new DocumentStore(ServerOptions.Parse(arguments));
    }

    [Fact]
    public void Without_an_open_document_the_pointed_at_file_is_used()
    {
        var plant = _ws.Write("plant.aml", Fixtures.Caex215);
        var pointer = _ws.Write("open-document.txt", plant);
        var store = StoreFollowing(pointer);

        Assert.Contains("Plant/Pump", AmlTools.ElementCard(store, "Pump"));
    }

    [Fact]
    public void A_document_the_editor_switches_to_is_picked_up_without_a_restart()
    {
        var first = _ws.Write("first.aml", Fixtures.Caex215);
        var second = _ws.Write("second.aml", Fixtures.Rich30);
        var pointer = _ws.Write("open-document.txt", first);
        var store = StoreFollowing(pointer);

        Assert.Contains("Reference check for first.aml", AmlTools.CheckText(store));

        File.WriteAllText(pointer, second);

        Assert.Contains("Reference check for second.aml", AmlTools.CheckText(store));
    }

    [Fact]
    public void A_document_the_client_opened_itself_still_wins_until_the_editor_moves_on()
    {
        var followed = _ws.Write("followed.aml", Fixtures.Caex215);
        var chosen = _ws.Write("chosen.aml", Fixtures.Rich30);
        var pointer = _ws.Write("open-document.txt", followed);
        var store = StoreFollowing(pointer);

        AmlTools.OpenDocumentText(store, chosen);

        Assert.Contains("Reference check for chosen.aml", AmlTools.CheckText(store));
    }

    [Fact]
    public void A_pointer_outside_the_roots_or_a_broken_one_is_ignored()
    {
        using var outside = new Workspace();
        var secret = outside.Write("secret.aml", Fixtures.Caex215);
        var pointer = _ws.Write("open-document.txt", secret);
        var store = StoreFollowing(pointer);

        Assert.Contains("outside the directories this server may read",
            Assert.Throws<McpException>(() => AmlTools.CheckText(store)).Message);

        File.WriteAllText(pointer, "this is not a path");
        Assert.Contains("which does not exist", Assert.Throws<McpException>(() => AmlTools.CheckText(store)).Message);
    }

    [Fact]
    public void The_option_needs_a_file()
    {
        Assert.Contains("--follow needs a file", Assert.Throws<ArgumentException>(() => ServerOptions.Parse(new[] { "--follow" })).Message);
        Assert.Equal(Path.GetFullPath("pointer.txt"), ServerOptions.Parse(new[] { "--follow", "pointer.txt" }).FollowFile);
    }
}

/// <summary>The client may ask for the overview without naming a file at all.</summary>
public sealed class OpenWithoutAPathTests : IDisposable
{
    private readonly Workspace _ws = new();

    public void Dispose() => _ws.Dispose();

    [Fact]
    public void Omitting_the_path_reads_the_document_the_editor_points_at()
    {
        var plant = _ws.Write("plant.aml", Fixtures.Caex215);
        var pointer = _ws.Write("open-document.txt", plant);
        var store = new DocumentStore(ServerOptions.Parse(new[] { "--root", _ws.Dir, "--follow", pointer }));

        var text = Answer.Text(AmlTools.OpenAmlDocument(store));

        Assert.Contains("Document: plant.aml", text);
        Assert.Contains("CAEX 2.15", text);
    }

    [Fact]
    public void Without_a_pointer_the_refusal_says_what_to_do()
    {
        var store = new DocumentStore();

        var ex = Assert.Throws<McpException>(() => AmlTools.OpenAmlDocument(store));

        Assert.Contains("call open_aml_document with the path", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class FollowedDocumentOutsideTheRootTests : IDisposable
{
    private readonly Workspace _inside = new();
    private readonly Workspace _outside = new();

    public void Dispose()
    {
        _inside.Dispose();
        _outside.Dispose();
    }

    [Fact]
    public void The_refusal_names_the_file_and_the_fence()
    {
        var elsewhere = _outside.Write("plant.aml", Fixtures.Caex215);
        var pointer = _inside.Write("open-document.txt", elsewhere);
        var store = new DocumentStore(ServerOptions.Parse(new[] { "--root", _inside.Dir, "--follow", pointer }));

        var ex = Assert.Throws<McpException>(() => AmlTools.OpenAmlDocument(store));

        Assert.Contains("the editor has", ex.Message);
        Assert.Contains(elsewhere, ex.Message);
        Assert.Contains("outside the directories this server may read", ex.Message);
    }
}

/// <summary>
/// Every tool states all four hints. Without them a client has to assume the defaults of the
/// specification, which are destructive and open world, and would warn about a server that reads.
/// </summary>
public sealed class ToolAnnotationTests
{
    [Fact]
    public void All_tools_declare_that_they_only_read_a_local_document()
    {
        var tools = typeof(AmlTools).GetMethods()
            .Select(m => m.GetCustomAttributes(typeof(ModelContextProtocol.Server.McpServerToolAttribute), false)
                .Cast<ModelContextProtocol.Server.McpServerToolAttribute>().FirstOrDefault())
            .Where(a => a is not null)
            .ToList();

        Assert.Equal(10, tools.Count);
        Assert.All(tools, tool =>
        {
            Assert.True(tool!.Idempotent);
            Assert.False(tool.Destructive);
            Assert.False(tool.OpenWorld);
        });

        // Nine read. show_in_editor writes a file for the editor, and says so.
        Assert.Equal(9, tools.Count(t => t!.ReadOnly));
        Assert.Equal("show_in_editor", Assert.Single(tools.Where(t => !t!.ReadOnly))!.Name);
    }
}

/// <summary>An assistant can point the editor at an element instead of leaving the user to search.</summary>
public sealed class ShowInEditorTests : IDisposable
{
    private readonly Workspace _ws = new();

    public void Dispose() => _ws.Dispose();

    private DocumentStore Store(string? selectFile)
    {
        var arguments = selectFile is null
            ? new[] { "--root", _ws.Dir }
            : new[] { "--root", _ws.Dir, "--select", selectFile };
        var store = new DocumentStore(ServerOptions.Parse(arguments));
        AmlTools.OpenDocumentText(store, _ws.Write("rich.aml", Fixtures.Rich30));
        return store;
    }

    [Fact]
    public void The_id_of_the_element_is_written_where_the_editor_watches()
    {
        var selectFile = Path.Combine(_ws.Dir, "show-in-editor.txt");
        var store = Store(selectFile);

        var result = AmlTools.ShowInEditor(store, "Station1");
        var data = Answer.Data(result);

        var id = File.ReadAllText(selectFile).Trim();
        Assert.Equal(data.GetProperty("id").GetString(), id);
        Assert.Contains("Showing", Answer.Text(result));
        Assert.Contains(data.GetProperty("path").GetString()!, Answer.Text(result));
    }

    [Fact]
    public void Without_an_editor_the_call_says_what_is_missing()
    {
        var ex = Assert.Throws<McpException>(() => AmlTools.ShowInEditor(Store(null), "Station1"));

        Assert.Contains("No editor is attached", ex.Message);
        Assert.Contains("--select", ex.Message);
    }

    [Fact]
    public void An_unknown_element_is_refused_before_anything_is_written()
    {
        var selectFile = Path.Combine(_ws.Dir, "show-in-editor.txt");
        var store = Store(selectFile);

        Assert.Throws<McpException>(() => AmlTools.ShowInEditor(store, "NoSuchElement"));

        Assert.False(File.Exists(selectFile));
    }
}
